# PaciolanEvenue .NET (port from Python)

.NET 8 port of [`../python/paciolanevenue/`](../python/paciolanevenue/README.md). Mirrors the way
`dotnet/` ports the other two sites (dotnet/BroadwayDirect/README.md / dotnet/StubHub/README.md): same project
split, added to the same `Crawlers.sln`, reusing `BroadwayDirect.Core.Proxy.ProxyUri` and
`BroadwayDirect.Fetch.WebView2Host` only - everything else (models, parsing, grouping, the
PerimeterX browser, persistence) is its own, since eVenue's page/API shape and bot wall are both
different from Broadway/StubHub.

**`PaciolanEvenue.Fetch`/`PaciolanEvenue.Api` (WebView2) are Windows-only.** `dotnet run` fails to
even start on macOS ("No frameworks were found", `Microsoft.WindowsDesktop.App` has no macOS
build - confirmed 2026-09-22, not a code bug). A real Windows run against a known-good event
(Oklahoma F26/F03) surfaced 2 real bugs during development - both fixed (see git history / this
file's own notes below) - and **a follow-up real Windows run after the fixes confirmed it now
works end-to-end**: fetches the event, retries through PerimeterX blocks with a fresh proxy
session each time, groups listings, and persists to Mongo.

`PaciolanEvenue.Fetch.Playwright` + `PaciolanEvenue.Cli` also exist for macOS/Linux - added
2026-09-22 to prove the .NET port could crawl real data without waiting for a Windows machine.
**Real-tested and confirmed working**: the same Oklahoma F26/F03 via the same residential proxy -
blocked on the first event-page attempt and 3 of 4 seat-availability attempts, succeeded on retry
with a fresh session each time (same pattern the Python client proved),
`capacityMatch=yes(80395) availableMatch=yes(1054)`, 130 listings written to
`PaciolanEvenue.Cli/output/`. See "Cross-platform crawl (Playwright)" below.

## Structure

```
PaciolanEvenue.Core/    - Models (EventPageData, PriceLevel, SeatRow, ListingGroup,
                          PaciolanEvenueListing), Parsing/EventPageParser (__NEXT_DATA__ ->
                          EventPageData, ported from the .NET 8 demo at
                          ~/Desktop/crawler-playbook/PaciolanEvenue/PaciolanEvenueCrawler/,
                          Newtonsoft -> System.Text.Json), Parsing/SeatAvailabilityParser,
                          Grouping/SeatGrouper (listing rule of python/paciolanevenue/docs/EVENUE_INVENTORY_RULES.md:
                          GA quantity listings, Odd/Even sections), Storage/ListingIdentity (djb2 hash, ported
                          byte-for-byte from the real .NET Rowing bot's ListingIdentity.cs) +
                          Storage/PaciolanEvenueInventoryStore (Mongo - see "Mongo persistence").
                          Builds + tests on macOS/Linux.
PaciolanEvenue.Fetch.Playwright/ - PaciolanEvenuePlaywrightBrowser/Client - Microsoft.Playwright
                          (cross-platform), the PROVEN path (see above). Same retry/soft-block
                          logic as PaciolanEvenue.Fetch, ported back from what real testing found.
PaciolanEvenue.Cli/     - Manual-test console app, mirrors python/paciolanevenue/cli.py exactly
                          (args, output shape, exit codes). No Mongo write - prints + writes
                          event.json/listings.json under output/{host}/{season}/{item}/.
PaciolanEvenue.Fetch/   - PaciolanEvenueBrowser (PerimeterX block-marker poll, brand-new WebView2
                          environment + fresh proxy {SESSIONID} on every open - NOT session reuse,
                          see "Notable differences" below) + PaciolanEvenueClient (retry
                          orchestration) + PaciolanEvenueFetchOptions. Windows-only, NOT yet
                          confirmed working end-to-end there (build-checks clean on macOS only).
                          Reuses BroadwayDirect.Fetch.WebView2Host (the STA pump) only.
PaciolanEvenue.Api/     - ASP.NET Core Minimal API: POST /api/seatavailability. Same request
                          contract as python/paciolanevenue/api.py.
PaciolanEvenue.Tests/   - xUnit: EventPageParserTests, SeatAvailabilityParserTests, GroupingTests,
                          ListingIdentityTests (cross-checked against the Python port's own test
                          vectors). 23/23 green on macOS.
```

## Seat availability is CAPTURED, not fetched (2026-09-25)

`PaciolanEvenue.Fetch.Playwright` no longer calls the seat-availability API with its own in-page `fetch()`. Microsoft.Playwright
evaluates in the page's main world, and PerimeterX answered that call with 403 on every run (0/1, and 0/2 with the NuGet
`Patchright`). The event page requests the same API by itself, and the client now captures that response (`IPage.Response`,
listener attached before navigation, waits up to 15s, then retries with a new session). With stock Microsoft.Playwright 1.48:
3/3 experiment runs, then real runs **Oklahoma F03 OK on attempt 1 in 13.9s** and **Purdue F06 OK** after new sessions.
The output is identical to Python (127 listings / 1,037 seats for F03). Patchright is not needed here.
Evidence: `../python/paciolanevenue/docs/EVENUE_OPTIMIZATION_FINDINGS.md`.

