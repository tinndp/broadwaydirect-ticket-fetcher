# BroadwayDirectShowDiscovery.App (Electron desktop UI)

A minimal desktop GUI wrapping the [`BroadwayDirectShowDiscovery.Crawler/`](../BroadwayDirectShowDiscovery.Crawler/README.md) crawler -
**no crawler logic is duplicated here**. This app imports
`../BroadwayDirectShowDiscovery.Crawler/dist/crawl.js` and `csv.js` directly (dynamic
`import()` from the CommonJS main process, since the crawler's output is ESM)
and just adds a form + a live log view + a native "Save As..." dialog
around it.

```
BroadwayDirectShowDiscovery.App/
├── src/
│   ├── main.js      - Electron main process: IPC handlers, imports ../BroadwayDirectShowDiscovery.Crawler/dist/*
│   └── preload.js   - contextBridge, exposes window.crawler to the renderer
└── renderer/
    ├── index.html
    ├── renderer.js  - form submit -> IPC -> live log -> result -> save
    └── style.css
```

## Usage

Build the Node package first (this app imports its compiled output):

```bash
cd ../BroadwayDirectShowDiscovery.Crawler && npm install && npm run build
cd ../BroadwayDirectShowDiscovery.App && npm install
npm start
```

Fill in the start/end month (defaults match the CLI: current month through
6 months out), optionally adjust "days back" or check "all shows", click
**Start Crawl**. A Chromium window opens briefly in the background (same
as the CLI - needed to pass Cloudflare) while progress streams into the
log panel. When done, click **Save CSV as...** to write the result
wherever you like.

## Auto-repeat (schedule)

The "Auto-repeat" box reuses whatever's currently in the form above (same
start/end/days-back/all-shows) and runs it on a timer instead of once:

1. Pick an interval (minutes or hours) and an output folder.
2. Click **Start Schedule** - it runs immediately, then re-arranges the
   next run for `interval` *after the current one finishes* (a
   `setTimeout` chain, not a fixed clock tick), so a slow run can never
   overlap the next one. Each run auto-saves to `{folder}/events-{ISO
   timestamp}.csv` - no dialog, since it can fire unattended.
3. Click **Stop Schedule** at any time; a run already in flight finishes
   normally and does not reschedule itself afterward.

Manual "Start Crawl" and the scheduler share one mutex (`crawlInProgress`
in `main.js`) - trying to run both at once just gets a graceful "a crawl
is already running" instead of two Chromium instances colliding.

Two things this does **not** do, by design (kept to the minimal-UI tier
that was asked for):

- **Not persisted across restarts.** The schedule only runs while this
  app window is open; closing the app stops it. For "run even when the
  app/computer is off", use cron / Task Scheduler with the CLI instead
  (`../BroadwayDirectShowDiscovery.Crawler/README.md`) - that's what OS-level schedulers are for, no
  reason to reinvent one here.
- **No built-in floor on the interval.** The UI shows a soft warning
  under 30 minutes (matches the "every 30-60 minutes" guidance in
  `../NOTES.md`), but doesn't block shorter intervals - the measured
  rate-limit testing there only covered short bursts, not hours of
  sustained repeats at high frequency.

## How progress reaches the UI

`BroadwayDirectShowDiscovery.Crawler/src/*.ts` reports progress via `console.error(...)` (same as the
CLI, which just lets those lines fall through to the terminal). Rather
than touching that already-verified code to add a callback parameter,
`main.js` temporarily wraps `console.error` for the duration of one crawl,
forwarding each line to the renderer over IPC (`crawl-log`) in addition to
printing it normally, then restores the original function. Zero changes
to the crawler modules.

## Known limitation

The native "Save As..." dialog (`dialog.showSaveDialog`) is OS-level UI
that can't be driven by the same in-page automation used to test the rest
of the app (verified: form fill -> Start -> live log -> result all work
end-to-end via a scripted run). It's a thin, standard use of Electron's
own `dialog` module, so this is a low-risk gap, but worth knowing if you
change that code path.
