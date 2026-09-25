"""HTTP API wrapper for PaciolanEvenueClient - same contract shape as
broadwaydirect/api.py and stubhub/api.py (see those for the pattern this
follows), but Mongo persistence writes the REAL .NET Rowing shape
(mongo_inventory.py: one SHARED collection per datasource, keyed by
SourceEventId, one document per listing, PascalCase fields) - NOT
broadwaydirect's own raw_events/cleaned_events shape. See
mongo_inventory.py's module docstring for exactly which C# source
confirms this (SettingFactory.GetIntegrationNewInventoryCollectionName,
IntegrationCrawlerSessionForm.SaveInventoryAsync) and for a correction
noted there: an earlier pass of this file used a per-event collection
name, which was wrong.

Run:
    pip install -r requirements.txt
    python3 -m patchright install chromium      # first run only
    python3 -m uvicorn paciolanevenue.api:app --port 8200

Try it (same request shape as stubhub/api.py - eventId + url, cross-checked
against each other):
    curl -X POST http://localhost:8200/api/seatavailability \\
      -H "Content-Type: application/json" \\
      -d '{"eventId":"F06","url":"https://purduesports.evenue.net/event/F26/F06"}'
"""

import asyncio
import os
import sys
from typing import Optional

from fastapi import FastAPI
from fastapi.responses import JSONResponse
from pydantic import BaseModel

from broadwaydirect.proxy_pool import ProxyPool, normalize_proxy

from .client import PaciolanEvenueClient, PerimeterXBlocked
from .parser import NotAnEventPage, parse_event_url
from .grouping import build_listings
from . import mongo_inventory

app = FastAPI(title="Paciolan eVenue Fetch API")


_PROXY_POOL: Optional[ProxyPool] = None
_proxy_list_path = os.environ.get("PACIOLAN_PROXY_LIST_PATH")
if _proxy_list_path:
    try:
        _PROXY_POOL = ProxyPool(_proxy_list_path)
        print(f"  loaded {len(_PROXY_POOL)} proxies from {_proxy_list_path}", file=sys.stderr)
    except Exception as e:
        print(f"  !! could not load PACIOLAN_PROXY_LIST_PATH={_proxy_list_path}: {e}", file=sys.stderr)


def _env_proxy_template() -> Optional[str]:
    """A single {SESSIONID}-templated proxy from PACIOLAN_PROXY_* env vars
    (matches ProxyConfig.cs in the .NET demo) - checked after a
    request-supplied proxy and before the round-robin pool. Falls back to
    the gitignored _local_proxy.py (see that file) if no env var is set."""
    host = os.environ.get("PACIOLAN_PROXY_HOST")
    port = os.environ.get("PACIOLAN_PROXY_PORT")
    user = os.environ.get("PACIOLAN_PROXY_USER", "")
    pw = os.environ.get("PACIOLAN_PROXY_PASS", "")
    if host and port:
        return f"http://{user}:{pw}@{host}:{port}"
    try:
        from ._local_proxy import PROXY_URL
        return PROXY_URL
    except ImportError:
        return None


def _pick_proxy(requested: Optional[str]) -> Optional[str]:
    if requested:
        return normalize_proxy(requested)
    env_proxy = _env_proxy_template()
    if env_proxy:
        return env_proxy
    return _PROXY_POOL.next() if _PROXY_POOL else None


# One long-lived client per (host, proxy), each behind its own lock - same idea as
# broadwaydirect/api.py's client pool. Keeps the PerimeterX-cleared browser between
# requests so the 2nd+ event on a host takes the warm-page fast path (~4.5s instead of a
# new browser + a new PerimeterX challenge per request; ../EVENUE_OPTIMIZATION_FINDINGS.md).
# A client whose page got blocked reopens a fresh proxy session by itself (client.py).
_clients: dict = {}


def _client_for(host: str, proxy: Optional[str]) -> tuple:
    key = (host, proxy or "")
    if key not in _clients:
        _clients[key] = (PaciolanEvenueClient(proxy=proxy), asyncio.Lock())
    return _clients[key]


@app.on_event("shutdown")
async def _shutdown():
    await asyncio.gather(*(c.close() for c, _ in _clients.values()), return_exceptions=True)


