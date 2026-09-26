"""Tests for paciolanevenue/grouping.py - the listing rule agreed on 2026-09-25 (see
docs/EVENUE_INVENTORY_RULES.md). Pure logic, no network/browser. The paciolan_rules fixtures are
real responses captured 2026-09-25 (seat-availability rows + event page PL_PT_PRICES +
maps_eventMap HOLDCODES/SEATING_TYPES)."""

import json
import sys
from collections import Counter
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))  # python/

from paciolanevenue.models import Event, PriceLevel, SeatRow
from paciolanevenue.grouping import (
    build_listings, odd_even_sections, parse_seat_availability, split_seat_code,
)
from paciolanevenue.mongo_inventory import listing_to_document

FX = Path(__file__).resolve().parent / "fixtures" / "paciolan_rules"


def _load(name, with_map=True):
    d = json.loads((FX / f"{name}.json").read_text())
    seats, _ = parse_seat_availability([d["rows"], d["columns"]], d["columns"])
    m = d["meta"]
    ev = Event(host=m["host"], season_cd=m["season"], item_cd=m["item"])
    if with_map:
        ev.hold_codes = {h["holdcode"]: h["type"] for h in d["hold_codes"]}
        ev.seating_types = {str(x["pl"]): x["seatingType"] for x in d["seating_types"]}
    pls = [PriceLevel(pl=str(x["PL"]), pl_desc=x["PL_DESC"] or "", pt=x["PT"] or "",
                      pt_desc=x["PT_DESC"] or "", price=x["PRICE"] or 0) for x in d["pl_pt"]]
    return seats, ev, pls


def _seat(level_section, row, seat_cd, plcd="1", available=True, status="O"):
    level, _, section = level_section.partition(":")
    n, tag = split_seat_code(seat_cd)
    return SeatRow(level_section_cd=level_section, level=level, section=section or level_section,
                   row_cd=row, seat_cd=seat_cd, seat_num=n if not tag else None, price_level_cd=plcd,
                   seat_status=status, available=available)


def _ev(hold_codes=None, seating_types=None):
    ev = Event(host="h", season_cd="S", item_cd="I")
    ev.hold_codes, ev.seating_types = hold_codes, seating_types
    return ev


PL1 = [PriceLevel(pl="1", pl_desc="Tier 1", pt="P", pt_desc="Public", price=5000)]


# --- seat codes -----------------------------------------------------------------------------

def test_split_seat_code():
    assert split_seat_code("12") == (12, "")
    assert split_seat_code("W1") == (1, "W")
    assert split_seat_code("10w") == (10, "w")
    assert split_seat_code("1A") == (1, "A")
    assert split_seat_code("INFANT") == (None, "INFANT")


# --- reserved seats: consecutive runs ------------------------------------------------------

def test_consecutive_run_is_one_listing():
    seats = [_seat("E:105", "10", str(n)) for n in (8, 9, 10)]
    ls = build_listings(seats, _ev(), PL1)
    assert len(ls) == 1
    assert (ls[0].quantity, ls[0].seat_range_label, ls[0].seating_type) == (3, "8-10", "Consecutive")
    assert (ls[0].level, ls[0].section) == ("E", "105")
    assert ls[0].seat_keys == ["10:8", "10:9", "10:10"]


def test_missing_number_ends_a_run():
    seats = [_seat("E:105", "10", str(n)) for n in (8, 9, 10, 15, 16)]
    assert sorted(l.seat_range_label for l in build_listings(seats, _ev(), PL1)) == ["15-16", "8-10"]


def test_price_level_and_section_split_listings():
    pls = PL1 + [PriceLevel(pl="2", pl_desc="Tier 2", pt="P", pt_desc="Public", price=4000)]
    seats = [_seat("E:105", "10", "8", "1"), _seat("E:105", "10", "9", "2"), _seat("E:106", "10", "8", "1")]
    assert len(build_listings(seats, _ev(), pls)) == 3


