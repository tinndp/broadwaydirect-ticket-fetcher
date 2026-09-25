"""Writes listings into the REAL .NET Rowing Mongo staging shape: ONE
SHARED collection PER DATASOURCE (`PaciolanEvenue_Inventories_NEW`, no
event id in the name), keyed by the `SourceEventId` field inside each
document, one DOCUMENT PER LISTING, PascalCase fields matching
`IIntegrationTemplateSourceInventory`/`BroadwayDirectSourceInventory`
(ETECH.Application.Library.SK4DataSource.Core/Models/Templates/
IntegrationTemplateSourceInventory.cs, BroadwayDirectSourceInventory.cs).

CORRECTED 2026-09-22: an earlier pass of this file used a *per-event*
collection name (`PaciolanEvenue_Inventories_NEW_{eventId}`), based on
what looked like the real Mongo instance's collections on 2026-09-21.
That was wrong - re-reading the actual, current C# source (not just live
Mongo data, which can contain stale/leftover collections from before a
refactor) shows the real write path is unambiguous and consistent across
every reference in the codebase:
`SettingFactory.GetIntegrationNewInventoryCollectionName(dataSourceType)`
= `"{prefix}_Inventories_NEW"` (SettingFactory.cs) - no event id suffix -
and `IntegrationCrawlerSessionForm.SaveInventoryAsync` (the bot's own
doc comment: "This is the ONLY place listing data lands") writes there via
`IntegrationSourceNewListingMongoRepository.SaveFilteredListingsAsync`,
which does `deleteMany({SourceEventId: eventId})` then `insertMany` -
NOT a delete of the whole collection. `BroadwayDirectCrawlerInventoryStager`,
`IntegrationTemplateSyncMongoContext.DropEventStagingCollection`, and the
synchronizer (`IntegrationTemplateToLocalPOSSynchronizer`) all agree on
the same shared name. This module now matches that: see
`collection_name()` (no event id) and `write_inventory()`'s
`delete_many({"SourceEventId": ...})` below.

This REPLACES the earlier `raw_events`/`cleaned_events` shape (shared
collections, one doc per event with nested listings[]) that
`broadwaydirect`/`stubhub`'s own mongo_storage.py use - that shape was
copied from those packages' own README without checking it against the
real C# source first.

eVenue is NOT a registered DataSourceType in the .NET enum yet as of the
start of this module's life, though the `crawler-rowing-integration` skill
run on 2026-09-22 is adding it (`DataSourceType.PaciolanEvenue`) alongside
the rest of the Rowing wiring - see that session's changes under
`ETECH.Application.Library.SK4DataSource.Core`. Writing in this shape here
means the shape is already right for when the real bot's data lands
next to this package's test data.
"""

import datetime
import threading
from typing import Optional

from .models import Event, Listing, PriceLevel

# --- ListingIdentity.cs, ported 1:1 (unchecked ulong djb2-variant hash +
# base-36 compact encode) - see that file's comment for why the hash
# preimage includes the event id, and VerifyKnownVectors for its own
# self-test (not ported here; this implementation follows the same
# published algorithm, byte for byte, but was not cross-checked against
# the C# runtime output since nothing here can execute C#). -------------

_MASK64 = (1 << 64) - 1
_COMPACT_ALPHABET = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ"
_COMPACT_RADIX = 36
_COMPACT_LENGTH = 13


def get_deterministic_hash_code(s: Optional[str]) -> int:
    if s is None:
        s = ""
    hash1 = ((5381 << 16) + 5381) & _MASK64
    hash2 = hash1
    n = len(s)
    i = 0
    while i < n:
        hash1 = (((hash1 << 5) + hash1) ^ ord(s[i])) & _MASK64
        if i == n - 1:
            break
        hash2 = (((hash2 << 5) + hash2) ^ ord(s[i + 1])) & _MASK64
        i += 2
    return (hash1 + hash2 * 1566083941) & _MASK64


def compact_encode(hash_val: int) -> str:
    chars = [""] * _COMPACT_LENGTH
    h = hash_val
    for i in range(_COMPACT_LENGTH - 1, -1, -1):
        chars[i] = _COMPACT_ALPHABET[h % _COMPACT_RADIX]
        h //= _COMPACT_RADIX
    return "".join(chars)


def build_listing_id(event_id: str, fingerprint: str) -> str:
    """Port of ListingIdentity.Build(eventId, fingerprint)."""
    preimage = (event_id or "") + "\n" + (fingerprint or "")
    return compact_encode(get_deterministic_hash_code(preimage))


