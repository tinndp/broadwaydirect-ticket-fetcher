"""Tests for the axs package - pure parsing/grouping/document shape on the
REAL responses captured from event 1457730 (Tom Jones, The HALL at Live!,
2026-09-24). No browser, no network, no MongoDB. These are also the test
vectors for the .NET port (step 3)."""

import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))  # python/

from axs.parser import parse_event, parse_event_url, parse_price, parse_seats, is_resale_offer
from axs.grouping import build_result, group_into_listings
from axs.models import Listing, Event
from axs.mongo_inventory import listing_to_document, axs_fingerprint

FX = Path(__file__).resolve().parent / "fixtures" / "axs_1457730"


def _load(name):
    return json.loads((FX / f"{name}.json").read_text())


def _event():
    return parse_event({"pageProps": _load("event")})


def _result():
    return build_result(_event(), {k: _load(k) for k in ("price", "sections", "offer_search")})


def test_parse_event_url():
    assert parse_event_url("https://www.axs.com/events/1457730/tom-jones-21-event-tickets") == (1457730, "tom-jones-21-event-tickets")
    assert parse_event_url("https://www.axs.com/uk/events/123/x?y=1") == (123, "x")
    assert parse_event_url("https://example.com/events/1") is None


def test_parse_event_metadata_and_utc():
    ev = _event()
    assert ev.event_id == 1457730
    assert ev.name == "Tom Jones (21+ Event)"
    assert ev.venue_id == 128148 and ev.venue_name == "The HALL at Live!"
    assert ev.event_dt_local == "2026-10-04T19:00:00"
    assert ev.event_dt_utc == "2026-10-04T23:00:00Z"   # AXS omits the Z; EDT = UTC-4
    assert ev.event_tz == "America/New_York"
    assert ev.ticket_url == "https://shop.axs.com/?c=axs&e=30367541911434726"
    assert ev.performer_ids == ["201194"]


def test_parse_price_cents_and_resale():
    pls = parse_price(_load("price"))
    p5 = [p for p in pls if p.offer_id == "11434726" and p.label == "P5"][0]
    assert p5.price_cents == 7500 and p5.price_type_label == "Regular"
    resale = [p for p in pls if p.is_resale]
    assert len({p.offer_id for p in resale}) == 17
    assert all(is_resale_offer(p.offer_id) for p in resale)


def test_seats_all_land_in_listings():
    seats, meta = parse_seats(_load("offer_search"))
    assert len(seats) == 756
    assert sum(s.is_resale for s in seats) == 35
    res = _result()
    assert sum(l.quantity for l in res["listings"]) == 756
    assert len(res["listings"]) == 167
    assert all(l.price_cents > 0 for l in res["listings"])


def test_resale_offer_is_one_listing_not_regrouped():
    res = _result()
    resale = [l for l in res["listings"] if l.is_resale]
    assert len(resale) == 17 and len({l.offer_id for l in resale}) == 17
    l = [x for x in resale if x.offer_id == "9000000178233956"][0]
    assert l.price_cents == 47430 and l.total_price == 595.25 and l.fee_per_ticket == 120.95
    assert l.split_rule == "EVEN"


def test_primary_listings_are_consecutive_runs():
    res = _result()
    for l in res["listings"]:
        if l.seating_type == "Consecutive":
            assert l.seat_nums == list(range(l.seat_nums[0], l.seat_nums[0] + len(l.seat_nums)))


def test_grouping_splits_gaps_and_keeps_non_numeric_seats():
    from axs.models import Seat, PriceLevel
    mk = lambda n: Seat(offer_id="1", section="A", row="1", number=str(n), seat_num=n if isinstance(n, int) else None, price_level_id="9")
    seats = [mk(1), mk(2), mk(4), mk("GA-1")]
    pls = [PriceLevel(offer_id="1", offer_name="", price_level_id="9", label="P9", price_type_id="1", price_type_label="Regular", price_cents=1000)]
    ls = group_into_listings(seats, pls, {})
    assert sorted((l.seating_type, l.quantity) for l in ls) == [("Consecutive", 1), ("Consecutive", 2), ("Ungrouped", 1)]


def test_mongo_document_ids_unique_and_shape():
    ev, res = _event(), _result()
    docs = [listing_to_document(ev, l) for l in res["listings"]]
    assert len({d["_id"] for d in docs}) == len(docs)
    d = docs[0]
    for k in ("SourceEventId", "Section", "Row", "LowSeat", "HighSeat", "Quantity", "Seating", "Price",
              "PriceLevelId", "Zone", "SeatKeys", "OfferId", "LastApiSyncedDateTimeUtc"):
        assert k in d
    assert d["SourceEventId"] == "1457730"


