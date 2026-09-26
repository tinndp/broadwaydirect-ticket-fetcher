# AXS .NET 8 (step 3: macOS) - port of `python/axs/`

**STATUS (2026-09-24): works on macOS.** Core is identical to Python (8/8 parity tests), and the CLI and API crawled
real data. Veritix and Marketplace outputs match Python listing by listing, Mongo writes are verified, and runs
went both with and without the proxy.

Built on the standard layout (`BroadwayDirect.*`). The macOS browser part follows `PaciolanEvenue.Fetch.Playwright`
+ `.Cli`, because Broadway has no Playwright variant. Python source of truth: `../python/axs/` (README + RECON.md).

```
AXS.Core/              net8.0, Nullable OFF, C# 7.3-friendly (to be copied into the net48 Rowing bot in step 4)
  Models/AxsModels.cs          Event, PriceLevel, Seat, Listing, ResaleMeta, AxsResult (= python models.py)
  Parsing/AxsParser.cs         1:1 python parser.py (same "x or y" truthiness, banker's rounding)
  Grouping/AxsGrouper.cs       1:1 python grouping.py + build_result (ordinal sort = Python tuple sort)
  Storage/AxsInventoryStore.cs Rowing Mongo shape, AXS_Inventories_NEW, deleteMany+insertMany;
                               ListingIdentity reused from PaciolanEvenue.Core (python imports it the same way)
AXS.Fetch.Playwright/  **NuGet `Patchright` 1.63.0** (drop-in, same `Microsoft.Playwright` namespace) - see below.
                       AxsPlaywrightClient (python client.py): installed Chrome, poll challenge, hard-block page,
                       capture the ticket app's own commerce responses, retry w/ fresh proxy {SESSIONID};
                       AxsOutput (raw_*.json / result.json writers - snake_case = same JSON as python; proxy pick)
AXS.Cli/               `event --url ... [--proxy] [--retries] [--out] [--headless] [--bundled-chromium]` = python cli
AXS.Api/               POST /api/eventinventory {eventId,url,proxy?} = python api.py; net8.0 (runs on macOS,
                       unlike BroadwayDirect.Api / PaciolanEvenue.Api which are WebView2/Windows)
AXS.Tests/             xUnit on the real fixtures + Fixtures/expected_python.json (python output on the same files)
```
There is no `AXS.Fetch` (WebView2) project here. The Windows/WebView2 part is step 4 (Rowing).

## Verified

- `dotnet build` of all AXS projects: 0 errors.
- `dotnet test AXS.Tests`: **8/8**. Parity with Python on real data:
  - Veritix (1457730): same coverage line, same event fields (UTC `Z` included), and all **167/167 Mongo docs
    identical** (`_id`/ListingIdentity, section, row, seats, qty, prices, splits…).
  - Marketplace (1623526): coverage `listings=3/3 tickets=12/12 … match=yes` and **3/3 docs identical**;
    marketplace id `7588913`.

## Browser: stock Microsoft.Playwright is blocked, NuGet `Patchright` passes

| Run (2026-09-24, macOS, installed Chrome) | Result |
|---|---|
| stock `Microsoft.Playwright` 1.48, 2 events, residential proxy, 4 sessions each | **8/8 stuck on "Just a moment..."** (www, 40s each) |
| stock `Microsoft.Playwright`, no proxy | stuck |
| same, with `--disable-blink-features=AutomationControlled` and without `--enable-automation` | still stuck |
| Python patchright, same moment, same proxy | passes (55s) |
| **NuGet `Patchright` 1.63.0** (github.com/DevEnterpriseSoftware/patchright-dotnet, Apache-2.0), no proxy | **passes: www 7s, total 26s** |

So Cloudflare detects stock Playwright; it isn't the proxy. `Patchright` is a third-party .NET port
("Microsoft Corporation, patched by Werner van Deventer"), adopted with the user's OK. It's a drop-in replacement:
the only change is the PackageReference, because the namespace is still `Microsoft.Playwright`. It targets
netstandard2.0, so it's also usable from net48 later. The automation-flag reduction (`ReduceAutomationFlags`,
default on) is kept, but the flags alone did not help.

## Real runs (2026-09-24, macOS, Patchright + installed Chrome)

| Run | Result |
|---|---|
| `AXS.Cli`, no proxy, **1457730 Tom Jones (Veritix)** | OK, attempt 1: www 5s, inventory 29s, **34s total**. 12 sections, 22 price levels, **756 seats** (721 primary + 35 resale), 167 listings |
| same run, next event **1623526 AEW (Marketplace)**, warm browser | OK: www **1s**, inventory 9s, **10s total**. listings 3/3, tickets 12/12, match=yes |
| `AXS.Cli`, AEW alone, cold browser | OK in 21-26s |
| **.NET vs Python `result.json`** (same events, same day) | **identical**: 1457730 167/167 listings (756 tickets); 1623526 3/3 (12 tickets); 0 differences |
| **`AXS.Api` + user's proxy payload** (`{SESSIONID}`), Mongo on | **HTTP 200 in 53s**, attempt 1 (inventory 36s). 3 listings / 12 tickets. Mongo `AXS_Inventories_NEW` rewritten (`LastApiSyncedDateTimeUtc` 09:30:02Z → 09:54:30Z) and read back as 3 / 12 |

Notes:
- The browser stays open across events (Cloudflare clearance is reused), so the 2nd+ event is much faster (10s vs 34s).
- CORS preflight `OPTIONS` responses hit the same commerce URLs with no body. They are ignored
  (fixed in both .NET and Python after the first API run logged `No resource with given identifier`).
- Mongo `Price` is written as Decimal128 `309` vs Python's `309.0`. They're numerically equal.

## Run

```bash
cd dotnet
dotnet build Crawlers.sln
dotnet test AXS.Tests
dotnet run --project AXS.Cli -- event --url https://www.axs.com/events/1457730/tom-jones-21-event-tickets \
    [--proxy "http://user-{SESSIONID}:pass@host:port"]      # -> AXS.Cli output dir (default output/axs)
MONGO_URI=... MONGO_DB=broadwaydirect dotnet run --project AXS.Api   # http://localhost:5310
curl -X POST http://localhost:5310/api/eventinventory -H "Content-Type: application/json" \
  -d '{"eventId":"1623526","url":"https://www.axs.com/events/1623526/aew-blood-and-guts-tickets","proxy":"..."}'
```
Google Chrome must be installed (`Channel="chrome"`).

## Listing rules (2026-09-25)

`AxsGrouper` is a 1:1 port of the Python rule in `python/axs/docs/AXS_LISTING_RULES.md`, which was measured on 17 real events:
- Veritix primary seats are grouped by consecutive seat numbers per (offer, section, row, price level).
- FLASHSEATS resale and Marketplace listings are kept as the seller listed them.
- Marketplace "ladders" are labelled (`LadderGroup` / `IsLadderMax`) and never merged.
- Missing values are null.
- Marketplace seller notes (`listings[].notes`) go to `PublicNotes`; AXS seat labels (`seatFeatures`) go to `SeatFeatures` (comma-joined).

The Digital Venue seat map is not used: it changed no listing on the 2 Veritix venues sampled, its `main_hd` layout has no aisle markers, and suite seat order is shuffled. The parity test also covers the ladder event (`Fixtures/axs_1623554_mp_offers.json`).
