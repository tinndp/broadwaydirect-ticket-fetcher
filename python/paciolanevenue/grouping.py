"""Seats -> listings, the rule agreed with the user on 2026-09-25 after 72 real events on 12
eVenue hosts (research notes: ../EVENUE_INVENTORY_RULES.md).

1. Only price levels with a public price (in the event page's PL_PT_PRICES) are sold.
2. Which seats are sellable depends on the price level's SEATING_TYPES (maps_eventMap):
   - "R" (reserved): AVAILABLE == 1 (pickable on the map) - what the crawler always used.
   - "G" (GA, sold by quantity): SEATSTATUS whose HOLDCODES type is "available", whatever
     AVAILABLE says - quantity-only pages (ALLOWSEATMAP=False) report AVAILABLE=0 for every
     seat while selling them (Oklahoma softball/volleyball, Kansas soccer).
   Without maps_eventMap data (event.hold_codes is None) it falls back to AVAILABLE == 1.
3. GA: ONE quantity listing per price level (no seat numbers - eVenue assigns them; Rowing
   gives it dummy seats). Section = PL_DESC, Row = "GA".
4. Reserved: seats of one (section, row, price level, seat tag) grouped into runs.
   A SECTION is Odd/Even when, over EVERY numbered seat of the map (sold ones too), it has
   at least 4 seats, all odd or all even, and no two seats numbered n and n+1 (e.g. UCLA Royce
   Hall LEFT 15,17..41 / RGHT 16,18..42). Runs step 2 there, step 1 elsewhere; a missing number
   ends a run. A 1-seat run is Consecutive.
5. Lettered seat codes (W1, C1, 10w, 12c, 1A): the number is the seat number, the letters are
   the tag - only seats with the same tag are grouped. Codes with no digits at all become one
   listing per (section, row, price level) without seat numbers.
"""

import re
from collections import defaultdict
from typing import Optional

from .models import SeatRow, Listing

SELLABLE_GA_HOLD_TYPE = "available"
_CODE_RE = re.compile(r"^([A-Za-z]*)(\d+)([A-Za-z]*)$")


def split_seat_code(seat_cd: str):
    """"12" -> (12, ""), "W1" -> (1, "W"), "10w" -> (10, "w"), "INFANT" -> (None, "INFANT")."""
    m = _CODE_RE.match(seat_cd or "")
    if not m:
        return None, seat_cd or ""
    return int(m.group(2)), m.group(1) + m.group(3)


def odd_even_sections(all_seats) -> set:
    """level_section_cd of every Odd/Even section, judged on the whole seat map."""
    nums = defaultdict(set)
    for s in all_seats:
        n, tag = split_seat_code(s.seat_cd)
        if n is not None and not tag:
            nums[s.level_section_cd].add(n)
    return {sec for sec, v in nums.items()
            if len(v) >= 4 and len({n % 2 for n in v}) == 1 and not any(n + 1 in v for n in v)}


def is_sellable(seat: SeatRow, event) -> bool:
    if event.hold_codes is None or event.seating_types is None:
        return seat.available
    if event.seating_types.get(seat.price_level_cd) == "G":
        return event.hold_codes.get(seat.seat_status) == SELLABLE_GA_HOLD_TYPE
    return seat.available


def build_listings(all_seats: list, event, price_levels: list) -> list:
    """all_seats = the WHOLE seat-availability response (every status) - needed to judge
    Odd/Even sections. Returns listings for sellable seats only (see module docstring)."""
    priced = {pl.pl for pl in price_levels}
    pl_desc = {}
    for pl in price_levels:
        pl_desc.setdefault(pl.pl, pl.pl_desc)
    seating_types = event.seating_types or {}
    sellable = [s for s in all_seats if s.price_level_cd in priced and is_sellable(s, event)]

    listings = []
    ga = defaultdict(list)
    reserved = defaultdict(list)
    for s in sellable:
        if seating_types.get(s.price_level_cd) == "G":
            ga[s.price_level_cd].append(s)
        else:
            n, tag = split_seat_code(s.seat_cd)
            reserved[(s.level_section_cd, s.row_cd, s.price_level_cd, tag if n is not None else None)].append((n, s))

    for plcd in sorted(ga, key=lambda x: (len(x), x)):
        seats = ga[plcd]
        listings.append(Listing(
            level="GA", section=pl_desc.get(plcd, "") or "General Admission", row="GA",
            price_level_cd=plcd, seat_tag=f"PL{plcd}", seating_type="Consecutive",
            seating_type_cd="G", seat_statuses=sorted({s.seat_status for s in seats}),
            quantity_override=len(seats),
        ))

    oe = odd_even_sections(all_seats)
    for (lsc, row, plcd, tag), items in reserved.items():
        first = items[0][1]
        base = dict(level=first.level, section=first.section, row=row, price_level_cd=plcd,
                    seating_type_cd=seating_types.get(plcd, ""))
        if tag is None:  # no digits in the code at all
            seats = [s for _, s in items]
            listings.append(Listing(seat_keys=[s.seat_key for s in seats], seat_cds=[s.seat_cd for s in seats],
                                    seat_tag=f"NC{plcd}",
                                    seat_statuses=sorted({s.seat_status for s in seats}), **base))
            continue
        step = 2 if (not tag and lsc in oe) else 1
        by_num = {}
        for n, s in sorted(items, key=lambda x: x[0]):
            by_num.setdefault(n, s)
        run = []
        for n in sorted(by_num) + [None]:
            if run and (n is None or n - run[-1] != step):
                seats = [by_num[x] for x in run]
                listings.append(Listing(
                    seat_keys=[x.seat_key for x in seats], seat_nums=list(run), seat_cds=[x.seat_cd for x in seats],
                    seat_tag=tag, seating_type="Odd/Even" if step == 2 and len(run) > 1 else "Consecutive",
                    seat_statuses=sorted({x.seat_status for x in seats}), **base))
                run = []
            if n is not None:
                run.append(n)
    return listings


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
