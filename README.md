# ticket-crawlers

Prototype and reference implementations of the ticket-site crawlers that end up in Rowing
(`ETECH.Application/Rowing/<DS>`). Every site goes through the same steps:
recon → Python fetcher → .NET on macOS → Rowing on Windows → event discovery (Node).
This repo holds steps 2, 3 and 5. Broadway Direct is the standard; the others follow its layout.

```
ticket-crawlers/
├── python/        step 2 - one package per crawler (+ shared/), each with tests/ and docs/
├── dotnet/        step 3 - Crawlers.sln, one folder per crawler: <Site>.Core/.Fetch/.Api/.Tests (+ README)
└── discovery/     step 5 - Node crawler + Electron app per site (same names as the Rowing copy)
```

| Crawler | Python | .NET | Discovery | Anti-bot |
|---|---|---|---|---|
| Broadway Direct | [`python/broadwaydirect`](python/broadwaydirect/README.md) | [`dotnet/BroadwayDirect`](dotnet/BroadwayDirect/README.md) | [`discovery/BroadwayDirectShowDiscovery`](discovery/BroadwayDirectShowDiscovery/README.md) | Cloudflare |
| StubHub | [`python/stubhub`](python/stubhub/README.md) | [`dotnet/StubHub`](dotnet/StubHub/README.md) | - | DataDome |
| Ticketmaster | - | [`dotnet/TicketMaster`](dotnet/TicketMaster/README.md) | - | Kasada-style |
| Paciolan eVenue | [`python/paciolanevenue`](python/paciolanevenue/README.md) | [`dotnet/PaciolanEvenue`](dotnet/PaciolanEvenue/README.md) | - | PerimeterX |
| AXS | [`python/axs`](python/axs/README.md) | [`dotnet/AXS`](dotnet/AXS/README.md) | [`discovery/AXSEventDiscovery`](discovery/AXSEventDiscovery/README.md) | Cloudflare Turnstile |

## Run

```bash
cd python && python3 -m pytest                                   # all Python tests
cd python && python3 -m axs.cli event --url https://www.axs.com/events/…
cd dotnet && dotnet build Crawlers.sln && dotnet test Crawlers.sln
cd dotnet && dotnet run --project AXS/AXS.Api                     # needs MONGO_URI for the Mongo mirror
cd discovery/AXSEventDiscovery/AXSEventDiscovery.Crawler && npm install && npm run build && npm run cli -- --out events.csv
cd discovery/AXSEventDiscovery/AXSEventDiscovery.App && npm install && npm start
```

Settings that must never be committed:
- `python/paciolanevenue/_local_proxy.py`
- `python/output/`, `dotnet/**/output/`
- `discovery/**/*.App/App.config` (copy `App.config.example`)
- Mongo and SQL credentials come from the environment or Docker (`MONGO_URI`; SQL `sa` from the `etech_database-*` container).

## Scope

These tools fetch public data, group seats into listings and store them (MongoDB, `S4K_<DS>_SourceEvents`). They do not push listings to any resale marketplace; see `python/broadwaydirect/README.md` "Scope & limitations".
