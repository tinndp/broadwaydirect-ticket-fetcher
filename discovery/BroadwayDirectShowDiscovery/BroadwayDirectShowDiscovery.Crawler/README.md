# BroadwayDirectShowDiscovery.Crawler (Node/TypeScript)

TypeScript crawler + CLI. It started as a port of a Python version (retired
2026-09-26) and matched it row-for-row (4122 performances / 35 shows for the
same date range). Cross-platform (macOS/Linux/Windows).

Uses [`patchright`](https://www.npmjs.com/package/patchright) (an
anti-detection Playwright fork) instead of plain `playwright`: Cloudflare
reliably detects and blocks stock Playwright/headless Chromium.

## Usage

```bash
npm install                 # also downloads Chromium (postinstall)
npm run build
npm run cli -- --start 2026-09 --end 2027-02 --out events.csv
```

Flags: `--start`, `--end` (YYYY-MM, default: current
month through 6 months out), `--out` (default `events.csv`), `--days-back`
(default 75), `--all-shows` (ignore `--days-back`, check every show ever
listed), `--headless` (not recommended - see below).

## Architecture (measurements in `../NOTES.md`)

1. **`src/sitemap.ts`** - one Cloudflare pass on `broadwaydirect.com`,
   then batched in-page `fetch()` over every recently-modified
   `/show/{slug}/` page from `show-sitemap.xml`, keeping the ones that
   link to a `tickets.broadwaydirect.com/.../series/{id}` ticket page.

2. **`src/events.ts`** - one Cloudflare pass on `tickets.broadwaydirect.com`
   (`bootstrap()`, using any single series's ticket page - the resulting
   `cf_clearance` is domain-wide, not per-series, so this unlocks every
   other series's API immediately), then one large batched in-page fetch
   across every `(series_id, month)` pair.

`headless: false` is required - Cloudflare detects and blocks headless
Chromium.

## Output

Six columns: `event_url`, `event_name`,
`event_id`, `event_datetime`, `series_id`, `show_slug`.
