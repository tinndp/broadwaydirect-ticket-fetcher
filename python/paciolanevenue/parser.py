"""Reads the event page's own `__NEXT_DATA__` script tag into an Event +
list[PriceLevel] - port of the .NET demo's EventPageParser.cs (see
ETECH.Application.MarkAutomation/ETECH.Application/Rowing/PaciolanEvenue/
PaciolanEvenueCrawler/EventPageParser.cs), same field names/behavior.
Nothing here is hard-coded per school - every id comes from the page.
"""

import json
import re
from typing import Optional

from .models import Event, PriceLevel

_NEXT_DATA_RE = re.compile(
    r'<script id="__NEXT_DATA__"[^>]*>(?P<json>.*?)</script>', re.DOTALL
)


class NotAnEventPage(Exception):
    """Raised when the page doesn't render a single real event -
    context != "eventdetailpage" (e.g. a 404/catch-all page) even though
    the HTTP status was 200. Distinct from a PerimeterX block (see
    client.py's own block-marker check, which runs BEFORE this parses)."""


def parse_event_page(html: str, host: str, season_cd: str, item_cd: str) -> tuple[Event, list[PriceLevel]]:
    m = _NEXT_DATA_RE.search(html)
    if not m:
        raise NotAnEventPage("no __NEXT_DATA__ script tag found")
    try:
        next_data = json.loads(m.group("json"))
    except json.JSONDecodeError as e:
        raise NotAnEventPage(f"__NEXT_DATA__ did not parse as JSON: {e}")

    page_props = (next_data.get("props") or {}).get("pageProps") or {}
    context = page_props.get("context")
    props = ((page_props.get("component") or {}).get("props")) or {}

    # "eventdetailpage" confirmed on both Purdue and Oklahoma event pages
    # during recon - anything else means this itemCd is not a real event.
    if context != "eventdetailpage" or not props:
        raise NotAnEventPage(f"page context={context!r}, not a single event page")

    ssr = (props.get("ssrData") or {}).get("discovery_eventDetailMPT") or []
    if not ssr:
        raise NotAnEventPage("no discovery_eventDetailMPT row in ssrData")
    ev_raw = ssr[0]

    distributor_id = str(props.get("distributorId") or "")
    policy_cd = ev_raw.get("POLICYCD") or f"{distributor_id}:DEFAULT:I"
    policy_type = ev_raw.get("POLICYTYPE") or "I"

    event = Event(
        host=host,
        season_cd=season_cd,
        item_cd=item_cd,
        data_account_id=str(props.get("dataAccountId") or ""),
        distributor_id=distributor_id,
        policy_cd=policy_cd,
        policy_type=policy_type,
        event_name=ev_raw.get("EVENTNAME") or ev_raw.get("ITEMNAME") or "",
        facility_title=ev_raw.get("FAC_TITLE") or "",
        event_dt_utc=ev_raw.get("EVENTDT") or None,
        event_dt_fac_raw=ev_raw.get("EVENTDTFAC") or None,
        hide_time=bool(ev_raw.get("HIDE_TIME")),
        hide_date_time=bool(ev_raw.get("HIDE_DATE_TIME")),
        sold_out=bool(ev_raw.get("SOLD_OUT")),
        total_capacity_ssr=ev_raw.get("TOTALCAPACITY"),
        available_ssr=ev_raw.get("AVAILABLE"),
        min_qty=ev_raw.get("MINQTY"),
        max_qty=ev_raw.get("MAXQTY"),
        multiple_qty=ev_raw.get("MULTIPLEQTY"),
        fac_cd=str(ev_raw.get("FAC_CD") or ""),
        configuration_cd=str(ev_raw.get("CONFIGURATIONCD") or ""),
        base_map_id=str(props.get("baseMapId") or ""),
        allow_seat_map=ev_raw.get("ALLOWSEATMAP"),
    )

    price_levels = []
    for pl in ev_raw.get("PL_PT_PRICES") or []:
        price_levels.append(PriceLevel(
            pl=str(pl.get("PL") or ""),
            pl_desc=pl.get("PL_DESC") or "",
            pt=str(pl.get("PT") or ""),
            pt_desc=pl.get("PT_DESC") or "",
            price=pl.get("PRICE") or 0,
            per_ticket_fee=pl.get("PER_TICKET_FEE") or 0,
            facility_fee=pl.get("FACILITY_FEE") or 0,
            plpt_min_qty=pl.get("PLPT_MINQTY"),
            plpt_max_qty=pl.get("PLPT_MAXQTY"),
            plpt_multiple=pl.get("PLPT_MULTIPLE"),
        ))

    return event, price_levels


