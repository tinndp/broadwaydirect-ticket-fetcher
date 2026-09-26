# axs (Python) — step 2: fetch ONE AXS event

Fetches everything for one AXS event you already know (`eventId` + `eventUrl`): event metadata,
price levels, sections, every seat (primary and AXS Official Resale / FLASHSEATS), grouped into
listings, plus the raw JSON. Built on the `broadwaydirect` package (the standard) with parts taken
from `paciolanevenue`. Site recon: [`docs/RECON.md`](docs/RECON.md).

Run everything from `python/`.

## How it differs from broadwaydirect / paciolanevenue

| | broadwaydirect (standard) | paciolanevenue | axs |
|---|---|---|---|
| Bot wall | Cloudflare Managed Challenge | PerimeterX | Cloudflare **interactive Turnstile** + Queue-it + a rate-limited commerce API |
| Browser | patchright bundled Chromium | same | **installed Google Chrome** (`channel="chrome"`): bundled Chromium gets stuck on the Turnstile checkbox |
| Data calls | `context.request.get` | in-page `fetch()` | **capture the responses the AXS ticket app makes itself** (`page.on("response")`). Replaying them returns 429 or a 403 Cloudflare page |
| Retry | single session | fresh proxy `{SESSIONID}` per retry | same as paciolanevenue, plus a 60s×attempt backoff when there's no proxy (same IP) |
| Grouping | seats → listings (theater rules) | seats → listings | primary seats → consecutive runs per (offer, section, row, price level); **resale offers stay one listing each** (like stubhub) |
| Mongo | `raw_events`/`cleaned_events` | Rowing shape | Rowing shape, `AXS_Inventories_NEW` |

**Deviations from broadwaydirect, with the reason for each:**
- **Mongo shape (`mongo_inventory.py`):** broadwaydirect's shape doesn't match the C# Rowing bot, which is where AXS ends up in step 4. Paciolan already corrected this (see its docstring), and AXS follows Paciolan.
- **Separate `parser.py`:** pure parsing, so the same raw JSON fixtures test Python now and .NET in step 3.
- **`cli.py`:** a manual-test CLI, following paciolanevenue. Broadway is API-only.
- **Listing rules (2026-09-25):** see `docs/AXS_LISTING_RULES.md` (17 real events). Veritix primary seats are grouped by consecutive seat numbers per (offer, section, row, price level); resale and Marketplace listings are kept as the seller listed them, and Marketplace "ladders" (the same seats posted as quantity 1..n) are labelled (`LadderGroup` / `IsLadderMax`), never merged. Missing values are null.
- **Mongo `Seating` is always `Consecutive`:** the POS only knows `Consecutive` and `Odd/Even` (the same values Broadway and Paciolan write). The listing kind (Primary / Resale / Marketplace) goes in `PriceClass`. Marketplace listings have no seat numbers, so `LowSeat`/`HighSeat`/`SeatKeys` are empty and Rowing's `AssignDummySeats` gives them consecutive dummy seats (Default Start Seat 500, Dummy Seat Gap 20).
- **Quantity rules are stored verbatim, never computed:** `Splits` is the list the response gives (`purchasableQuantityList` for FLASHSEATS resale, `listings[].splits` for Marketplace; primary offers have none, so `null`) and `SplitRule` is `purchasableQuantityRule`. Primary offers carry their rules in `MinQuantity`/`MaxQuantity`/`QuantityIncrement` (price `offerPrices[].min/max/increment`) and `AllowEmptySingleSeats`/`RequireContiguousSeats` (offer/search `offers[]`). Marketplace `MaxQuantity` = `meta.maxTicketCount`. Marketplace seller notes go to `PublicNotes`, AXS seat labels (`seatFeatures`) to `SeatFeatures`. `null` = AXS didn't send the field.
- **Listing fingerprint:** `Section_Row_Low_High_OfferId` instead of Broadway's `Section_Row_Low_High`. Without the offer id, two offers with ungrouped (non-numeric) seats in the same row would get the same `_id`. Step 4 must add the same fingerprint to `ListingIdentity.cs`.