def test_fingerprint_separates_offers_in_same_row():
    a = Listing(offer_id="1", section="A", row="1", seat_keys=["x"], seating_type="Ungrouped")
    b = Listing(offer_id="2", section="A", row="1", seat_keys=["y"], seating_type="Ungrouped")
    assert axs_fingerprint(a) != axs_fingerprint(b)
    ev = Event(event_id=1)
    assert listing_to_document(ev, a)["_id"] != listing_to_document(ev, b)["_id"]


# --- AXS Marketplace (best_available) flow: real responses from event 1623526
# (AEW - Blood and Guts, VyStar Veterans Memorial Arena), 2026-09-24.
MP = Path(__file__).resolve().parent / "fixtures" / "axs_1623526"


def _mp():
    # offers only - eventinfo/mapinfo are no longer needed (they carry no ticket data)
    inv = {"mp_offers": json.loads((MP / "mp_offers.json").read_text())}
    return build_result(Event(event_id=1623526), inv)


def test_marketplace_listings_match_site_totals():
    res = _mp()
    assert "flow=marketplace" in res["coverage"] and "match=yes" in res["coverage"]
    assert "sections_with_tickets=3" in res["coverage"]
    assert res["event"].marketplace_event_id == 7588913
    assert len(res["listings"]) == 3 and sum(l.quantity for l in res["listings"]) == 12
    l = [x for x in res["listings"] if x.offer_id == "VB17279581528"][0]
    assert (l.section, l.row, l.quantity) == ("Lower Level 103", "V", 4)
    assert l.price_cents == 22300 and l.total_price == 266.64 and l.fee_per_ticket == 40.14
    assert l.split_quantities == [1, 2, 4] and l.seating_type == "Marketplace" and l.seat_nums == []


def test_marketplace_mongo_docs():
    res = _mp()
    docs = [listing_to_document(Event(event_id=1623526), l) for l in res["listings"]]
    assert len({d["_id"] for d in docs}) == 3
    d = [x for x in docs if x["OfferId"] == "VB17279581528"][0]
    assert d["Quantity"] == 4 and d["LowSeat"] is None and d["PriceClass"] == "Marketplace"
    assert d["Seating"] == "Consecutive"   # POS vocabulary; the listing kind is in PriceClass
    assert d["Price"] == 223.0 and d["Splits"] == "1,2,4"


def test_api_mirrors_mongo_only_when_inventory_was_fetched():
    # sold out (fetched, 0 listings) must still rewrite Mongo; blocked / not on sale must not touch it
    from axs.api import inventory_fetched
    assert inventory_fetched({"mp_offers": {"meta": {}, "listings": []}, "notes": []})
    assert inventory_fetched({"price": {}, "offer_search": {"offers": []}})
    assert not inventory_fetched({"notes": ["not on sale: ..."]})


def test_mongo_seating_is_always_pos_vocabulary():
    # Resale / Ungrouped / Marketplace must never reach the POS Seating field
    ev, res = _event(), _result()
    docs = [listing_to_document(ev, l) for l in res["listings"]]
    assert {d["Seating"] for d in docs} == {"Consecutive"}
    assert {d["PriceClass"] for d in docs} == {"Primary", "Resale"}


def test_quantity_rules_are_copied_verbatim_from_axs():
    # MinQuantity/MaxQuantity/QuantityIncrement = price offerPrices[].min/max/increment,
    # Allow/Require flags = offer/search offers[]; Splits/SplitRule stay the response lists as-is.
    ev, res = _event(), _result()
    by_offer = {}
    for l in res["listings"]:
        by_offer.setdefault(l.offer_id, listing_to_document(ev, l))
    reg, aisle, ada = by_offer["11434726"], by_offer["11434641"], by_offer["11434639"]
    assert (reg["MinQuantity"], reg["MaxQuantity"], reg["QuantityIncrement"]) == (1, 8, 1)
    assert (aisle["MinQuantity"], aisle["MaxQuantity"], aisle["QuantityIncrement"]) == (2, 8, 2)
    assert (ada["MinQuantity"], ada["MaxQuantity"], ada["QuantityIncrement"]) == (1, 2, 1)
    assert ada["AllowEmptySingleSeats"] is False and ada["RequireContiguousSeats"] is False
    assert reg["Splits"] is None and reg["SplitRule"] is None      # primary: AXS sends no split list
    resale = [d for d in by_offer.values() if d["PriceClass"] == "Resale"]
    assert resale and all(d["MinQuantity"] is None and d["MaxQuantity"] is None for d in resale)
    assert all(d["Splits"] is not None and d["SplitRule"] for d in resale)
    mp = [listing_to_document(Event(event_id=1623526), l) for l in _mp()["listings"]]
    assert {d["MaxQuantity"] for d in mp} == {6} and {d["MinQuantity"] for d in mp} == {None}


