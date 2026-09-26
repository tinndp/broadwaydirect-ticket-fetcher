# BroadwayDirectShowDiscovery (standalone)

Discovers **every show currently ticketed through Broadway Direct's
Tixtrack platform** (`tickets.broadwaydirect.com`) and crawls **every
scheduled performance** for each one, producing a flat CSV of:

| column | meaning |
|---|---|
| `event_url` | `https://tickets.broadwaydirect.com/shop/tickets/series/{series_id}/{event_id}` |
| `event_name` | show name as returned by the API |
| `event_id` | Tixtrack event/performance ID |
| `event_datetime` | local performance datetime (ISO 8601) |
| `series_id` | Tixtrack series ID (one per show/production) |
| `show_slug` | the `broadwaydirect.com/show/{slug}/` marketing-page slug |

This is a separate, independent project from
[`ticket-crawlers`](..) (which
fetches per-seat inventory for one event you already know the ID/URL for).
This tool answers the opposite question: *"what are all the events, and
what are their IDs/URLs?"*

Two projects, named like the Rowing integration
(`Rowing/BroadwayDirect/BroadwayDirectShowDiscovery`), which is the newer,
SQL-syncing version of this code:

- **[`BroadwayDirectShowDiscovery.Crawler/`](BroadwayDirectShowDiscovery.Crawler/README.md)** -
  TypeScript crawler + CLI (`patchright`).
- **[`BroadwayDirectShowDiscovery.App/`](BroadwayDirectShowDiscovery.App/README.md)** -
  a minimal desktop GUI (form + live progress log + "Save CSV as...")
  wrapping the crawler's `dist/` directly - no duplicated crawler logic.

A full crawl takes about **~20 seconds** (~35 shows, ~4100 performances,
6-month window). (The original Python version was retired on 2026-09-26 -
discovery is Node + Electron only.)

```bash
cd BroadwayDirectShowDiscovery.Crawler && npm install && npm run build
npm run cli -- --start 2026-09 --end 2027-02 --out events.csv
cd ../BroadwayDirectShowDiscovery.App && npm install && npm start
```

## Why a real browser (not plain HTTP)

Both `broadwaydirect.com` (marketing site, used for discovery) and
`tickets.broadwaydirect.com` (the ticketing API) sit behind their own
Cloudflare Managed Challenge. A plain HTTP client gets a 403 "Just a
moment..." on either host, and headless Chromium is reliably detected and
blocked too - a real, visible (`headless: false`) browser is required.

Every data call runs `fetch()` **inside** an actual page (not a separate
HTTP client like Playwright's `context.request`),
which goes through the browser's real network stack - the only approach
that was 100% reliable in testing.

**Key measured finding**: `cf_clearance` for `tickets.broadwaydirect.com`
is domain-wide, not per-series - one Cloudflare pass (on any single
series's page) unlocks every other series's API immediately. An earlier
version of this code navigated to every series's own page first (one
Cloudflare pass each); that was unnecessary and took ~5-8 minutes for the
event-crawl stage alone. Bootstrapping once cut that to ~15 seconds for
the same ~216 requests.

## Rate limits (no proxy)

Every request comes from a single IP, no proxy. Measured directly against
`tickets.broadwaydirect.com`:

- Bursts of up to 200 concurrent `getbymonth` calls, and 745 requests in
  under 3 minutes total: **0 blocks, 0 non-200 responses**.
- 36 back-to-back full-page Cloudflare navigations, no delay between them:
  **0 failures**, consistently ~3.8s each.

No hard rate limit was found within that tested range (a ~1-2 hour
session, ~2000+ requests total, measured with the Python and Node versions). That only
covers that window, though - it says nothing about longer-horizon
behavioral heuristics Cloudflare's bot management might apply over many
hours/days. As a sensible default: avoid running this continuously
back-to-back with no gap; once every 30-60 minutes is well within what's
been verified safe and is already fast enough to be practically
real-time.

## Scope / limitations

- Only covers shows ticketed through `tickets.broadwaydirect.com`
  (Tixtrack). Shows sold via Telecharge, ATG, or other 3rd-party systems
  are outside this project's scope.
- The `--start`/`--end` window only pulls performances scheduled in that
  range - a show on sale further out than `--end` will be undercounted.
  Widen the range if needed.
- If a series's every month request fails (distinct from a show
  legitimately having 0 performances), it's dropped from the CSV and
  listed separately in the run's warning output.