## Files

```
models.py          Event, PriceLevel, Seat, Listing
parser.py          pure: event __NEXT_DATA__ -> Event; price / offer-search JSON -> PriceLevel / Seat
grouping.py        seats -> listings; build_result() = parse + group + coverage (shared by cli/api)
client.py          AxsClient (patchright + Chrome): get_event(url), get_inventory(event); retries
mongo_inventory.py Rowing Mongo shape (AXS_Inventories_NEW); ListingIdentity hash imported from paciolanevenue
api.py             FastAPI POST /api/eventinventory (Broadway contract)
cli.py             event | discover | crawl
output.py          raw JSON + result.json writers (output/ is gitignored)
discovery.py       PROTOTYPE for step 5 (Node discovery) - not part of the fetch API
```

## Flow per event (see docs/RECON.md §3)

1. Open `www.axs.com/events/{id}/{slug}` and wait out Cloudflare (poll the page title).
2. Read `__NEXT_DATA__` into `Event`: UTC gets a `Z` added, and the ticket link comes from here.
   `/_next/data/` does **not** include the ticket link.
3. Open the ticket link. It's either `shop.axs.com/?c=axs&e=…` or directly `tix.axs.com/{token}?axssid=…`.
   There may be a Queue-it wait here.
4. The app calls `unifiedapicommerce-us.axs.com/veritix/…`: `phase`, then `session` (creates the `axs_ecomm` cookie), then `start-flow`.
   After that, one of two sets follows. **Veritix:** `price`, `sections`, `offer/search`.
   **Marketplace:** `/axsmarketplace/{eventinfo,offers,mapinfo}`. The client captures whichever set appears (table below).
5. `build_result`: prices stay in cents in the model (e.g. `7500` = $75.00), and are converted to dollars only in Mongo/JSON.

## Run

```bash
python3 -m axs.cli event --url https://www.axs.com/events/1457730/tom-jones-21-event-tickets
#   -> output/axs/1457730/{raw_event,raw_phase,raw_price,raw_sections,raw_offer_search,result}.json

python3 -m uvicorn axs.api:app --port 8300
curl -X POST http://localhost:8300/api/eventinventory -H "Content-Type: application/json" \
  -d '{"eventId":"1457730","url":"https://www.axs.com/events/1457730/tom-jones-21-event-tickets"}'

python3 -m pytest -q tests/test_axs.py      # offline, real fixtures from event 1457730
```

Google Chrome must be installed; patchright drives it with `channel="chrome"`.
`--bundled-chromium` exists only to reproduce the Turnstile problem.

**Proxy is optional.** Priority order: request `proxy` field / `--proxy`, then
`AXS_PROXY_HOST/PORT/USER/PASS`, then `AXS_PROXY_LIST_PATH` (api only), then none.
`{SESSIONID}` in the user name is replaced on every (re)open. Never put real proxy values in a tracked file.

## Real runs (2026-09-24, macOS, Chrome, no proxy, IP in VN)

| Event | Result |
|---|---|
| 1457730 Tom Jones, The HALL at Live! (small theater) | **OK in 34s.** 12 sections, 22 price levels, 756/756 seats (721 primary + 35 resale) → 167 listings |
| 1623538 Riverdance, Warner Theatre | 1st try **FAILED**: waited for the Veritix calls, but this event uses the **Marketplace flow** (see below), so they never came. Then `phase` got **403** (IP flagged by the ×3 retries). Debug run: Marketplace, **0 listings** (the page itself says "No results found") |
| 1623528 Cam Girl, Warehouse Live (GA club) | Debug capture OK: **Marketplace**, 1 listing, 6/6 tickets, GA, $51 + $12.68 fee |
| 1623526 AEW, VyStar Veterans Memorial Arena (arena) | Debug capture OK: **Marketplace**, 3 listings, 12/12 tickets (`meta.listingCount`/`ticketCount` match) |
| all 3, CLI re-run ~13:50 | **BLOCKED**: `tix.axs.com` served AXS's hard-block page, "we actively prevent automated bots" (no Turnstile). The event pages still loaded. The machine's IP (IPv4, then IPv6) was flagged after ~15 ticket-page loads in about 1.5h |