_EVENT_URL_RE = re.compile(r"^https?://(?P<host>[^/]+)/event/(?P<season>[^/]+)/(?P<item>[^/?#]+)")


def parse_event_url(url: str):
    """"https://purduesports.evenue.net/event/F26/F06[/...]" ->
    (host, seasonCd, itemCd), or None if the url doesn't match that shape -
    same idea as stubhub/extract.py's event_id_from_url, adapted since
    eVenue's "event id" is really the (host, season, item) triple, not one
    bare numeric id."""
    m = _EVENT_URL_RE.match(url or "")
    if not m:
        return None
    return m.group("host"), m.group("season"), m.group("item")


def seat_availability_path(event: Event) -> str:
    """Builds the seat-availability API path for `event` - see README.md /
    RECON.md section 3.1. "A|S" (available+sold) is what evenue.net's own
    front-end used during recon; "A" alone did not reduce the row count."""
    event_id = f"{event.data_account_id}:{event.season_cd}:{event.item_cd}"
    return (
        f"/pac-api/seat-availability/event-id/{event_id}/seats"
        f"?distributorId={event.distributor_id}"
        f"&availability=A%7CS"
        f"&policyCd={event.policy_cd}"
        f"&policyType={event.policy_type}"
    )


EVENT_MAP_GQL_PATH = "/pac-api/consumer/gql"


def _gql_str(v) -> str:
    return json.dumps(str(v or ""))


def event_map_query(event: Event) -> dict:
    """GraphQL body for maps_eventMap {HOLDCODES, SEATING_TYPES} - the same query (same
    arguments) evenue.net's own map component sends on a seat-map event page (captured
    2026-09-25, purduesports F26/F04). Quantity-only pages don't send it themselves, but the
    endpoint answers it for them too (verified on 62 events / 11 hosts)."""
    args = (
        f"dataAccountId: {_gql_str(event.data_account_id)}, seasonCd: {_gql_str(event.season_cd)}, "
        f"facilityCd: {_gql_str(event.fac_cd)}, itemCd: {_gql_str(event.item_cd)}, "
        f"configCd: {_gql_str(event.configuration_cd)}, availability: \"A|S\", "
        f"policyCd: {_gql_str(event.policy_cd)}, policyType: {_gql_str(event.policy_type)}, "
        f"distributorId: {_gql_str(event.distributor_id)}, baseMapId: {_gql_str(event.base_map_id)}"
    )
    return {"query": "query { maps_eventMap(" + args + ") { SEATING_TYPES { pl seatingType } "
                     "HOLDCODES { holdcode title message type } } }"}


def parse_event_map(body: dict) -> tuple:
    """maps_eventMap response -> (hold_codes {code: type}, seating_types {pl: "R"|"G"}).
    Raises ValueError when the response has no maps_eventMap object."""
    data = ((body or {}).get("data") or {}).get("maps_eventMap")
    if not isinstance(data, dict):
        raise ValueError(f"no maps_eventMap in response: {str(body)[:200]}")
    hold_codes = {str(h.get("holdcode")): h.get("type") or "" for h in data.get("HOLDCODES") or []}
    seating_types = {str(x.get("pl")): x.get("seatingType") or "" for x in data.get("SEATING_TYPES") or []}
    return hold_codes, seating_types
