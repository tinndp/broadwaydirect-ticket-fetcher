"""Groups individual AVAILABLE seats into listings - same algorithm SHAPE as
broadwaydirect/grouping.py (per the user's "same rule as Broadway"
instruction), ported from the .NET demo's SeatGrouper.cs. Two things could
NOT be ported as-is - see README.md "Open questions":

  1. Accessible/ADA seats: broadwaydirect has an explicit `ada_type` field
     per seat and excludes non-"None" ones by default. eVenue's seat rows
     have no such field - the closest candidate, marker_id/
     seat_marker_active, is UNCONFIRMED. This grouper does NOT exclude
     anything based on it.
  2. DEFAULT_RULES here are deliberately inert (see below) - Broadway's
     numbers describe one theater's chart, not a stadium's.
"""

import json
from typing import Iterable, Optional

from .models import SeatRow, Listing

# center_seat_threshold=0 means every numbered seat takes the CENTER/
# Consecutive branch (plain adjacency grouping, no odd/even split) and both
# suffixes are empty (no label appended) - "group adjacent seats, invent
# nothing about the venue's layout" until real per-venue rules are supplied.
DEFAULT_RULES = {
    "box_prefixes": [],
    "center_seat_threshold": 0,
    "sides_suffix": "",
    "center_suffix": "",
}


def load_rules(path: Optional[str] = None) -> dict:
    if not path:
        return dict(DEFAULT_RULES)
    with open(path, "r", encoding="utf-8") as f:
        data = json.load(f)
    rules = dict(DEFAULT_RULES)
    for k in ("box_prefixes", "center_seat_threshold", "sides_suffix", "center_suffix"):
        if k in data:
            rules[k] = data[k]
    return rules


def classify_section(section: str, seat_num: Optional[int], rules: dict):
    """Returns (suffix, seating_type). A Box-prefix section, or a seat whose
    seat_num didn't parse (None), always comes back Consecutive/no-suffix -
    adjacency-by-number cannot apply to a non-numeric seat."""
    if seat_num is not None:
        section_upper = section.upper()
        if any(p and section_upper.startswith(p.upper()) for p in rules["box_prefixes"]):
            return "", "Consecutive"
        threshold = rules["center_seat_threshold"]
        if seat_num > threshold:
            return rules["center_suffix"], "Consecutive"
        return rules["sides_suffix"], "OddEven"
    return "", "Consecutive"


def group_into_listings(available_seats: Iterable[SeatRow], rules: Optional[dict] = None) -> list:
    rules = rules or DEFAULT_RULES
    buckets: dict[tuple, list] = {}
    bucket_order: list[tuple] = []
    bucket_suffix: dict[tuple, str] = {}
    ungrouped: list[SeatRow] = []

    for s in available_seats:
        suffix, seating_type = classify_section(s.section, s.seat_num, rules)
        if s.seat_num is None:
            ungrouped.append(s)
            continue
        # Bucket key MUST stay keyed on the FULL level_section_cd (not just
        # level or just section) so two different sections sharing the same
        # level, or the same section under a different level, never merge
        # into one listing. Only the OUTPUT fields (Listing.level/.section
        # below, in _make_listing) split it into Level-only vs Section-only.
        bucket_label = f"{s.level_section_cd} {suffix}".strip() if suffix else s.level_section_cd
        parity = (s.seat_num % 2) if seating_type == "OddEven" else -1
        key = (bucket_label, s.row_cd, s.price_level_cd, seating_type, parity)
        if key not in buckets:
            buckets[key] = []
            bucket_order.append(key)
            bucket_suffix[key] = suffix
        buckets[key].append(s)

    listings = []
    for key in bucket_order:
        _bucket_label, row, plcd, seating_type, _parity = key
        group = buckets[key]
        suffix = bucket_suffix[key]
        step = 2 if seating_type == "OddEven" else 1
        sorted_group = sorted(group, key=lambda s: s.seat_num)

        run = [sorted_group[0]]
        for prev, cur in zip(sorted_group, sorted_group[1:]):
            if cur.seat_num - prev.seat_num == step:
                run.append(cur)
            else:
                listings.append(_make_listing(suffix, row, plcd, seating_type, run))
                run = [cur]
        listings.append(_make_listing(suffix, row, plcd, seating_type, run))

    for s in ungrouped:
        listings.append(Listing(
            level=s.level,
            row=s.row_cd,
            price_level_cd=s.price_level_cd,
            section=s.section,
            seat_keys=[s.seat_key],
            seat_cds=[s.seat_cd],
            seating_type="Ungrouped",
        ))

    return listings


def _make_listing(suffix, row, plcd, seating_type, run) -> Listing:
    # A run of exactly one seat is always Consecutive, even in an OddEven
    # bucket - matches broadwaydirect's _make_listing.
    if len(run) <= 1:
        seating_type = "Consecutive"
    # Listing.level = Level only + suffix if any (e.g. "OK SIDES"), NOT the
    # full LEVELSECTIONCD - the Section part lives on Listing.section
    # instead (both same for every seat in the run - grouping already keyed
    # on the full level_section_cd, so run[0]'s level/section apply to all).
    level = run[0].level
    label = f"{level} {suffix}".strip() if suffix else level
    return Listing(
        level=label,
        row=row,
        price_level_cd=plcd,
        section=run[0].section,
        seat_keys=[s.seat_key for s in run],
        seat_nums=[s.seat_num for s in run],
        seat_cds=[s.seat_cd for s in run],
        seating_type=seating_type,
    )


def parse_seat_availability(top: list, columns_expected: list) -> tuple[list, list]:
    """top: the parsed [rows, columnNames] response. Returns
    (list[SeatRow], columns_actual) - columns_actual is returned so the
    caller can warn if it differs from columns_expected (the response is
    parsed positionally either way, same defensive stance as the .NET
    demo's SeatAvailabilityClient)."""
    rows_raw, columns = top[0], top[1]
    seats = []
    for r in rows_raw:
        if len(r) < 10:
            continue
        level_section = r[0] or ""
        level, _, section = level_section.partition(":")
        seat_cd = str(r[2]) if r[2] is not None else ""
        try:
            seat_num = int(seat_cd)
        except (ValueError, TypeError):
            seat_num = None
        seats.append(SeatRow(
            level_section_cd=level_section,
            level=level,
            section=section if section else level_section,
            row_cd=str(r[1]) if r[1] is not None else "",
            seat_cd=seat_cd,
            seat_num=seat_num,
            price_level_cd=str(r[3]) if r[3] is not None else "",
            seat_status=str(r[4]) if r[4] is not None else "",
            marker_id=r[5],
            seat_marker_active=(None if r[6] is None else r[6] == 1),
            available=(r[8] == 1),
            hidden=(r[9] == 1),
        ))
    return seats, columns
