"""Tests for paciolanevenue/mongo_inventory.py - pure logic (id generation,
document shape), no real MongoDB needed."""

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from paciolanevenue.models import Event, Listing, PriceLevel
from paciolanevenue.mongo_inventory import (
    build_listing_id, get_deterministic_hash_code, listing_to_document,
)


def _event(item_cd="F06"):
    return Event(host="purduesports.evenue.net", season_cd="F26", item_cd=item_cd)


def test_build_listing_id_is_deterministic_and_13_chars():
    lid1 = build_listing_id("purduesports.evenue.net:F26:F06", "E_10_8_10")
    lid2 = build_listing_id("purduesports.evenue.net:F26:F06", "E_10_8_10")
    assert lid1 == lid2
    assert len(lid1) == 13
    assert lid1.isalnum() and lid1 == lid1.upper()


def test_hash_stays_within_64_bits():
    h = get_deterministic_hash_code("some arbitrary preimage\nwith a newline")
    assert 0 <= h < (1 << 64)


def test_listing_to_document_id_does_not_collide_across_sections_sharing_a_level():
    # Two listings: same Level ("E", i.e. same Listing.level), same
    # Row/LowSeat/HighSeat, but DIFFERENT Section (Listing.section) - must
    # NOT produce the same _id, or one would silently overwrite the other's
    # Mongo document.
    event = _event()
    l1 = Listing(level="E", row="10", price_level_cd="1",
                 section="105", seat_keys=["10:8"], seat_nums=[8])
    l2 = Listing(level="E", row="10", price_level_cd="1",
                 section="106", seat_keys=["10:8"], seat_nums=[8])
    doc1 = listing_to_document(event, l1, [])
    doc2 = listing_to_document(event, l2, [])
    assert doc1["_id"] != doc2["_id"]
    assert doc1["Level"] == doc2["Level"] == "E"
    assert doc1["Section"] == "105"
    assert doc2["Section"] == "106"


def test_listing_to_document_price_lookup_and_seat_keys_join():
    event = _event()
    price_levels = [PriceLevel(pl="2", pl_desc="East Midfield", pt="P", pt_desc="Public", price=9500)]
    listing = Listing(level="E", row="10", price_level_cd="2", section="105",
                       seat_keys=["10:8", "10:9"], seat_nums=[8, 9])  # Row:SeatCd only, per current convention
    doc = listing_to_document(event, listing, price_levels)
    assert doc["Zone"] == "East Midfield"
    assert "DisplayName" not in doc  # dropped per user request
    assert float(str(doc["Price"])) == 95.0  # 9500 / 100
    assert doc["SeatKeys"] == "10:8,10:9"
    assert doc["PriceLevelId"] == 2


def test_quantity_rules_are_copied_verbatim_from_the_event_page():
    # Real values from soonersports F26/F03 (2026-09-25): MINQTY 0, MAXQTY 8, MULTIPLEQTY 0,
    # STUDENTMAXQTY 0. eVenue uses 0 for "not set" -> stored as null; real limits are kept.
    import json, re
    from paciolanevenue.parser import parse_event_page
    fx = Path(__file__).resolve().parent.parent.parent / "dotnet" / "PaciolanEvenue.Tests" / "Fixtures" / "sample_event_page.html"
    html = fx.read_text()
    m = re.search(r'(<script id="__NEXT_DATA__"[^>]*>)(.*?)(</script>)', html, re.S)
    data = json.loads(m.group(2))
    ev_raw = data["props"]["pageProps"]["component"]["props"]["ssrData"]["discovery_eventDetailMPT"][0]
    ev_raw.update(MINQTY=0, MAXQTY=8, MULTIPLEQTY=0, STUDENTMAXQTY=0)
    for row in ev_raw["PL_PT_PRICES"]:
        row.update(PLPT_MINQTY=2, PLPT_MAXQTY=6, PLPT_MULTIPLE=2, PLPT_STUDENTMAXQTY=0)
    html = html[:m.start(2)] + json.dumps(data) + html[m.end(2):]
    event, price_levels = parse_event_page(html, "purduesports.evenue.net", "F26", "F06")
    pl = price_levels[0]
    listing = Listing(level="E", row="10", price_level_cd=pl.pl, section="105", seat_keys=["10:8"], seat_nums=[8])
    doc = listing_to_document(event, listing, price_levels)
    assert (doc["MinQuantity"], doc["MaxQuantity"], doc["QuantityIncrement"]) == (None, 8, None)
    assert (doc["PriceLevelMinQuantity"], doc["PriceLevelMaxQuantity"], doc["PriceLevelQuantityIncrement"]) == (2, 6, 2)
    assert "StudentMaxQuantity" not in doc and "PlptStudentMaxQuantity" not in doc  # student flow only - not stored
    assert doc["Splits"] is None  # eVenue sends no split list


def test_quantity_rules_are_none_when_evenue_omits_them():
    doc = listing_to_document(_event(), Listing(level="E", row="1", price_level_cd="9", seat_keys=["1:1"], seat_nums=[1]), [])
    assert doc["MinQuantity"] is None and doc["MaxQuantity"] is None and doc["PriceLevelMaxQuantity"] is None


if __name__ == "__main__":
    import inspect
    mod = sys.modules[__name__]
    fns = [f for name, f in inspect.getmembers(mod, inspect.isfunction) if name.startswith("test_")]
    failed = 0
    for f in fns:
        try:
            f()
            print(f"PASS {f.__name__}")
        except AssertionError as e:
            failed += 1
            print(f"FAIL {f.__name__}: {e}")
    print(f"\n{len(fns) - failed}/{len(fns)} passed")
    sys.exit(1 if failed else 0)