### Through the residential proxy (wiredproxies, US, `{SESSIONID}` rotated per attempt), API `POST /api/eventinventory`, event 1623526

About 12 proxy sessions across 3 API calls and 3 debug runs:

| Stage | What happened |
|---|---|
| Event page `www.axs.com` | Often stuck on "Just a moment..." past 40s (2 of 3 sessions in one run). Passes eventually with a new session, or with 90s settle |
| Ticket page `tix.axs.com` | Cloudflare clears after about 30s (title becomes "FanSight") |
| `GET pre-flow/phase` | **429** on most sessions, and the app does NOT retry. It stays on "Loading..." |
| `POST session` (when phase got 200) | **403**, a 455KB Cloudflare/AXS HTML block page |

**Retest ~15:20 local (08:20Z), same pool, user's payload via API:** **OK on the first session**, 88s.
3/3 listings and 12/12 tickets; Mongo `AXS_Inventories_NEW` rewritten with 3 docs / 12 tickets (`LastApiSyncedDateTimeUtc`
updated), read back 3 / 12. So the pool is **intermittent**, not always refused.

**Earlier result:** 0 inventory fetched through this proxy pool. The commerce API refuses these IPs, so
rotating the session (the fix that worked for eVenue/PerimeterX) does not help here.
With the machine's own IP, the same flow worked (1457730) until the IP was flagged.

### MongoDB (docker `etech_mongodb`, db `broadwaydirect`), verified

`AXS_Inventories_NEW` was written from the real captured data, twice per event: no duplicates, deterministic `_id`.

| Event | Documents | Quantity sum | PriceClass |
|---|---|---|---|
| 1457730 | 167 | 756 | Primary + Resale |
| 1623526 | 3 | 12 | Marketplace |

**Two inventory flows exist.** `client.py` captures both; `grouping.build_result` picks by what was captured.

| Flow | When | Calls | Data |
|---|---|---|---|
| **Veritix** (`es5Flow` PICK_A_SEAT_2D) | primary sale on AXS, e.g. `shop.axs.com/?c=axs&e=…` (event 1457730) | `/veritix/inventory/…/price`, `/sections`, `/offer/search` | every seat, price levels in cents, FLASHSEATS resale |
| **Marketplace** (`es5Flow` BEST_AVAILABLE, `isExternalPurchaseFlow=true`) | `tix.axs.com/{token}?axssid=…` (3/3 newest events tried) | `/axsmarketplace/eventinfo`, `/offers`, `/mapinfo` | bundled listings (section, row, quantity, splits, price + fee in **dollars**, stock type); no seat numbers; `meta.listingCount/ticketCount` for coverage |

**Conclusion so far:** one IP with no proxy can load only a handful of AXS ticket pages. After that,
first the commerce API returns 403, and then `tix.axs.com` serves a hard-block page.
**Running more than a few events needs the proxy option.**

**Changes made after this failure:**
- The client now watches **every** commerce response, including `session` and `start-flow`, not only the captured ones.
- Retries without a proxy wait 60s × attempt.
- The CLI continues with the next event instead of stopping at the first failure.

**Still to verify:**
- (a) a full CLI/API run of the Marketplace flow. It needs an IP that AXS's commerce API accepts: the wiredproxies pool is refused (see above);
- (b) the API end-to-end with Mongo on a successful fetch. The Mongo write path alone is verified;
- (c) `offer/search` completeness at a big Veritix venue;
- (d) whether marketplace `offers` paginates on events with many listings;
- (e) how long the IP block lasts.

## Open questions

1. `price` → `priceLevels[].availability.amount` looks like the max per order (8), not stock, so coverage doesn't use it.
2. Some Veritix events may use BEST_AVAILABLE without the Marketplace. Not seen yet; if it happens, the client returns prices and sections with no seats, and adds a note.
3. The `phase` response contains the caller's IP, so it's kept out of `tests/fixtures/`.