## Purchase-quantity rules (2026-09-25)

The crawler stores what eVenue sends and computes nothing. From the event page SSR (`discovery_eventDetailMPT[0]`):

| Mongo field | Source | soonersports F26/F03 |
|---|---|---|
| `MinQuantity` / `MaxQuantity` / `QuantityIncrement` | event `MINQTY` / `MAXQTY` / `MULTIPLEQTY` | null / 8 / null (raw 0 / 8 / 0) |
| `PriceLevelMinQuantity` / `PriceLevelMaxQuantity` / `PriceLevelQuantityIncrement` | `PLPT_MINQTY` / `PLPT_MAXQTY` / `PLPT_MULTIPLE` of the same `PL_PT_PRICES` row the `Price` comes from | null / null / null (raw 0 / 0 / 0) |

`STUDENTMAXQTY` / `PLPT_STUDENTMAXQTY` are not stored: they only apply to the student purchase flow and were never set on 60 real events.

`null` means eVenue didn't send the field **or sent `0`**: eVenue uses 0 for "not set" (user decision 2026-09-25, same for AXS). Other "no value" fields are null too, not `""`/`0`: `PriceLevelId` (eVenue `PRICELEVELCD` as a number - the only price-level field stored, same name as Broadway/AXS; null if the code is not numeric), `Zone`, `PriceClass`, `SeatKeys` (GA quantity listing). eVenue sends no split list, so `Splits` stays `null`.

## Listing rule (2026-09-25)

`SeatGrouper.BuildListings` is a 1:1 port of `python/paciolanevenue/grouping.build_listings`. The rule and the evidence are in `python/paciolanevenue/docs/EVENUE_INVENTORY_RULES.md`. It matches Python listing by listing on the real fixtures in `PaciolanEvenue.Tests/Fixtures/paciolan_rules/` (`expected_python.json`).

- `GetEventMapAsync` (Playwright and WebView2 clients) POSTs GraphQL `maps_eventMap` in the page to get HOLDCODES + SEATING_TYPES. It never throws. On failure the grouper uses `AVAILABLE == 1` and the coverage says `map=unavailable(...)`.
- `GetSeatAvailabilityAsync` (Playwright):
  - Quantity-only pages (`AllowSeatMap == false`) never request seat availability, so it fetches in the page instead of waiting for a capture.
  - If a seat-map page has not requested it after 15s, it tries one in-page fetch before opening a new session.
- Real runs on macOS, 2026-09-25, via proxy:

| Event | Result | Same as Python |
|---|---|---|
| Oklahoma SBF26/FS02 (GA, no seat map) | 1 listing, **6,879 GA tickets** (0 before) | yes |
| Purdue F26/F04 | 1,160 listings, 8,734 tickets | yes |
| UCLA N26/370 (Royce Hall) | **blocked**: the page never requested the seat map; own fetch got 403 (PerimeterX vs stock Playwright). Python/patchright: 87 listings | n/a |

## Cross-platform crawl (Playwright) - works on macOS/Linux now

```bash
cd PaciolanEvenue.Cli
# first run only - installs a Chromium build into ~/Library/Caches/ms-playwright (macOS) or the
# platform equivalent, via the Microsoft.Playwright.CLI dotnet tool:
dotnet tool install --global Microsoft.Playwright.CLI
export PATH="$PATH:$HOME/.dotnet/tools"   # if not already on PATH
playwright install chromium

export PACIOLAN_PROXY_HOST=...
export PACIOLAN_PROXY_PORT=...
export PACIOLAN_PROXY_USER="...-session-{SESSIONID}"   # literal {SESSIONID} - substituted per retry
export PACIOLAN_PROXY_PASS=...
dotnet run --project . -- --host soonersports.evenue.net --season F26 --item F03
```