def _paciolan_fingerprint(section: str, row: str, low_seat, high_seat, seat_tag: str = "") -> str:
    """Modeled on ListingIdentity.BroadwayFingerprint (Section_Row_Low_High) -
    there is no official "BuildPaciolanEvenue" in the real C# yet since
    eVenue isn't a registered DataSourceType, so this is the closest
    existing analog, not a literal port of an existing eVenue-specific
    method."""
    base = f"{section}_{row}_{low_seat}_{high_seat}"
    # seat_tag (lettered seats "W", GA "PL6", no-digit codes "NC6") keeps e.g. W1-W3 and C1-C3 of one
    # row apart. Plain numbered seats have no tag, so their _id is unchanged from before 2026-09-25.
    return f"{base}_{seat_tag}" if seat_tag else base


def source_event_id(event: Event) -> str:
    """host:seasonCd:itemCd - globally unique across schools (itemCd alone
    collides between e.g. Purdue's F06 and a different school's F06)."""
    return f"{event.host}:{event.season_cd}:{event.item_cd}"


def collection_name(event: Event) -> str:
    """Shared collection for the WHOLE datasource - no event id in the name.
    Matches SettingFactory.GetIntegrationNewInventoryCollectionName(DataSourceType.PaciolanEvenue)
    = "PaciolanEvenue_Inventories_NEW". `event` is accepted (unused) only to
    keep call sites symmetric with `source_event_id(event)`."""
    return "PaciolanEvenue_Inventories_NEW"


def _seat_status_type(event: Event, listing: Listing):
    """HOLDCODES types of the listing's seat statuses, e.g. "available" or "accessible, available"."""
    if not event.hold_codes:
        return None
    types = sorted({event.hold_codes[c] for c in listing.seat_statuses if event.hold_codes.get(c)})
    return ", ".join(types) or None


def _qty(v):
    """A purchase-quantity rule of 0 means "not set" in eVenue - stored as null, like a missing one."""
    return v or None


def _price_row(listing: Listing, price_levels: list):
    """The PL_PT_PRICES row _listing_price reads: Public ("P") for this
    listing's price level, else the first one, else None."""
    candidates = [pl for pl in price_levels if pl.pl == listing.price_level_cd]
    if not candidates:
        return None
    return next((pl for pl in candidates if pl.pt == "P"), candidates[0])


def _listing_price(listing: Listing, price_levels: list) -> float:
    """Picks the Public ("P") price for this listing's price level, else the
    first available price type for that level, else 0. Divides by 100
    (see README.md "Open questions" - units look like cents but are not
    100% confirmed). Returns a plain float; caller wraps as Decimal128 for
    Mongo, matching the C# `decimal Price` field."""
    candidates = [pl for pl in price_levels if pl.pl == listing.price_level_cd]
    if not candidates:
        return 0.0
    public = next((pl for pl in candidates if pl.pt == "P"), candidates[0])
    return round(public.price / 100, 2)