def test_sold_seats_are_not_listed_but_still_shape_the_section():
    # full map: seats 1..6 consecutive, only 1 and 3 available -> two 1-seat listings, not "1-3 Odd/Even"
    seats = [_seat("E:1", "A", str(n), available=n in (1, 3)) for n in range(1, 7)]
    ls = build_listings(seats, _ev(), PL1)
    assert sorted(l.seat_range_label for l in ls) == ["1", "3"]
    assert {l.seating_type for l in ls} == {"Consecutive"}


def test_unpriced_price_level_is_never_listed():
    seats = [_seat("E:105", "10", "8", plcd="9")]  # PL 9 has no public price
    assert build_listings(seats, _ev(), PL1) == []


# --- odd/even sections ------------------------------------------------------------------------

def test_odd_even_section_detection():
    odd = [_seat("L:LEFT", "A", str(n)) for n in (15, 17, 19, 21)]
    even = [_seat("L:RGHT", "A", str(n)) for n in (16, 18, 20)]  # only 3 seats -> not enough evidence
    mixed = [_seat("L:CTR", "A", str(n)) for n in (1, 3, 5, 6)]  # has 5/6
    assert odd_even_sections(odd + even + mixed) == {"L:LEFT"}


def test_odd_even_run_steps_by_two_and_single_seat_stays_consecutive():
    seats = [_seat("L:LEFT", "A", str(n)) for n in (15, 17, 19, 23, 25, 29)]
    ls = sorted(build_listings(seats, _ev(), PL1), key=lambda l: l.seat_nums[0])
    assert [(l.seat_range_label, l.seating_type) for l in ls] == [
        ("15-19", "Odd/Even"), ("23-25", "Odd/Even"), ("29", "Consecutive")]


def test_royce_hall_real_map():
    seats, ev, pls = _load("ucla_royce_370")
    assert odd_even_sections(seats) == {"1:BLCOR", "1:BLCTR", "1:BLEFT", "1:BRCOR", "1:BRCTR",
                                        "1:BRGHT", "1:LEFT", "1:RGHT"}  # CENTER (1-14) stays consecutive
    ls = build_listings(seats, ev, pls)
    assert sum(l.quantity for l in ls) == sum(1 for s in seats if s.available)  # nothing lost
    assert Counter(l.seating_type for l in ls) == Counter({"Odd/Even": 70, "Consecutive": 17})
    types = [listing_to_document(ev, l, pls)["SeatStatusType"] for l in ls]
    assert set(types) == {None, "accessible"} and types.count("accessible") == 3  # regular seats -> null
    left = [l for l in ls if l.section == "LEFT" and l.seating_type == "Odd/Even"]
    assert left and all(n % 2 == 1 for l in left for n in l.seat_nums)
    assert all(b - a == 2 for l in left for a, b in zip(l.seat_nums, l.seat_nums[1:]))


# --- lettered seat codes --------------------------------------------------------------------

def test_lettered_codes_group_by_tag_and_keep_distinct_ids():
    seats, ev, pls = _load("mgoblue_v07_wc")
    # Michigan V07 (Crisler Center) H:114 row 20: C1, C7, W1, W7 available - C1 and W1 share
    # Section/Row/Low/High, only the tag keeps them apart
    ls = [l for l in build_listings(seats, ev, pls) if l.section == "114" and l.row == "20"]
    assert sorted((l.seat_tag, l.seat_range_label) for l in ls) == [("C", "1"), ("C", "7"), ("W", "1"), ("W", "7")]
    assert all(l.seating_type == "Consecutive" for l in ls)
    docs = [listing_to_document(ev, l, pls) for l in build_listings(seats, ev, pls)]
    assert len({d["_id"] for d in docs}) == len(docs)
    # wheelchair / companion seats (SEATSTATUS "w" -> HOLDCODES type "accessible") vs regular seats
    by_tag = {(d["SeatTag"], d["LowSeat"]): d["SeatStatusType"] for d in docs if d["Section"] == "114" and d["Row"] == "20"}
    assert set(by_tag.values()) == {"accessible"}


def test_codes_without_digits_become_one_listing_without_seat_numbers():
    seats = [_seat("GA:GEN", "GEN", "INFANT"), _seat("GA:GEN", "GEN", "LAP")]
    ls = build_listings(seats, _ev(), PL1)
    assert len(ls) == 1 and ls[0].seat_nums == [] and ls[0].quantity == 2 and ls[0].seat_tag == "NC1"


