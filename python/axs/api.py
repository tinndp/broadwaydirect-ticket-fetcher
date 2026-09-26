"""HTTP API for AxsClient - same contract as broadwaydirect/api.py (the
standard): POST /api/eventinventory {eventId, url, proxy?}.

Response:
    {"eventId", "event": {...}, "raw": {"price","sections","offer_search"} or {"mp_offers"} (+ mp_eventinfo/mp_mapinfo only if they arrived first),
     "price_levels": [...], "listings": [...], "coverage": "...", "notes": [...]}
"raw" is the commerce API JSON exactly as the AXS ticket app received it.

Mongo mirror is best-effort (a Mongo failure never fails the response, same
policy as broadwaydirect/paciolanevenue) and writes the Rowing shape - see
mongo_inventory.py for why that differs from broadwaydirect's.

Configuration (env, all optional):
    MONGO_URI (default mongodb://localhost:27017), MONGO_DB (default "broadwaydirect")
    AXS_PROXY_HOST/PORT/USER/PASS   single proxy, USER may contain {SESSIONID}
    AXS_PROXY_LIST_PATH             round-robin pool file (host:port:user:pass per line)
    AXS_OUTPUT_DIR                  raw JSON dir (default output/axs)

Run (from python/):
    python3 -m uvicorn axs.api:app --port 8300
    curl -X POST http://localhost:8300/api/eventinventory -H "Content-Type: application/json" \\
      -d '{"eventId":"1457730","url":"https://www.axs.com/events/1457730/tom-jones-21-event-tickets"}'

Requests are served ONE AT A TIME per proxy (a lock per pooled client): the
AXS commerce API rate-limits hard (docs/RECON.md section 4), so parallel loads on
one IP only produce 429s.
"""

import asyncio
import dataclasses
import os
import sys
import time
from typing import Optional

from fastapi import FastAPI
from fastapi.responses import JSONResponse
from pydantic import BaseModel

from shared.proxy_pool import ProxyPool, normalize_proxy

from .client import AxsClient, AxsBlocked, NotOnSale
from .parser import NotAnEventPage, parse_event_url
from .grouping import build_result
from .cli import env_proxy
from . import mongo_inventory, output

app = FastAPI(title="AXS Fetch API")
OUTPUT_DIR = os.environ.get("AXS_OUTPUT_DIR", "output/axs")

_PROXY_POOL: Optional[ProxyPool] = None
_pool_path = os.environ.get("AXS_PROXY_LIST_PATH")
if _pool_path:
    try:
        _PROXY_POOL = ProxyPool(_pool_path)
        print(f"  loaded {len(_PROXY_POOL)} proxies from {_pool_path}", file=sys.stderr)
    except Exception as e:
        print(f"  !! could not load AXS_PROXY_LIST_PATH={_pool_path}: {e}", file=sys.stderr)


def _pick_proxy(requested: Optional[str]) -> Optional[str]:
    """request `proxy` > AXS_PROXY_* env > AXS_PROXY_LIST_PATH pool > none."""
    if requested:
        return normalize_proxy(requested)
    return env_proxy() or (_PROXY_POOL.next() if _PROXY_POOL else None)


# One warmed browser per proxy (keeps Cloudflare clearance across requests,
# like broadwaydirect/api.py's client pool), each behind its own lock.
_clients: dict = {}
_locks: dict = {}


def _client_for(proxy: Optional[str]) -> tuple:
    key = proxy or ""
    if key not in _clients:
        _clients[key] = AxsClient(proxy=proxy, retries=int(os.environ.get("AXS_RETRIES", "4")),
                                  on_raw=output.raw_writer(OUTPUT_DIR))
        _locks[key] = asyncio.Lock()
    return _clients[key], _locks[key]


class EventInventoryRequest(BaseModel):
    eventId: str
    url: str
    proxy: Optional[str] = None


def inventory_fetched(inv: dict) -> bool:
    """True when the ticket app's inventory was actually captured (either
    flow) - then Mongo must be rewritten EVEN WITH 0 listings, so a sold-out
    event doesn't keep stale listings. False when nothing was fetched
    (not on sale / no flow): Mongo is left untouched."""
    return inv.get("mp_offers") is not None or inv.get("offer_search") is not None or inv.get("price") is not None


def _mirror_to_mongo(event, listings: list) -> None:
    uri = os.environ.get("MONGO_URI", "mongodb://localhost:27017")
    db = os.environ.get("MONGO_DB", "broadwaydirect")
    try:
        n = mongo_inventory.write_inventory(uri, db, event, listings)
        print(f"  wrote {n} listing(s) to {mongo_inventory.COLLECTION}", file=sys.stderr)
    except Exception as e:
        print(f"  !! failed to mirror event {event.event_id} to MongoDB: {e}", file=sys.stderr)


@app.post("/api/eventinventory")
async def event_inventory(req: EventInventoryRequest):
    if not req.eventId or not req.url:
        return JSONResponse(status_code=400, content={"error": "eventId and url are required"})
    parsed = parse_event_url(req.url)
    if not parsed:
        return JSONResponse(status_code=400, content={"error": "url must look like https://www.axs.com/events/{eventId}/{slug}"})
    if str(parsed[0]) != req.eventId.strip():
        return JSONResponse(status_code=400, content={"error": f"eventId mismatch: url has {parsed[0]}, body has {req.eventId!r}"})
    try:
        proxy = _pick_proxy(req.proxy)
    except ValueError as e:
        return JSONResponse(status_code=400, content={"error": f"invalid proxy: {e}"})

    client, lock = _client_for(proxy)
    t0 = time.monotonic()
    async with lock:
        try:
            event = await client.get_event(req.url)
        except NotAnEventPage as e:
            return JSONResponse(status_code=404, content={"error": f"{req.eventId} is not an event page: {e}"})
        except AxsBlocked as e:
            return JSONResponse(status_code=502, content={"error": f"blocked: {e}"})
        try:
            inv = await client.get_inventory(event)
        except NotOnSale as e:
            inv = {"notes": [f"not on sale: {e}"]}
        except AxsBlocked as e:
            return JSONResponse(status_code=502, content={"error": f"blocked on the ticket page: {e}"})

    res = build_result(event, inv)
    res["coverage"] += f" elapsed={time.monotonic() - t0:.0f}s"
    output.write_result(OUTPUT_DIR, res)
    if inventory_fetched(inv):
        _mirror_to_mongo(event, res["listings"])  # 0 listings -> clears this event's stale docs

    body = output.to_jsonable(res)
    return JSONResponse(content={
        "eventId": req.eventId,
        "event": body["event"],
        # every captured inventory response except "phase" (it echoes the caller's IP)
        "raw": {k: v for k, v in inv.items() if k not in ("phase", "notes") and v is not None},
        "price_levels": body["price_levels"],
        "listings": body["listings"],
        "coverage": body["coverage"],
        "notes": body["notes"],
    })


@app.on_event("shutdown")
async def _shutdown():
    await asyncio.gather(*(c.close() for c in _clients.values()), return_exceptions=True)
