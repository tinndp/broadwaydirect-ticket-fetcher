"""Writes AXS listings in the REAL .NET Rowing Mongo staging shape - same
as paciolanevenue/mongo_inventory.py (read its module docstring):

  - ONE shared collection per datasource: `AXS_Inventories_NEW`
    (= SettingFactory.GetIntegrationNewInventoryCollectionName(DataSourceType.AXS)
    once AXS is registered in step 4 - planned enum value 9),
  - one document per listing, keyed by `SourceEventId`,
  - `deleteMany({SourceEventId})` + `insertMany` per event
    (IntegrationSourceNewListingMongoRepository.SaveFilteredListingsAsync),
  - PascalCase fields of IntegrationTemplateSourceInventory +
    BroadwayDirectSourceInventory's extras, plus AXS-only extras.

Deviation from broadwaydirect (the standard): broadwaydirect/mongo_storage.py
writes `raw_events`/`cleaned_events`; that shape does not match the C# Rowing
bots (see paciolanevenue/mongo_inventory.py's 2026-09-22 correction note), and
the Rowing bot is where AXS ends up (step 4). Raw JSON is kept on disk instead
(output/axs/{eventId}/raw_*.json).

The id hash (ListingIdentity port) is IMPORTED from paciolanevenue, not
copied - one implementation of that algorithm in the Python side.
"""

import datetime
from decimal import Decimal
from typing import Optional

from shared.rowing_mongo import build_listing_id, get_client

from .models import Event, Listing

COLLECTION = "AXS_Inventories_NEW"


def source_event_id(event: Event) -> str:
    """AXS event ids are globally unique numbers - used as-is (string)."""
    return str(event.event_id)


def axs_fingerprint(listing: Listing) -> str:
    """Broadway's fingerprint is Section_Row_Low_High. AXS adds the offer id:
    two different offers (e.g. a regular and an ADA offer, or two resale
    offers) can both have an "Ungrouped" listing - no seat numbers - in the
    same section/row, which would otherwise collide onto one _id. Step 4 must
    add the same fingerprint to ListingIdentity.cs."""
    low = listing.seat_nums[0] if listing.seat_nums else ""
    high = listing.seat_nums[-1] if listing.seat_nums else ""
    return f"{listing.section}_{listing.row}_{low}_{high}_{listing.offer_id}"


def _qty(v):
    """A purchase-quantity rule of 0 means "not set" - stored as null, like a missing one."""
    return v or None


def _dollars(cents: int) -> float:
    return round((cents or 0) / 100, 2)


def listing_to_document(event: Event, listing: Listing, now: Optional[datetime.datetime] = None) -> dict:
    src = source_event_id(event)
    price = _dollars(listing.price_cents)
    try:
        price_level_id = int(listing.price_level_id)
    except (TypeError, ValueError):
        price_level_id = None  # resale / marketplace: no price level
    return {
        "_id": build_listing_id(src, axs_fingerprint(listing)),
        "SourceEventId": src,
        "Section": listing.section,
        "Row": listing.row,
        "LowSeat": listing.seat_nums[0] if listing.seat_nums else None,
        "HighSeat": listing.seat_nums[-1] if listing.seat_nums else None,
        "Quantity": listing.quantity,            # marketplace: listing quantity (no seat keys)
        # POS seating vocabulary is only Consecutive / Odd/Even. AXS listings are consecutive seat
        # numbers or seat-less (Rowing assigns consecutive dummy seats), and no odd/even row was seen on
        # 17 real events (docs/AXS_LISTING_RULES.md), so always "Consecutive". The listing kind is PriceClass.
        "Seating": "Consecutive",
        "Price": price,                        # face/list price per ticket, dollars, fees NOT included
        "PublicNotes": (listing.notes or "").strip() or None,   # marketplace seller notes (obstructed view, mobile entry...)
        "PrivateNotes": None,
        "Splits": ",".join(str(q) for q in listing.split_quantities) or None,
        "BrokerOwned": None,
        "LastApiSyncedDateTimeUtc": now or datetime.datetime.now(datetime.timezone.utc),
        # Broadway-style extras
        "PriceLevelId": price_level_id,
        "Zone": listing.zone or None,
        "DisplayPrice": price,
        "PriceClass": "Marketplace" if listing.seating_type == "Marketplace" else ("Resale" if listing.is_resale else "Primary"),
        "SeatKeys": ",".join(listing.seat_keys) or None,   # marketplace: no seat numbers
        # AXS extras
        "OfferId": listing.offer_id,
        "TotalPrice": listing.total_price,     # resale: AXS totalPrice per ticket incl. fee
        "FeePerTicket": listing.fee_per_ticket,
        "SplitRule": listing.split_rule or None,
        "StockType": listing.stock_type or None,   # marketplace only
        "InHandDate": listing.in_hand_date or None,
        # purchase-quantity rules, verbatim from AXS (None = AXS didn't send it)
        "MinQuantity": _qty(listing.min_quantity),
        "MaxQuantity": _qty(listing.max_quantity),
        "QuantityIncrement": _qty(listing.quantity_increment),
        "AllowEmptySingleSeats": listing.allow_empty_single_seats,
        "RequireContiguousSeats": listing.require_contiguous_seats,
        # marketplace, verbatim / labels only (docs/AXS_LISTING_RULES.md): faceValue, and the "ladder" a
        # listing belongs to - the same seats posted as quantity 1..n. Every listing is still stored;
        # POS/Mapper can keep only IsLadderMax to avoid counting the same seats n times.
        "FaceValue": listing.face_value,
        # AXS seat labels from a fixed list ("Restricted/Obstructed View", "Front of Section"), comma-joined
        "SeatFeatures": ",".join(listing.seat_features) or None,
        "LadderGroup": listing.ladder_group,
        "IsLadderMax": listing.is_ladder_max,
    }


def write_inventory(mongo_uri: str, mongo_db: str, event: Event, listings: list) -> int:
    """Rewrites ONE event's slice of the shared collection. Raises on Mongo
    failure - api.py treats persistence as best-effort."""
    from bson.decimal128 import Decimal128

    coll = get_client(mongo_uri, mongo_db)[mongo_db][COLLECTION]
    now = datetime.datetime.now(datetime.timezone.utc)
    docs = []
    for l in listings:
        d = listing_to_document(event, l, now)
        for k in ("Price", "DisplayPrice", "TotalPrice", "FeePerTicket"):
            if d[k] is not None:
                d[k] = Decimal128(Decimal(str(d[k])))
        docs.append(d)
    coll.delete_many({"SourceEventId": source_event_id(event)})
    if docs:
        coll.insert_many(docs)
    return len(docs)