def listing_to_document(event: Event, listing: Listing, price_levels: list) -> dict:
    """One listing -> one Mongo document, matching BroadwayDirectSourceInventory's
    shape (base IntegrationTemplateSourceInventory fields + Broadway's own
    extras, reused here since eVenue has no dedicated C# model yet)."""
    low_seat = listing.seat_nums[0] if listing.seat_nums else None
    high_seat = listing.seat_nums[-1] if listing.seat_nums else None
    seating = listing.seating_type  # already POS vocabulary: "Consecutive" | "Odd/Even" (grouping.py)

    src_event_id = source_event_id(event)
    # IMPORTANT: listing.level is Level-only (see models.py Listing
    # docstring), so the fingerprint must combine it with listing.section to
    # stay unique - two different sections under the same level (e.g.
    # "OK:107" vs "OK:108") sharing a row/seat range would otherwise
    # collide onto the same _id if only listing.level were used here.
    full_section = f"{listing.level}:{listing.section}" if listing.section else listing.level
    fingerprint = _paciolan_fingerprint(full_section, listing.row, low_seat, high_seat, listing.seat_tag)
    doc_id = build_listing_id(src_event_id, fingerprint)

    price_levels_for_pl = [pl for pl in price_levels if pl.pl == listing.price_level_cd]
    zone = price_levels_for_pl[0].pl_desc if price_levels_for_pl else ""
    price = _listing_price(listing, price_levels)
    row = _price_row(listing, price_levels)
    try:
        price_level_id = int(listing.price_level_cd)
    except (TypeError, ValueError):
        price_level_id = None

    return {
        "_id": doc_id,
        "SourceEventId": src_event_id,
        "Level": listing.level,      # Level only (+ suffix if any), e.g. "OK"
        "Section": listing.section,  # Section only, Level dropped, e.g. "107" - matches BroadwayDirectSourceInventory.Section's role
        "Row": listing.row,
        "LowSeat": low_seat,
        "HighSeat": high_seat,
        "Quantity": listing.quantity,
        "Seating": seating,
        "Price": price,
        "PublicNotes": None,
        "PrivateNotes": None,
        "Splits": None,
        "BrokerOwned": None,
        "LastApiSyncedDateTimeUtc": datetime.datetime.now(datetime.timezone.utc),
        # Broadway-style extras (kept for parity - see module docstring):
        "PriceLevelId": price_level_id,  # eVenue PRICELEVELCD as a number (same field name as Broadway/AXS; null if not numeric)
        "Zone": zone or None,  # DisplayName dropped per user request - was always identical to this
        "DisplayPrice": price,
        "PriceClass": (row.pt if row else "") or None,  # PT of the same row the Price comes from (.NET/Rowing do the same)
        "SeatKeys": ",".join(listing.seat_keys) or None,  # GA quantity listing: no seat numbers
        # Purchase-quantity rules from the event page SSR. eVenue uses 0 for "not set", so 0 and a
        # missing value are both stored as null (user decision 2026-09-25).
        # Event level: MINQTY / MAXQTY / MULTIPLEQTY. (STUDENTMAXQTY / PLPT_STUDENTMAXQTY are not stored:
        # student-flow only, never set on 60 real events - user decision 2026-09-25.)
        "MinQuantity": _qty(event.min_qty),
        "MaxQuantity": _qty(event.max_qty),
        "QuantityIncrement": _qty(event.multiple_qty),
        # Price level: PLPT_MINQTY / PLPT_MAXQTY / PLPT_MULTIPLE of the same PL_PT_PRICES row the Price comes from.
        "PriceLevelMinQuantity": _qty(row.plpt_min_qty if row else None),
        "PriceLevelMaxQuantity": _qty(row.plpt_max_qty if row else None),
        "PriceLevelQuantityIncrement": _qty(row.plpt_multiple if row else None),
        # Standard eVenue category of the listing's seats (maps_eventMap HOLDCODES type of their
        # SEATSTATUS codes): "available" = regular seats, "accessible" = wheelchair / ADA / companion
        # (usually only for buyers who need them), "limited" = obstructed / limited view.
        # Null when HOLDCODES could not be read.
        "SeatStatusType": _seat_status_type(event, listing),
        # Part of the _id fingerprint (see _paciolan_fingerprint) - stored so Rowing's
        # ListingIdentity.ForIntegrationListing can rebuild the same _id from the document.
        "SeatTag": listing.seat_tag or None,
    }


_clients_lock = threading.Lock()
_clients: dict = {}  # (mongo_uri, mongo_db) -> pymongo.MongoClient


def _get_client(mongo_uri: str, mongo_db: str):
    """Lazily connects once per (uri, db) and reuses the same pooled client
    across calls - same pattern as broadwaydirect/api.py's `_get_mongo()`
    singleton, instead of opening (and, on any failure, leaking) a brand new
    MongoClient - with its own connection pool and background monitor
    thread - on every single write_inventory() call. A failed connect isn't
    cached, so the next call retries cleanly."""
    import pymongo

    key = (mongo_uri, mongo_db)
    with _clients_lock:
        client = _clients.get(key)
        if client is not None:
            return client
        client = pymongo.MongoClient(mongo_uri, serverSelectionTimeoutMS=5000)
        try:
            client.admin.command("ping")
        except Exception:
            client.close()
            raise
        _clients[key] = client
        return client


def write_inventory(mongo_uri: str, mongo_db: str, event: Event, listings: list, price_levels: list) -> int:
    """Rewrites ONE EVENT's slice of the shared collection: deletes only the
    documents whose SourceEventId matches this event, then insert_many's the
    fresh set - matches IntegrationSourceNewListingMongoRepository.
    SaveFilteredListingsAsync exactly (deleteMany({SourceEventId: eventId})
    then InsertMany), NOT a delete of the whole collection, since the
    collection is now shared across every event of this datasource (see
    module docstring). Returns the number of documents written. Raises on a
    Mongo failure - caller decides whether that's fatal (see api.py, which
    treats persistence as best-effort). Uses a shared, long-lived MongoClient
    (see _get_client) rather than opening and closing a new one per call."""
    from bson.decimal128 import Decimal128
    from decimal import Decimal

    src_event_id = source_event_id(event)
    client = _get_client(mongo_uri, mongo_db)
    coll = client[mongo_db][collection_name(event)]

    docs = []
    for listing in listings:
        doc = listing_to_document(event, listing, price_levels)
        doc["Price"] = Decimal128(Decimal(str(doc["Price"])))
        doc["DisplayPrice"] = Decimal128(Decimal(str(doc["DisplayPrice"])))
        docs.append(doc)

    coll.delete_many({"SourceEventId": src_event_id})
    if docs:
        coll.insert_many(docs)
    return len(docs)
