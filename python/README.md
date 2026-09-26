# python/ - one package per crawler

Run everything from this folder (`cd python`); imports are absolute (`axs.cli`, `shared.proxy_pool`).

| Package | Site | Entry points | Docs |
|---|---|---|---|
| [`broadwaydirect/`](broadwaydirect/README.md) | Broadway Direct (Tixtrack, Cloudflare) | `uvicorn broadwaydirect.api:app`, `python3 -m broadwaydirect.reprocess` | README, `config/section_rules.json` |
| [`stubhub/`](stubhub/README.md) | StubHub (DataDome) | `uvicorn stubhub.api:app` | README |
| [`paciolanevenue/`](paciolanevenue/README.md) | Paciolan eVenue (PerimeterX) | `python3 -m paciolanevenue.cli`, `uvicorn paciolanevenue.api:app` | README, `docs/EVENUE_*.md`, `tools/build_excel_export.py` |
| [`axs/`](axs/README.md) | AXS (Cloudflare Turnstile) | `python3 -m axs.cli event --url …`, `uvicorn axs.api:app` | README, `docs/AXS_LISTING_RULES.md`, `docs/RECON.md` |
| `shared/` | - | - | `proxy_pool.py` (proxy list, `{SESSIONID}`), `rowing_mongo.py` (Rowing `ListingIdentity` port + pooled Mongo client) |

Each package keeps its own `tests/` (and `tests/fixtures/`):

```bash
python3 -m pytest              # every package (pytest.ini: pythonpath=., importlib mode)
python3 -m pytest axs          # one package
```

Local-only, never committed: `output/` (raw JSON of manual runs), `paciolanevenue/_local_proxy.py` (proxy URL).
Event lists (step 5) are not in Python - see `../discovery/`.