`--headless` runs Chromium invisibly (default is a real, visible-but-off-Playwright's-own-window
browser - PerimeterX blocks headless, same reason every other client in this repo defaults to
`headless=False`/`false`). `--retries N` (default 4), `--out DIR` (default `output/`). Writes
`event.json`/`listings.json` under `{out}/{host}/{season}/{item}/` - **not committed**
(`dotnet/.gitignore` excludes `PaciolanEvenue.Cli/output/`).

No Mongo write from the CLI (matches `python/paciolanevenue/cli.py` - manual test only). Wire up
`PaciolanEvenueInventoryStore` yourself (see `PaciolanEvenue.Api/Program.cs`'s `GetStore()`/
`BuildDocs()` for the exact shape) if you need this path to persist.

## Step 1 (WebView2/.Api only, CONFIRMED PASSING 2026-09-22): WebView2 gets past PerimeterX

On a **real Windows machine**, start the API (below) and call it with a real event page URL:

```bash
curl -X POST http://localhost:5283/api/seatavailability ^
  -H "Content-Type: application/json" ^
  -d "{\"eventId\":\"F06\",\"url\":\"https://purduesports.evenue.net/event/F26/F06\"}"
```

Expected: an Edge (WebView2) window appears off-screen, PaciolanEvenueBrowser polls for a
PerimeterX block marker to clear (up to `SettleMaxSeconds`, default 15s), then the API returns
`{eventId, sourceEventId, event, priceLevels, listings, coverage}`, no 502. Per
`python/paciolanevenue/docs/EVENUE_PERIMETERX_FINDINGS.md`, one attempt is NOT expected to reliably pass - the retry loop
(`Retries`, default 4) opens a brand-new WebView2 environment with a fresh proxy `{SESSIONID}` each
time, which is what actually got through in the Python package's real runs (2/2 successful, one of
them only on the 3rd retry). A 502 after all `Retries` are exhausted with a PerimeterX message in
the detail is expected occasionally, not necessarily a bug - see that document before concluding
the technique doesn't work.

**2 real bugs found + fixed 2026-09-22**, both via real Windows runs against `F26/F03` (a
KNOWN-GOOD event previously verified by the Python client) - a **follow-up Windows run after both
fixes confirmed the endpoint now works end-to-end**:

1. `PaciolanEvenueBrowser.SettleAsync()` returned the instant no block marker was found, but never
   waited for the page to actually finish (re)loading after the PerimeterX challenge clears - the
   Python client's own `_settle()` does (`wait_for_load_state("networkidle", timeout=3000)` *after*
   the marker check passes), and the C# port had silently dropped that step. Fixed by polling
   `document.readyState` for up to 3s once markers clear, before reading the page.
