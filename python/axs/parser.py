"""Pure parsing - no browser, no network. Turns AXS JSON (event page
__NEXT_DATA__ / _next/data, and the Veritix commerce API responses captured
by client.py) into models.py objects. Kept separate from client.py (same
split as paciolanevenue/parser.py) so it can be tested against saved raw
JSON and ported 1:1 to AXS.Core in the .NET step."""

import re
from typing import Optional

from .models import Event, PriceLevel, Seat

EVENT_URL_RE = re.compile(r"https?://(?:www\.)?axs\.com/(?:[a-z]{2}(?:-[a-z]{2})?/)?events/(\d+)(?:/([^/?#]+))?", re.I)


class NotAnEventPage(Exception):
    """The page/JSON has no discoveryEventData - wrong id, removed event,
    or a redirect (festival/series page). Not a block - retrying won't help."""


def parse_event_url(url: str) -> Optional[tuple]:
    """"https://www.axs.com/events/1457730/tom-jones-21-event-tickets" -> (1457730, "tom-jones-21-event-tickets")."""
    m = EVENT_URL_RE.match(url or "")
    if not m:
        return None
    return int(m.group(1)), (m.group(2) or "")


def _utc(s: Optional[str]) -> Optional[str]:
    """AXS's *UTC fields are real UTC but have no "Z" (verified across DST in
    docs/RECON.md) - add it so every consumer parses them as UTC, never local."""
    if not s:
        return None
    return s if s.endswith("Z") or "+" in s[10:] else s + "Z"


def page_props(next_data: dict) -> dict:
    """Accepts either the full __NEXT_DATA__ ({props:{pageProps}}) or a
    /_next/data/... response ({pageProps}) - same payload, two wrappers."""
    if "props" in next_data:
        return next_data["props"].get("pageProps") or {}
    return next_data.get("pageProps") or {}


def parse_event(next_data: dict) -> Event:
    pp = page_props(next_data)
    d = pp.get("discoveryEventData")
    if not d or not d.get("eventId"):
        raise NotAnEventPage(f"no discoveryEventData (redirect={pp.get('__N_REDIRECT')!r})")
    venue = d.get("venue") or {}
    title = d.get("title") or {}
    ticketing = d.get("ticketing") or {}
    # ticketingEventData is a sibling copy of discoveryEventData.ticketing. The
    # link is only present in the HTML page's __NEXT_DATA__ - the /_next/data/
    # route strips url/ticketURL entirely - so ticket_url is None there.
    # Seen both as shop.axs.com/?c=axs&e=... and a direct tix.axs.com/{token} link.
    t2 = pp.get("ticketingEventData") or {}
    ticket_url = ticketing.get("url") or ticketing.get("ticketURL") or t2.get("url") or t2.get("ticketURL")
    assoc = d.get("associations") or {}
    return Event(
        event_id=int(d["eventId"]),
        slug=d.get("urlSlug") or "",
        name=title.get("eventTitleText") or title.get("headlinersText") or "",
        venue_id=venue.get("venueId"),
        venue_name=venue.get("title") or "",
        venue_city=venue.get("city") or "",
        venue_state=venue.get("state") or "",
        performer_ids=[str(p) for p in (assoc.get("performerIds") or [])],
        event_dt_local=d.get("eventDatetime"),
        event_dt_utc=_utc(d.get("eventDatetimeUTC")),
        event_tz=d.get("eventDatetimeTz") or d.get("eventDatetimeZone") or venue.get("timeZone") or "",
        door_dt_utc=_utc(d.get("doorDatetimeUTC")),
        onsale_dt_utc=_utc(d.get("onsaleDatetimeUTC")),
        status_id=ticketing.get("statusId", t2.get("statusId")),
        status=ticketing.get("status") or t2.get("status") or "",
        ticket_url=ticket_url,
        publish_status=d.get("publishStatus"),
    )


def is_resale_offer(offer_id) -> bool:
    """AXS Official Resale (FLASHSEATS) offers have 16-digit ids starting
    9000000... (docs/RECON.md 3b); primary offers are 8-digit."""
    return str(offer_id or "").startswith("9000000")


def parse_price(price_json: dict) -> list:
    """inventory/v4/{token}/price -> list[PriceLevel]. One entry per
    (offer, price level, price type). Resale offers have no labels, just an
    amount - kept with is_resale=True."""
    out = []
    for offer in price_json.get("offerPrices") or []:
        offer_id = str(offer.get("offerID") or "")
        resale = is_resale_offer(offer_id)
        for zp in offer.get("zonePrices") or []:
            type_labels = {str(pt.get("priceTypeID")): pt.get("label") or "" for pt in zp.get("priceTypes") or []}
            for pl in zp.get("priceLevels") or []:
                for pr in pl.get("prices") or []:
                    out.append(PriceLevel(
                        offer_id=offer_id,
                        offer_name=offer.get("offerName") or "",
                        price_level_id=str(pl.get("priceLevelID") or ""),
                        label=pl.get("label") or "",
                        price_type_id=str(pr.get("priceTypeID") or ""),
                        price_type_label=type_labels.get(str(pr.get("priceTypeID")), ""),
                        price_cents=int(pr.get("base") or 0),
                        available=(pl.get("availability") or {}).get("amount"),
                        is_resale=resale,
                    ))
    return out