class SeatAvailabilityRequest(BaseModel):
    """Same shape as stubhub/api.py's EventInventoryRequest ({eventId, url,
    proxy?}) - eventId is eVenue's itemCd (its closest equivalent to a bare
    numeric event id), cross-checked against what's embedded in `url`, same
    as StubHub's own id-mismatch check."""
    eventId: str
    url: str
    proxy: Optional[str] = None


def _price_level_dict(pl) -> dict:
    return {
        "pl": pl.pl, "pl_desc": pl.pl_desc, "pt": pl.pt, "pt_desc": pl.pt_desc,
        "price": pl.price, "per_ticket_fee": pl.per_ticket_fee, "facility_fee": pl.facility_fee,
    }


def _listing_dict(l) -> dict:
    return {
        "level": l.level, "section": l.section, "row": l.row,
        "price_level_cd": l.price_level_cd, "seating_type": l.seating_type, "quantity": l.quantity,
        "seat_range": l.seat_range_label, "seat_keys": l.seat_keys,
        "seating_type_cd": l.seating_type_cd, "seat_statuses": l.seat_statuses,
    }


def _mirror_to_mongo(event, price_levels: list, listings: list) -> None:
    """Best-effort - a Mongo failure never fails the HTTP response, same
    policy as broadwaydirect/stubhub's own _mirror_to_mongo. See
    mongo_inventory.py for the actual document shape."""
    mongo_uri = os.environ.get("MONGO_URI", "mongodb://localhost:27017")
    mongo_db = os.environ.get("MONGO_DB", "broadwaydirect")
    try:
        n = mongo_inventory.write_inventory(mongo_uri, mongo_db, event, listings, price_levels)
        print(f"  wrote {n} listing(s) to {mongo_inventory.collection_name(event)}", file=sys.stderr)
    except Exception as e:
        print(f"  !! failed to mirror event {event.item_cd} to MongoDB: {e}", file=sys.stderr)


@app.post("/api/seatavailability")
async def seat_availability(req: SeatAvailabilityRequest):
    if not req.eventId or not req.url:
        return JSONResponse(status_code=400, content={"error": "eventId and url are required"})

    parsed_url = parse_event_url(req.url)
    if not parsed_url:
        return JSONResponse(status_code=400, content={
            "error": "url must look like https://{host}/event/{seasonCd}/{itemCd}"})
    host, season_cd, item_cd = parsed_url
    if item_cd != req.eventId:
        return JSONResponse(status_code=400, content={
            "error": f"eventId mismatch: url has {item_cd!r}, body has {req.eventId!r}"})

    try:
        proxy = _pick_proxy(req.proxy)
    except ValueError as e:
        return JSONResponse(status_code=400, content={"error": f"invalid proxy: {e}"})

    client, lock = _client_for(host, proxy)
    async with lock:
        try:
            event, price_levels = await client.get_event(host, season_cd, item_cd)
        except NotAnEventPage as e:
            return JSONResponse(status_code=404, content={"error": f"{item_cd} is not a real event: {e}"})
        except PerimeterXBlocked as e:
            return JSONResponse(status_code=502, content={"error": f"blocked by PerimeterX: {e}"})

        try:
            seats, columns, coverage = await client.get_seat_availability(event)
        except PerimeterXBlocked as e:
            return JSONResponse(status_code=502, content={"error": f"blocked by PerimeterX on the API call: {e}"})
        map_note = await client.get_event_map(event)

    listings = build_listings(seats, event, price_levels)
    coverage = f"{coverage} {map_note} listings={len(listings)} tickets={sum(l.quantity for l in listings)} " \
               f"ga_tickets={sum(l.quantity for l in listings if l.seating_type_cd == 'G')}"
    _mirror_to_mongo(event, price_levels, listings)

    return JSONResponse(content={
        "event": {
            "host": event.host, "seasonCd": event.season_cd, "itemCd": event.item_cd,
            "eventName": event.event_name, "facilityTitle": event.facility_title,
            "eventDtUtc": event.event_dt_utc, "hideTime": event.hide_time,
            "soldOut": event.sold_out,
        },
        "coverage": coverage,
        "price_levels": [_price_level_dict(pl) for pl in price_levels],
        "listings": [_listing_dict(l) for l in listings],
    })