# --- Marketplace ladders: the same seats posted as quantity 1..n (labelled, never dropped) ---

MP2 = Path(__file__).resolve().parent / "fixtures" / "axs_1623554"


def test_sound_of_music_ladders_are_labelled_and_nothing_is_dropped():
    res = build_result(Event(event_id=1623554), {"mp_offers": json.loads((MP2 / "mp_offers.json").read_text())})
    ls = res["listings"]
    assert len(ls) == 44 and sum(l.quantity for l in ls) == 110          # everything AXS returned is kept
    groups = {l.ladder_group for l in ls}
    assert len(groups) == 11 and None not in groups                      # 11 ladders cover all 44 listings
    assert sum(1 for l in ls if l.is_ladder_max) == 11
    assert sum(l.quantity for l in ls if l.is_ladder_max) == 44          # the real ticket count
    m = sorted((l.quantity, l.is_ladder_max, listing_to_document(Event(event_id=1623554), l)["LadderGroup"])
               for l in ls if l.section == "ORCH RIGHT" and l.row == "M")
    assert m == [(1, False, "ORCH RIGHT|M|50000"), (2, False, "ORCH RIGHT|M|50000"),
                 (3, False, "ORCH RIGHT|M|50000"), (4, True, "ORCH RIGHT|M|50000")]
    doc = listing_to_document(Event(event_id=1623554), [l for l in ls if l.is_ladder_max][0])
    assert "LadderSize" not in doc and doc["Quantity"] == 4 and doc["IsLadderMax"] is True and doc["FaceValue"] is not None


def test_no_ladder_on_aew_and_on_look_alikes():
    from axs.grouping import mark_ladders
    assert {l.ladder_group for l in _mp()["listings"]} == {None}
    def mp(q, splits, notes=""):
        return Listing(offer_id=f"{q}{notes}", section="B", row="V", seating_type="Marketplace", price_cents=5000,
                       split_quantities=splits, quantity_override=q, notes=notes)
    two_pairs = mark_ladders([mp(2, [2]), mp(2, [2])])                         # Maddow: two 2-ticket listings
    gap = mark_ladders([mp(1, [1]), mp(2, [1, 2]), mp(4, [1, 2, 3, 4])])      # quantities not 1..n
    odd_splits = mark_ladders([mp(1, [1]), mp(2, [2])])                       # splits not [1..q]
    other_notes = mark_ladders([mp(1, [1]), mp(2, [1, 2], notes="aisle")])   # not the same listing data
    for ls in (two_pairs, gap, odd_splits, other_notes):
        assert {l.ladder_group for l in ls} == {None} and {l.is_ladder_max for l in ls} == {None}
    ok = mark_ladders([mp(2, [1, 2]), mp(1, [1])])
    assert [(l.quantity, l.is_ladder_max) for l in ok] == [(2, True), (1, False)]


def test_marketplace_seller_notes_go_to_public_notes():
    docs = [listing_to_document(Event(event_id=1623526), l) for l in _mp()["listings"]]
    assert all(d["PublicNotes"] and d["PublicNotes"].startswith("Please note") for d in docs)
    assert all(d["PrivateNotes"] is None for d in docs)
    veritix = [listing_to_document(_event(), l) for l in _result()["listings"]]
    assert all(d["PublicNotes"] is None for d in veritix)                # Veritix offers carry no seller notes
    assert all(d["SeatFeatures"] is None for d in veritix)


def test_marketplace_seat_features_are_stored_verbatim():
    from axs.parser import parse_marketplace_offers
    raw = {"meta": {}, "listings": [
        {"id": "a", "row": "S", "quantity": 2, "splits": [2], "price": 90, "section": {"name": "Orch"},
         "seatFeatures": ["Restricted/Obstructed View", "Front of Section"]},
        {"id": "b", "row": "T", "quantity": 2, "splits": [2], "price": 90, "section": {"name": "Orch"}, "seatFeatures": []}]}
    ls, _ = parse_marketplace_offers(raw)
    docs = [listing_to_document(Event(event_id=1), l) for l in ls]
    assert [d["SeatFeatures"] for d in docs] == ["Restricted/Obstructed View,Front of Section", None]
