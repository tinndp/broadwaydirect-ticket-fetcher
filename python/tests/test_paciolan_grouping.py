"""Tests for paciolanevenue/grouping.py - same style as test_grouping.py
(broadwaydirect), pure logic, no network/browser needed."""

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from paciolanevenue.models import SeatRow
from paciolanevenue.grouping import (
    classify_section, group_into_listings, parse_seat_availability, DEFAULT_RULES,
)


def _seat(level_section, row, seat_cd, plcd="1", available=True, hidden=False):
    section = level_section.split(":", 1)[1] if ":" in level_section else level_section
    try:
        seat_num = int(seat_cd)
    except ValueError:
        seat_num = None
    return SeatRow(
        level_section_cd=level_section, level=level_section.split(":")[0],
        section=section, row_cd=row, seat_cd=seat_cd, seat_num=seat_num,
        price_level_cd=plcd, seat_status="O", available=available, hidden=hidden,
    )


def test_classify_section_default_rules_always_consecutive():
    # DEFAULT_RULES is deliberately inert - see grouping.py docstring.
    suffix, seating_type = classify_section("101", 5, DEFAULT_RULES)
    assert suffix == ""
    assert seating_type == "Consecutive"
    suffix, seating_type = classify_section("101", 500, DEFAULT_RULES)
    assert suffix == ""
    assert seating_type == "Consecutive"


def test_classify_section_non_numeric_seat_is_consecutive():
    suffix, seating_type = classify_section("GA", None, DEFAULT_RULES)
    assert suffix == ""
    assert seating_type == "Consecutive"


def test_classify_section_box_prefix_exempt():
    rules = dict(DEFAULT_RULES, box_prefixes=["BOX"], center_seat_threshold=100,
                 sides_suffix="SIDES", center_suffix="CENTER")
    suffix, seating_type = classify_section("BOX 12", 5, rules)
    assert suffix == ""
    assert seating_type == "Consecutive"


def test_classify_section_threshold_splits_sides_center():
    rules = dict(DEFAULT_RULES, center_seat_threshold=100, sides_suffix="SIDES", center_suffix="CENTER")
    suffix, seating_type = classify_section("101", 50, rules)
    assert (suffix, seating_type) == ("SIDES", "OddEven")
    suffix, seating_type = classify_section("101", 150, rules)
    assert (suffix, seating_type) == ("CENTER", "Consecutive")


def test_group_contiguous_seats_into_one_listing():
    seats = [_seat("E:105", "10", str(n)) for n in (8, 9, 10)]
    listings = group_into_listings(seats, DEFAULT_RULES)
    assert len(listings) == 1
    assert listings[0].quantity == 3
    assert listings[0].seat_range_label == "8-10"
    assert listings[0].seating_type == "Consecutive"


def test_group_level_is_level_only_section_is_section_only():
    # Listing.level = Level only; Listing.section = Section only - per user
    # request, splitting LEVELSECTIONCD across the two fields.
    seats = [_seat("E:105", "10", str(n)) for n in (8, 9, 10)]
    listings = group_into_listings(seats, DEFAULT_RULES)
    assert listings[0].level == "E"
    assert listings[0].section == "105"
    # seat_keys drop BOTH Level and Section now - just "Row:SeatCd" -
    # Section lives on Listing.section (asserted above) instead.
    assert listings[0].seat_keys == ["10:8", "10:9", "10:10"]


def test_group_does_not_merge_different_sections_sharing_a_level():
    # Same Level ("E"), different Section (105 vs 106), same row/seat_num -
    # must NOT be grouped together even though Listing.level (Level-only)
    # would be identical for both.
    seats = [_seat("E:105", "10", "8"), _seat("E:106", "10", "8")]
    listings = group_into_listings(seats, DEFAULT_RULES)
    assert len(listings) == 2
    sections = sorted(l.section for l in listings)
    assert sections == ["105", "106"]


def test_group_splits_on_gap():
    seats = [_seat("E:105", "10", str(n)) for n in (8, 9, 10, 15, 16)]
    listings = group_into_listings(seats, DEFAULT_RULES)
    assert len(listings) == 2
    ranges = sorted(l.seat_range_label for l in listings)
    assert ranges == ["15-16", "8-10"]


def test_group_splits_on_price_level_change():
    seats = [_seat("E:105", "10", "8", plcd="1"), _seat("E:105", "10", "9", plcd="2")]
    listings = group_into_listings(seats, DEFAULT_RULES)
    assert len(listings) == 2


def test_group_non_numeric_seat_is_its_own_ungrouped_listing():
    seats = [_seat("E:105", "10", "8"), _seat("GA", "GEN", "INFANT")]
    listings = group_into_listings(seats, DEFAULT_RULES)
    ungrouped = [l for l in listings if l.seating_type == "Ungrouped"]
    assert len(ungrouped) == 1
    assert ungrouped[0].seat_cds == ["INFANT"]
    assert ungrouped[0].seat_nums == []


def test_group_singleton_run_forced_consecutive_even_in_oddeven_bucket():
    rules = dict(DEFAULT_RULES, center_seat_threshold=100, sides_suffix="SIDES", center_suffix="CENTER")
    seats = [_seat("E:105", "10", "8")]  # seat_num=8 <= threshold -> OddEven bucket
    listings = group_into_listings(seats, rules)
    assert len(listings) == 1
    assert listings[0].seating_type == "Consecutive"


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
    # seat_key is Row:SeatCd - Level ("E") AND Section ("101") both dropped
    # per user request; they live on Listing.level / Listing.section
    # instead (see grouping.py).
    assert seats[1].seat_key == "10:2"


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