2. Even with (1) fixed, the same 502 kept happening. Real diagnosis (added a diagnostic dump of the
   retrieved HTML to the exception message, see `PaciolanEvenueClient.GetEventAsync`) showed a
   THIRD PerimeterX response tier: a real `<title>` (so it doesn't look empty/wrong) but the
   Next.js `__NEXT_DATA__` island stripped entirely, no block-marker text either - a "soft block"
   neither `SettleAsync` nor `EventPageParser` recognized as a block. Fixed by treating a page with
   no `__NEXT_DATA__` at all as suspected-blocked (retry with a fresh session), distinct from "has
   `__NEXT_DATA__` but `context != 'eventdetailpage'"` (genuinely not an event page - itemCd wrong,
   does not retry).

## Running the API

```bash
cd PaciolanEvenue.Api
dotnet run
```

Listens on `http://localhost:5283` (see `Properties/launchSettings.json`). Swagger UI:
`http://localhost:5283/swagger`.

### `POST /api/seatavailability`

Body: `{ eventId, url, proxy? }`

| field | default | meaning |
|---|---|---|
| `eventId` | - | must equal the `itemCd` segment parsed from `url`, or 400 |
| `url` | - | the event page: `https://{host}/event/{seasonCd}/{itemCd}` |
| `proxy` | none | `scheme://[user:pass@]host:port` (may contain the literal `{SESSIONID}` placeholder in the username - a fresh id is substituted on every WebView2 open) or raw `host:port:user:pass` |

Response: `{ eventId, sourceEventId, event, priceLevels[], listings[], coverage }` -
`sourceEventId` = `{host}:{seasonCd}:{itemCd}` (matches the Mongo document's `SourceEventId` and
the `.NET` Rowing bot's `ListingIdentity.BuildPaciolanEvenue` preimage). `coverage` is the same
`rows=… available=… capacityMatch=… availableMatch=…` note the Python/`.NET` Rowing sides produce.

### Mongo persistence (env vars, all optional)

```
MONGO_URI   default "mongodb://localhost:27017"
MONGO_DB    default "broadwaydirect"
```

**Not a mirror - the only place PaciolanEvenue listing data lands**, matching the REAL `.NET`
Rowing bot (`ETECH.Application.MarkAutomation/Rowing/PaciolanEvenue/`, confirmed 2026-09-22 against
`SettingFactory.GetIntegrationNewInventoryCollectionName` + every reference to it in that
codebase - NOT the per-event-collection convention `StubHubInventoryStore` above uses, which was
never cross-checked against StubHub's own real Rowing bot):

- **one SHARED collection for the whole datasource**, `PaciolanEvenue_Inventories_NEW` (no event
  id in the name), indexed on `SourceEventId`;
- each request **deletes only that event's documents** (`DeleteMany({SourceEventId: ...})`) then
  inserts the fresh set - never drops or wipes the whole collection, since other events' documents
  live there too;
- one document per listing (`PaciolanEvenueListing` - PascalCase fields matching
  `IntegrationTemplateSourceInventory`/`PaciolanEvenueSourceInventory`), `_id` =
  `ListingIdentity.BuildPaciolanEvenue(sourceEventId, level, section, row, lowSeat, highSeat)`
  (Level+Section combined in the fingerprint - Section alone would collide two sections sharing a
  level);
- `Zone`/`PriceClass` come from the matched price level's `PL_DESC`/`PT`; `Price`/`DisplayPrice` =
  the Public ("P") price level's `PRICE` (else the first available type), divided by 100 (~confirmed
  cents, not 100% - see `python/paciolanevenue/README.md` "Open questions").

If Mongo is unreachable or the write fails the request returns **500** (the fetch succeeded but the
data is not persisted) - it is not swallowed, same as StubHub.Api/TicketMaster.Api. Fetch tuning
defaults live in `PaciolanEvenue.Api/appsettings.json` under `"Fetch"`.

## Notable differences from the Python version

- **Retry = brand-new WebView2 environment, not session reuse.** Unlike
  `BroadwayDirect.Fetch.ProxyEnvironmentPool` (Cloudflare - one warmed session reused across calls)
  and `StubHub.Fetch.DataDomeBrowser` (DataDome - one persistent control, cookies cleared per
  event), `PaciolanEvenueBrowser.OpenFreshAsync` disposes any existing control/environment and
  creates an entirely new one (new profile dir, new proxy `{SESSIONID}`) every time - matching
  `python/paciolanevenue/client.py`'s own `_open()` exactly. This is the literal
  "retry-with-fresh-proxy-session" finding from `python/paciolanevenue/docs/EVENUE_PERIMETERX_FINDINGS.md`, not a stealth
  trick layered on top - PerimeterX bypass here is not guaranteed on any single attempt.
- **Block detection is TEXT MARKERS, not a named cookie** - `PaciolanEvenueBrowser` polls
  `document.documentElement.outerHTML` for `BlockMarkers` (`"Access to this page has been denied"`,
  `"px-captcha"`, `"Please verify you are a human"`), same list as the Python client and the
  `.NET` Rowing bot's `PaciolanEvenueFetchClient`, instead of Cloudflare's `cf_clearance` cookie
  check.
- **Reads page HTML directly, not via fetch()/postMessage** - `ReadOuterHtmlAsync` is a synchronous
  script (`ExecuteScriptAsync` marshals its return value correctly); only the seat-availability API
  call goes through the postMessage workaround (`FetchInPageAsync`), since that one is async/Promise-based.
- **Proxy is per instance, not per call**: one `PaciolanEvenueClient` (one `WebView2Host`) per proxy
  *template* (may contain `{SESSIONID}`), pooled in `Program.cs` - same as
  `python/paciolanevenue/api.py`'s proxy handling. Each retry inside that client still gets a fresh
  substituted session id.
- **No `headless` switch** - always a real (off-screen) window; PerimeterX blocks headless, as the
  Python side already found.

## Testing

```bash
dotnet test PaciolanEvenue.Tests/PaciolanEvenue.Tests.csproj
# or the whole solution (BroadwayDirect + StubHub + TicketMaster + PaciolanEvenue):
dotnet test Crawlers.sln
```

`PaciolanEvenue.Tests` runs green 23/23 on macOS (Core only - no WebView2), cross-checked against
fixtures built from the documented recon field names (RECON.md) and against the Python port's own
`ListingIdentity` test vectors. The solution total is 76/76 (17 BroadwayDirect + 13 TicketMaster +
23 StubHub + 23 PaciolanEvenue).
