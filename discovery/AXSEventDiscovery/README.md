# AXSEventDiscovery

Lists **every upcoming AXS event**: id, URL, local and UTC time, venue, and how it is sold. The result is synced into `S4K_AXS_SourceEvents`, the table `EA.TWS.AXSCrawlerServer` reads to schedule the inventory crawls of `AXSCrawlerBot`. This is the discovery counterpart of those crawlers, modeled on `Rowing/BroadwayDirect/BroadwayDirectShowDiscovery`.

The same folder exists in two places with identical code:
- `Rowing/AXS/AXSEventDiscovery`, in the ETECH repo;
- `~/etech/ticket-crawlers/discovery/AXSEventDiscovery`, standalone, next to `BroadwayDirectShowDiscovery`.

Only `AXSEventDiscovery.App/App.config` differs: it is empty in git and filled locally.

- **`AXSEventDiscovery.Crawler/`**: Node/TypeScript crawler and CLI (patchright + Google Chrome).
- **`AXSEventDiscovery.App/`**: Electron UI copied from `BroadwayDirectShowDiscovery.App`. It imports the crawler's `dist/` unchanged, including `dates.js`, so the date rules are the CLI's. It adds a form, a live log, an auto-repeat schedule and Save CSV. It syncs to SQL automatically when `App.config` has a `ConnectionString`.

`NOTES.md` has the measurements and the reasons for each choice (in Vietnamese).

## How it works

1. Launch Google Chrome (`channel: "chrome"`, visible window, optional proxy). Pass Cloudflare once on `www.axs.com/venues`, then move to `www.axs.com/robots.txt`.
2. From that page, `fetch()` AXS's own search API one day at a time:
   `unifiedapisearch.discovery-prod.axs.com/v1/Discovery/Events?filterCriteria=Date&dateStart=…&dateEnd=…&results=100&page=N`.
   One query is capped at 10,000 results, so the walk goes day by day. A day that still hits the cap is split in two.
3. Dedupe by `eventId` (multi-day events appear on several days). Keep upcoming events (start, or end for multi-day events, after now). Write a CSV and/or sync to SQL.
4. If the API starts refusing (HTTP 0/403/429 on every retry), or the page/browser dies (window closed, crash), relaunch and redo the same day, at most 5 sessions. A relaunch gets a new proxy session when a proxy is set.

Why the Chrome window shows `www.axs.com/robots.txt` during a run:
- The API only answers `fetch()` calls made from a www.axs.com page; curl gets 403.
- `/venues` reloads itself and breaks in-flight calls, while plain-text robots.txt never does.
- Nothing is read from robots.txt itself.