def test_no_ungrouped_value_anymore():
    seats = [_seat("E:1", "A", "1"), _seat("E:1", "A", "10w"), _seat("GA:GEN", "GEN", "INFANT")]
    assert {l.seating_type for l in build_listings(seats, _ev(), PL1)} == {"Consecutive"}


# --- GA (SEATING_TYPES "G") ---------------------------------------------------------------------

def test_ga_quantity_page_counts_available_hold_codes_even_when_available_flag_is_0():
    # Kansas soccer (S26/06): quantity-only page, every seat AVAILABLE=0, 2006 open seats in the GA level
    seats, ev, pls = _load("ku_iowa_state_06_ga")
    assert not any(s.available for s in seats)
    ls = build_listings(seats, ev, pls)
    ga = [l for l in ls if l.seating_type_cd == "G"]
    assert len(ga) == 1
    g = ga[0]
    assert (g.quantity, g.row, g.level, g.seat_nums, g.seat_keys) == (2006, "GA", "GA", [], [])
    assert g.seat_tag == f"PL{g.price_level_cd}" and g.seat_statuses == ["O"]
    doc = listing_to_document(ev, g, pls)
    assert doc["Quantity"] == 2006 and doc["LowSeat"] is None and doc["SeatStatusType"] is None
    assert "SeatingType" not in doc and "SeatStatus" not in doc
    assert doc["Seating"] == "Consecutive"


def test_ga_non_available_hold_types_are_not_counted():
    hc = {"O": "available", "w": "accessible", "K": "hidden"}
    seats = [_seat("GA:GA", "1", str(n), available=False, status=s) for n, s in ((1, "O"), (2, "O"), (3, "w"), (4, "K"), (5, "X"))]
    ls = build_listings(seats, _ev(hc, {"1": "G"}), PL1)
    assert [l.quantity for l in ls] == [2]


def test_without_event_map_falls_back_to_available_flag():
    seats, ev, pls = _load("ku_iowa_state_06_ga", with_map=False)
    assert build_listings(seats, ev, pls) == []  # the old behaviour: nothing is AVAILABLE=1


# --- raw response parsing -------------------------------------------------------------------

def test_parse_seat_availability_positional_columns():
    columns = ["LEVELSECTIONCD", "ROWCD", "SEATCD", "PRICELEVELCD", "SEATSTATUS",
               "MARKER_ID", "SEAT_MARKER_ACTIVE", "SLP_PRICE", "AVAILABLE", "HIDDEN"]
    rows = [
        ["E:101", "10", "1", "9", "X", None, None, None, 0, 0],
        ["E:101", "10", "2", "9", "O", "18", 1, None, 1, 0],
    ]
    seats, cols = parse_seat_availability([rows, columns], columns)
    assert cols == columns
    assert len(seats) == 2
    assert seats[0].available is False
    assert seats[1].available is True
    assert seats[1].seat_num == 2
    assert seats[1].marker_id == "18"
    assert seats[1].seat_marker_active is True
    assert seats[1].seat_key == "10:2"


def test_parse_event_map():
    from paciolanevenue.parser import parse_event_map, event_map_query
    body = {"data": {"maps_eventMap": {"SEATING_TYPES": [{"pl": "1", "seatingType": "R"}, {"pl": "6", "seatingType": "G"}],
                                       "HOLDCODES": [{"holdcode": "O", "title": "Open Seats", "message": None, "type": "available"}]}}}
    assert parse_event_map(body) == ({"O": "available"}, {"1": "R", "6": "G"})
    q = event_map_query(Event(host="h", season_cd="F26", item_cd="F04", data_account_id="242", fac_cd="RA",
                              configuration_cd="T", policy_cd="IBM:DEFAULT:I", policy_type="I",
                              distributor_id="IBM", base_map_id="1947"))["query"]
    assert 'itemCd: "F04"' in q and 'baseMapId: "1947"' in q and "HOLDCODES" in q and 'availability: "A|S"' in q


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
