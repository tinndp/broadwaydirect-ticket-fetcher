# AXS: listing rules (final, 2026-09-26)

Evidence: 17 real AXS events recorded on 2026-09-25/26, with every XHR/fetch of the ticket page saved. They cover theaters (Broadway Theatre, Beacon, Warner DC, Wilbur, Uptown KC, Vivian Beaumont), arenas (Crypto.com Arena, VyStar), clubs (Hollywood Palladium, White Oak) and casinos.
- The machine IP gets hard-blocked after about 8–11 ticket pages, and needs about 7 hours to clear.
- The wiredproxies pool is refused by AXS (403/429/hard block on 4/4 sessions).

## Flows (from `start-flow`)

| Flow | How to recognise it | What AXS sells | Seen |
|---|---|---|---|
| **Veritix** | `es5Flow = PICK_A_SEAT_2D`, ticket link `shop.axs.com` | primary seats (`price` + `sections` + `offer/search`) + FLASHSEATS resale | 3 (Tom Jones, LA Kings, JUNGLE) |
| **Marketplace** | `es5Flow = BEST_AVAILABLE`, `isExternalPurchaseFlow = true`, ticket link `tix.axs.com` | **resale only**, listings bundled by the seller, no seat numbers; primary is sold elsewhere | 13 |
| not on AXS | ticket link to another seller (e.g. ticketmaster.com) | nothing | 1 (Beacon, Ray LaMontagne) |

## Rules (python `axs/grouping.py`, .NET `AXS.Core/Grouping/AxsGrouper.cs`, Rowing `AXSCrawlerBot/AXSGrouper.cs`)

1. **Veritix primary seats:**
   - Seats with **consecutive seat numbers** in one (offer, section, row, price level) form one listing.
   - `Seating = Consecutive`. `LowSeat`/`HighSeat` are the lowest/highest number.
   - Seats whose number is not an integer go into one listing per group, without seat numbers.
2. **Veritix FLASHSEATS resale:** one listing per resale offer, exactly as the seller listed it. `Splits` / `SplitRule` come from `purchasableQuantityList` / `purchasableQuantityRule`.
3. **Marketplace:** one listing per `listings[]` entry, exactly as returned.
   - `Splits` = `splits`, `MaxQuantity = meta.maxTicketCount`.
   - `StockType`, `InHandDate`, `FaceValue` are stored as returned.
   - `PublicNotes` = the seller's `listings[].notes` (e.g. "Obstructed view", "Aisle seats", "you will need to use an iOS or Android mobile device"), trimmed; null when empty. Veritix offers have no seller notes, so it stays null there.
   - `SeatFeatures` = `listings[].seatFeatures` joined with "," (AXS labels from a fixed list, seen: "Restricted/Obstructed View", "Front of Section"); null when empty. Kept apart from `PublicNotes` so it can be filtered on; 3 of the 7 labelled listings seen had nothing about it in the seller notes.
4. **Marketplace ladders:** the same seats posted by a seller as quantity 1, 2, .., n.
   - They are labelled, never merged. `LadderGroup` = "section|row|price in cents", `IsLadderMax` = true on the n-ticket listing. No `LadderSize`: n = the `Quantity` of the `IsLadderMax` listing (= the number of listings in the group), so it would only repeat data.
   - A ladder = same section (whitespace collapsed), row, price, faceValue, allInPrice, stockType, inHandDate and notes; 2+ listings; quantities exactly 1..n; each listing's splits exactly [1..q].
   - Seen on 2 of 13 marketplace events: Sound of Music (11 ladders = all 44 listings), Great Gatsby (3).
5. **Purchase rules of a Veritix offer:** `MinQuantity` / `MaxQuantity` / `QuantityIncrement` from `price` `offerPrices[].min/max/increment` (e.g. Aisle offer 2 / 8 / 2). `AllowEmptySingleSeats` / `RequireContiguousSeats` from `offer/search`.
6. **No value = null**, never `""` or `0`. Examples: `PriceLevelId` of resale/marketplace, `SeatKeys` of marketplace, an empty `Zone`, a quantity rule of 0.
7. The listing id fingerprint is `Section_Row_Low_High_OfferId`.

## Why the Digital Venue seat map is NOT used

The Veritix picker loads a Digital Venue seat map (`3ddvapis.com .../maps/main/` or `.../maps/main_hd/master_full.json`) with every seat of every row. It was tested as a way to group seats by physical position and was dropped:
- **No effect on real data.** On both Veritix venues sampled (Tom Jones: 151 listings; LA Kings: 487), grouping by the map gave exactly the same listings as grouping by seat numbers. No odd/even row was found, and no aisle split two consecutive numbers.
- **Unreliable order.** The `main_hd` layout (Crypto.com Arena) has no empty-slot markers. Section 102 row 3 lists `..6, 10..` with seats 7–9 missing, so map order would merge 6 with 10. Suite/box seats are listed out of order (`17, 18, 19, 20, 24, 23, 22, 21`).
- **Cost.** It adds 0.4–1.9 MB per event and up to 6 s of waiting.

If an AXS venue with real odd/even rows shows up, handle it with a per-venue rule, as Broadway does.