def parse_offer_rules(price_json: dict, offer_search_json: dict) -> dict:
    """offer_id -> purchase-quantity rules, copied verbatim (None when AXS
    doesn't send the field): min/max/increment from price offerPrices[],
    allowEmptySingleSeats/requireContiguousSeats from offer/search offers[].
    Resale offers carry none of these (their rule is purchasableQuantityList)."""
    rules = {}
    for offer in (price_json or {}).get("offerPrices") or []:
        r = rules.setdefault(str(offer.get("offerID") or ""), {})
        r["min_quantity"] = offer.get("min")
        r["max_quantity"] = offer.get("max")
        r["quantity_increment"] = offer.get("increment")
    for offer in (offer_search_json or {}).get("offers") or []:
        r = rules.setdefault(str(offer.get("offerID") or ""), {})
        r["allow_empty_single_seats"] = offer.get("allowEmptySingleSeats")
        r["require_contiguous_seats"] = offer.get("requireContiguousSeats")
    return rules


def _int(s) -> Optional[int]:
    try:
        return int(str(s))
    except (TypeError, ValueError):
        return None


def parse_seats(offer_search_json: dict) -> tuple:
    """inventory/V2/{token}/offer/search -> (list[Seat], dict offer_id -> resale offer meta).
    Resale meta keeps what's only on the offer (totalPrice, connectionFee,
    purchasableQuantityRule/List) - grouping.py needs it for resale listings."""
    seats, resale_meta = [], {}
    for offer in offer_search_json.get("offers") or []:
        offer_id = str(offer.get("offerID") or "")
        resale = is_resale_offer(offer_id) or offer.get("offerType") == "FLASHSEATS"
        if resale:
            resale_meta[offer_id] = {
                "total_price": offer.get("totalPrice"),
                "connection_fee": offer.get("connectionFee"),
                "split_rule": offer.get("purchasableQuantityRule") or "",
                "split_quantities": offer.get("purchasableQuantityList") or [],
                "offer_type": offer.get("offerType") or "",
            }
        for it in offer.get("items") or []:
            number = str(it.get("number") or "")
            seats.append(Seat(
                offer_id=offer_id,
                section=it.get("sectionLabel") or "",
                row=it.get("rowLabel") or "",
                number=number,
                seat_num=_int(number),
                price_level_id=str(it.get("priceLevelID") or ""),
                section_id=str(it.get("sectionID") or ""),
                row_id=str(it.get("rowID") or ""),
                seat_id=str(it.get("id") or ""),
                status_label=it.get("statusCodeLabel") or it.get("priceCodeLabel") or "",
                seat_type=it.get("seatType") or "",
                neighborhood=it.get("neighborhoodPrintDescription") or it.get("neighborhoodLabel") or "",
                is_ga=bool(it.get("isGASection")),
                is_resale=resale,
            ))
    return seats, resale_meta


def parse_marketplace_offers(offers_json: dict) -> tuple:
    """/axsmarketplace/offers (best_available flow - events whose inventory on
    AXS is the Marketplace, `isExternalPurchaseFlow=True` in phase) ->
    (list[Listing], meta). Listings arrive bundled with no seat numbers;
    prices are DOLLARS here (unlike Veritix cents) - converted to cents so
    Listing.price_cents means the same thing in both flows.
    meta.listingCount / meta.ticketCount are the site's own totals (coverage)."""
    from .models import Listing
    meta = offers_json.get("meta") or {}
    out = []
    for l in offers_json.get("listings") or []:
        sec = l.get("section") or {}
        pb = l.get("priceBreakdown") or {}
        out.append(Listing(
            offer_id=str(l.get("id") or ""),
            section=sec.get("name") or "",
            row=str(l.get("row") or ""),
            seating_type="Marketplace",
            price_cents=int(round(float(l.get("price") or 0) * 100)),
            total_price=l.get("allInPrice", pb.get("total")),
            fee_per_ticket=pb.get("serviceFee"),
            split_quantities=list(l.get("splits") or []),
            is_resale=True,
            zone=sec.get("longSectionName") or sec.get("name") or "",
            quantity_override=int(l.get("quantity") or 0),
            stock_type=l.get("stockType") or "",
            in_hand_date=l.get("inHandDate") or "",
            notes=l.get("notes") or "",
            seat_features=[str(f) for f in (l.get("seatFeatures") or []) if f],
            max_quantity=meta.get("maxTicketCount"),
            face_value=l.get("faceValue"),
        ))
    return out, meta


def section_count(sections_json: dict) -> int:
    """inventory/V2/{token}/sections is a dict sectionLabel -> section."""
    return len(sections_json or {})