| File | Role |
|---|---|
| `Crawler/src/browser.ts` | launch Chrome (+ proxy `{SESSIONID}`), pass Cloudflare, in-page `fetch()` batch |
| `Crawler/src/search.ts` | search API URL, day windows, paging, retry, split at the 10,000 cap |
| `Crawler/src/events.ts` | search hit → `EventRow` (UTC gets its `Z`), "upcoming" rule |
| `Crawler/src/dates.ts` | start / end (included) / days → range, shared by the CLI and the app |
| `Crawler/src/crawl.ts` | the one orchestrator (CLI and app): session, relaunch, dedupe |
| `Crawler/src/csv.ts`, `cli.ts` | output and command line |
| `App/src/main.js` | form → `runCrawl` → CSV / SQL sync, schedule |
| `App/src/mssqlSync.js` | MERGE into `S4K_AXS_SourceEvents` (Broadway's code) |

## Run

```bash
cd AXSEventDiscovery.Crawler && npm install && npm run build
npm run cli -- --out events.csv                                   # today + 180 days
npm run cli -- --start 2026-10-01 --end 2026-10-31 --out oct.csv  # 1..31 Oct, the end day is included
npm run cli -- --days 30 --proxy "http://user-{SESSIONID}:pass@host:port"

cd ../AXSEventDiscovery.App && npm install && npm start           # desktop app
```

On Windows (Rowing copy), `run.bat` installs, builds and starts the app, and `run-app.bat` only starts it.

| CLI option | Default | |
|---|---|---|
| `--start YYYY-MM-DD` | today (UTC) | first day; a past date is replaced by today |
| `--end YYYY-MM-DD` | - | last day, **included** |
| `--days N` | 180 | used when there is no `--end` (not both). 180 = +6 months like Broadway; events thin out after that (Jan 2028: 35 events in the month). Max 730 |
| `--out` | `events.csv` | |
| `--proxy` | env `AXS_PROXY`, else none | `{SESSIONID}` is replaced on each launch |
| `--concurrency` | 4 | parallel pages inside one day |
| `--axs-only` | off | drop events AXS lists but doesn't sell (the app does this by default) |
| `--headless` | off | not recommended - Cloudflare |

Exit code 2 means some day windows could not be fetched completely; they are listed at the end. Either pages kept failing, or an hour was still over the 10,000 cap. This is not the same as a day with 0 events.

App form: start date, end date (included; empty = use Days), days (default 180, max 730), "include listed-only events".

`App.config` keys:
- `ConnectionString`: optional. When set, every crawl is MERGEd into `S4K_AXS_SourceEvents`.
- `Proxy`: optional, with `{SESSIONID}`.

For local tests, the SQL settings come from the Docker SQL Server: container `etech_database-*`, `192.168.100.1:1433`, user `sa`, password from `printenv SA_PASSWORD`.

## CSV columns

- `event_url`, `event_name`, `event_id`
- `event_datetime`: local time, as AXS returns it.
- `event_datetime_utc`: with `Z`.
- `event_end_datetime_utc`: multi-day events only.
- `timezone`, `venue`, `venue_id`, `city`, `state`, `country_code`, `performer_ids`, `category`
- `axs_ticketed = true`: primary tickets on AXS (the Veritix flow of the inventory crawler).
- `axs_marketplace = true`: AXS resale marketplace (the Marketplace flow).
- `axs_ticketed` and `axs_marketplace` both false: AXS only lists the event, and tickets are sold elsewhere.
- `ticketing_status`, `date_tbd`, `onsale_datetime_utc`
- `description`: AXS HTML, as returned.

## How it differs from BroadwayDirectShowDiscovery

| | Broadway | AXS | Why |
|---|---|---|---|
| Source | sitemap → show pages → per-show month API | AXS search API, one day per query | The AXS sitemaps hold about 170k event URLs, past ones included, with no dates. Checking them one by one is about 50 hours. The search API filters by date. |
| Browser | bundled Chromium | Google Chrome (`channel: "chrome"`) | AXS Turnstile gets stuck on bundled Chromium |
| Fetch page | the warmed-up page | `www.axs.com/robots.txt` after the warm-up | `/venues` reloads itself |
| Proxy | none | `--proxy` / `AXS_PROXY` / `Proxy` in `App.config` (optional), relaunch on block | the machine IP gets challenged on www.axs.com |
| Form / CLI | start/end month (default +6 months), days back, all shows | start date, end date (included) or days (default 180), "include listed-only events" | the API filters by date; 180 days = Broadway's +6 months |
| `PerformerID` | Tixtrack series id | AXS `performerIds`, comma-joined | AXS has no series per event |
| `Description` | as returned | AXS HTML converted to text, capped at 1,000 chars | AXS descriptions are full HTML pages (5 KB+), and `Description` is a display column |
| `App.config` values | filled in | empty in git | no connection strings or proxies in the repo |

## SQL sync (`S4K_AXS_SourceEvents`)

`src/mssqlSync.js` is Broadway's code: one transaction, a `#StageSourceEvents` temp table, bulk load, one `MERGE` on `EventID`, and `EventDateEST` computed in SQL. The table is created by `AXSDBCreator` (`IntegrationTemplateDBCreator.EnsureSourceEventsTable`).

| Column | From |
|---|---|
| EventID | `eventId` |
| EventName | `eventTitle` (max 250) |
| EventDateUTC | `eventDatetimeUtc` (real UTC) |
| EventDateEST | computed by SQL from EventDateUTC |
| EventUrl | `eventURL` (`https://www.axs.com/events/{id}/{slug}`), the input of `AXSCrawlerBot` |
| PerformerID | `performerIds`, comma-joined (max 250) |
| Venue, City | `venue.venueTitle`, `venue.address.city` |
| Description | `description` as text (max 1,000) |
| Enabled | 1 |

By default the app leaves out events AXS only lists (not AXS-ticketed and not on the AXS marketplace), because they have no inventory for the AXS crawler. The "include listed-only events" checkbox keeps them.

## Verified on macOS (2026-09-26)

- The crawler ran for real through the proxy; the numbers are in `NOTES.md`. One example: `--start 2026-10-20 --end 2026-10-21` gave 1,505/1,505 hits and 1,499 events from 16 requests in 34s.
- A discovered `event_url` goes straight into the step-2 fetcher: `python3 -m axs.cli event --url https://www.axs.com/events/1156220/blue-man-group-tickets` read the same id, name, UTC time and venue as the CSV row.
- `syncSourceEvents` was run against SQL Server 2019 in Docker (scratch database `AXSDiscoveryTest`):
  - 3,440 rows gave `3440 inserted, 0 updated`; the same rows again gave `0 inserted, 3440 updated`.
  - `EventDateUTC` was stored unchanged.
- The Electron app starts without errors and loads `dates.js` from the crawler. The form → crawl → sync flow was **not** clicked through (no desktop automation here).

## Open

- Which events go to `S4K_AXS_SourceEvents`: all, AXS-sold only, US only, a venue list… A full year is 150k+ events, far more than the inventory IP budget.
