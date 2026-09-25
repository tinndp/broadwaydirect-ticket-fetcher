# Paciolan eVenue: which seats are sold, and how they become listings (2026-09-25)

Research on **72 real events on 12 eVenue hosts**: theaters (UCLA Royce Hall, Centre in the Square),
stadiums, arenas, ice, soccer and softball fields. For each event the crawler saved the whole
seat-availability map (sold seats too), the event page `PL_PT_PRICES`, and GraphQL `maps_eventMap`.
The scratch scripts and raw data stayed in the session scratchpad. Real responses used by the tests are
in `tests/fixtures/paciolan_rules/` (Royce Hall, Kansas S26/06, Michigan V07 H:108/H:114).

## What was found

| Finding | Evidence |
|---|---|
| `SEATSTATUS` codes differ per school (`O` at Purdue, `i` at Wake Forest, `Y` at UConn). The official meaning is GraphQL `maps_eventMap { HOLDCODES { holdcode title type } }`, where `type` is available / accessible / limited / hidden | 62 events, 11 hosts |
| `maps_eventMap { SEATING_TYPES { pl seatingType } }` says whether a price level is `R` (reserved seats) or `G` (GA, sold by quantity) | same |
| The seat API's `AVAILABLE=1` means "pickable on the seat map". Every AVAILABLE=1 seat had a sellable hold code and a priced level | 56 events |
| **Quantity-only pages** (`ALLOWSEATMAP=False`, e.g. Oklahoma softball/volleyball, Kansas soccer): every seat is AVAILABLE=0, but the site sells GA by quantity. The crawler used to report **0 tickets** there | Oklahoma FS02: 0 -> 6,879 (= the page's own AVAILABLE) |
| Quantity-only pages do **not** request seat availability themselves, so "capture the page's response" (.NET mac, Windows bot) can't work there. It must fetch in the page | recon of FGCU MS0929, Oklahoma FS01 |
| The SSR `AVAILABLE` total also counts price levels with **no public price** (student, lawn, holds). It is not a target. About 70% of the "missing" seats were unpriced | 57,554 of 82,358 |
| Odd/Even numbering: only UCLA Royce Hall (CENTER 1-14 consecutive, LEFT 15..41 odd, RGHT 16..42 even, balcony the same). The Broadway ">100 = center" threshold would be wrong there | 35,000 rows, 104 odd/even rows, all Royce |
| Rows with a missing number (`...5, 7, 8...`) are consecutive rows with a removed seat, not odd/even | 47 rows |
| Lettered seat codes: `W1`/`C1` (wheelchair/companion), `10w`/`12c`, `1A` (row `25A`). All are letters+digits | 1,392 codes, 0 unparseable |

## The rule (python `grouping.build_listings`, .NET `SeatGrouper.BuildListings`, Rowing `PaciolanEvenueSeatGrouper.BuildListings`)

1. **Only price levels with a public price** (present in `PL_PT_PRICES`).
2. **Sellable seat:**
   - `G` level: SEATSTATUS whose hold code type is `available`, whatever `AVAILABLE` says.
   - Every other level: `AVAILABLE == 1`.
   - If `maps_eventMap` failed, `AVAILABLE == 1` for everything (the old behaviour), and the coverage says `map=unavailable(...)`.
3. **GA:** one quantity listing per price level. `Level` = "GA", `Section` = `PL_DESC`, `Row` = "GA", no seat numbers (Rowing assigns dummy seats).
4. **Reserved:** seats of one (section, row, price level, seat tag) grouped into runs.
   - A **section** is Odd/Even when, over every numbered seat of the map, it has at least 4 seats, all odd or all even, and no two seats numbered n and n+1.
   - Runs step 2 in an Odd/Even section and 1 elsewhere. A missing number ends a run. A 1-seat run is Consecutive.
5. **Lettered codes:** the number is the seat number, and only seats with the same letters are grouped. Codes with no digits: one listing per (section, row, price level) without seat numbers.
6. `Seating` is only `Consecutive` or `Odd/Even` (the POS vocabulary). `"Ungrouped"` is gone.
7. **Listing id:** `Level:Section_Row_Low_High`, plus `_{SeatTag}` only when there is a tag (`W`, `PL6`, `NC6`). Plain numbered seats keep their old id. `SeatTag` is stored in the document so Rowing's `ListingIdentity.ForIntegrationListing` rebuilds the same id.
8. **New document fields:**
   - `SeatStatusType`: the standard HOLDCODES type of the seats' statuses — `available`, `accessible` (wheelchair / ADA / companion) or `limited` (obstructed view). SEATSTATUS codes themselves are not stored, because each school gives the same code a different meaning (`c` = Camera at one school, Companion Seat at another).
   - `SeatTag`: part of the id.
   - SEATING_TYPES R/G is not stored either: a GA listing is already recognisable from `Row = "GA"`, `Level = "GA"` and no seat numbers.

## Result on the 65 events with prices saved

- 27 events are identical to before.
- The changed events:
  - 5 GA events that returned 0 tickets now have them.
  - GA levels that used to be split into hundreds of fake-seat listings are now one quantity listing each (VCU volleyball 567 -> 1; the page itself only sells "General Admission" by quantity).
  - Royce Hall: 464 -> 87 listings (70 Odd/Even).
  - Michigan wheelchair/companion seats are grouped by tag.
- Nothing lost: total tickets are unchanged on every seat-map event. No duplicate ids.

## Open questions

- Oklahoma volleyball: the page counts statuses `i` and `f` (2,480) that are not in HOLDCODES. The rule takes the 700 `O` seats. Proving the right number would need a real cart hold, which was not done.
- Accessible hold codes with AVAILABLE=0 (a few dozen seats per event) are not taken. They are probably not sold online.
- Aisles inside a row are invisible: the seat API has no coordinates.
- A section mixing a consecutive center with odd/even sides under one code (Broadway style) was not seen. It would be classified Consecutive.
- .NET mac (stock Playwright) is blocked by PerimeterX on UCLA: the app never requests the seat map, and an own fetch gets 403. Python/patchright passes. This is not related to the rule.
