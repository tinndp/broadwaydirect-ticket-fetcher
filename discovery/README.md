# event-discovery

Standalone copies of the step-5 discovery tools: list every event of a ticket site (id, URL, local/UTC time, venue) as a CSV, and optionally sync it into `S4K_<DS>_SourceEvents`. Every site has the same shape as its Rowing integration (`ETECH.Application/Rowing/<DS>/…Discovery`): a Node/TypeScript `*.Crawler` (CLI) and an Electron `*.App` that imports the crawler's `dist/`.

| Folder | Site | Source of events | Rowing copy |
|---|---|---|---|
| [`BroadwayDirectShowDiscovery/`](BroadwayDirectShowDiscovery/README.md) | Broadway Direct (Tixtrack) | show sitemap → per-show month API | `Rowing/BroadwayDirect/BroadwayDirectShowDiscovery` (newer, with SQL sync) |
| [`AXSEventDiscovery/`](AXSEventDiscovery/README.md) | AXS | AXS search API, one day per query | `Rowing/AXS/AXSEventDiscovery` (same code; only `App.config` differs) |

Run any of them the same way:

```bash
cd <Site>/<Site>.Crawler && npm install && npm run build
npm run cli -- --out events.csv              # CLI
cd ../<Site>.App && npm install && npm start  # desktop app
```

Node + Electron only (no Python). `node_modules/` and `dist/` are build output.
