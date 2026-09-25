# paciolanevenue (Python)

Pulls seat-availability data from Paciolan eVenue college-athletics ticketing sites
(`*.evenue.net` - Purdue `purduesports.evenue.net`, Oklahoma `soonersports.evenue.net` used for
recon/testing), normalized into `price_levels` + `listings`, same shape philosophy as the sibling
`broadwaydirect`/`stubhub` packages. See:

- `../../ETECH.Application.MarkAutomation/ETECH.Application/Rowing/PaciolanEvenue/RECON.md` - the
  site recon this is built from (endpoints, field meanings, what's confirmed vs not).
- `../EVENUE_PERIMETERX_FINDINGS.md` - what was tried before this package (a plain .NET
  `Microsoft.Playwright` crawler, then a throwaway `patchright` probe) and why this package is
  built the way it is.

Run everything from the `python/` directory (`cd python` first).

## How it differs from broadwaydirect / stubhub

| | broadwaydirect | stubhub | paciolanevenue |
|---|---|---|---|
| Bot wall | Cloudflare Managed Challenge | DataDome | **PerimeterX** |
| Reliability found here | reliable | reliable | **NOT reliable per attempt** - see below |
| Fetch | `context.request.get`, pooled session | in-page `fetch()`, fresh context per event | in-page `fetch()`, retry-with-fresh-proxy-session |
| Grouping | seats -> listings (theater rules) | none (StubHub bundles listings already) | seats -> listings (stadium rules, see "Open questions") |
| Discovery | `getbymonth` API lists every performance | n/a (eventId supplied) | **unsolved** - see `README` note below |

## PerimeterX: what actually works here (read before changing `client.py`)

Cloudflare and DataDome are both reliably passed in this repo with a fairly simple recipe: a real
`headless=False` `patchright` browser, navigate to the right page, POLL until the challenge clears
(not a fixed sleep), then call the API. `paciolanevenue/client.py` does exactly that too (see
`_settle`) - but on its own it was **not reliable** against PerimeterX (`EVENUE_PERIMETERX_FINDINGS.md`:
5 real attempts, 3 different outcomes, same code, only the proxy's egress IP changing).

What made the difference, empirically (2026-09-21, `paciolanevenue.cli`, real runs against the real
site through a residential proxy): **retrying with a fresh proxy `{SESSIONID}` (a new egress IP)
when a block is detected**, same idea as how the .NET/WebView2 crawlers in the main
`ETECH.Application.MarkAutomation` repo (TM/Broadway) handle a flagged IP mid-run. Two live runs
with this client, `retries=4`:

- Purdue (`F06`): blocked on attempts 1-2 (fresh IP each time), **succeeded on attempt 3**.
- Oklahoma (`F03`): **succeeded on attempt 1**.

Both fully succeeded end to end (event page + seat-availability API + grouping), with
`capacityMatch=yes` on both and `availableMatch=yes` on Oklahoma (Purdue's available count differs
from the SSR-reported number because seat availability is LIVE and changes between the page load
and the API call moments later - not a bug, see RECON.md P9/"dữ liệu sống").

**Conclusion: this looks like proxy IP reputation, not fundamentally un-passable PerimeterX
protection** - the same technique (`patchright` + poll-until-clear) that works for Cloudflare/
DataDome here also works for PerimeterX, just not on the first try with this specific
(shared/scraping-marketed) residential proxy pool. `retries` defaults to 4 (higher than
Broadway's 3) to reflect that. If you have access to a higher-reputation proxy, it's worth
re-testing with a lower `retries` and see if attempt 1 succeeds more often - would also help
separate "IP reputation" from "some inherent extra PerimeterX friction" as the dominant factor,
which is still not fully isolated (see `EVENUE_PERIMETERX_FINDINGS.md`).

## Several events on one host: warm-page fast path (2026-09-25)

Once a page on a host has passed PerimeterX, `get_event` for the next event on the **same host** fetches that event's
page HTML with an in-page `fetch()`. patchright evaluates in an isolated world by default. It does not relaunch the
browser or navigate, and the seat API is fetched the same way. Measured result: **about 4s per event instead of 10–34s**,
with no new challenge. Navigating to the next event page was re-challenged 3/3 times, so the fast path never navigates.
Any block on the fast path falls back to the normal fresh-session retry. `api.py` keeps one client per `(host, proxy)`,
so this also applies across API requests. The CLI takes a repeatable `--item`.
Evidence: `../EVENUE_OPTIMIZATION_FINDINGS.md`.

## Purchase-quantity rules (2026-09-25)

The crawler stores what eVenue sends and computes nothing. From the event page SSR (`discovery_eventDetailMPT[0]`):

| Mongo field | Source | soonersports F26/F03 |
|---|---|---|
| `MinQuantity` / `MaxQuantity` / `QuantityIncrement` | event `MINQTY` / `MAXQTY` / `MULTIPLEQTY` | null / 8 / null (raw 0 / 8 / 0) |
| `PriceLevelMinQuantity` / `PriceLevelMaxQuantity` / `PriceLevelQuantityIncrement` | `PLPT_MINQTY` / `PLPT_MAXQTY` / `PLPT_MULTIPLE` of the same `PL_PT_PRICES` row the `Price` comes from | null / null / null (raw 0 / 0 / 0) |

`STUDENTMAXQTY` / `PLPT_STUDENTMAXQTY` are not stored: they only apply to the student purchase flow and were never set on 60 real events.

`null` means eVenue didn't send the field **or sent `0`**: eVenue uses 0 for "not set" (user decision 2026-09-25, same for AXS). Other "no value" fields are null too, not `""`/`0`: `PriceLevelId` (eVenue `PRICELEVELCD` as a number - the only price-level field stored, same name as Broadway/AXS; null if the code is not numeric), `Zone`, `PriceClass`, `SeatKeys` (GA quantity listing). eVenue sends no split list, so `Splits` stays `null`.

## Discovery is still unsolved

Same situation as documented in the .NET demo and `RECON.md`: eVenue's real "list every event"
call is a `POST /pac-api/consumer/gql` GraphQL request nobody has decoded yet. This package, like
the .NET one, expects the caller to already know the `itemCd` (or you use the same brute-force
heuristic idea documented in the .NET `Discovery.cs`/`RECON.md` section 5.4 - not re-implemented
here to avoid duplicating that unsolved problem twice).

## Files

```
models.py    - Event, PriceLevel, SeatRow, Listing dataclasses
parser.py    - reads __NEXT_DATA__ off an event page HTML into Event + price_levels
               (port of the .NET demo's EventPageParser.cs)
grouping.py  - seats -> listings, build_listings(): the rule agreed 2026-09-25 after 72 real
               events (../EVENUE_INVENTORY_RULES.md) - GA quantity listings, Odd/Even
               sections detected from the whole seat map, lettered seat codes
client.py    - PaciolanEvenueClient (patchright): get_event() + get_seat_availability(),
               retry-with-fresh-proxy-session on a PerimeterX block (see above)
api.py       - FastAPI, POST /api/seatavailability, mirrors to MongoDB in the
               REAL .NET Rowing staging shape - one SHARED PaciolanEvenue_Inventories_NEW
               collection for the whole datasource, keyed by SourceEventId, one document
               per listing (see mongo_inventory.py; this is NOT the raw_events/cleaned_events
               shape broadwaydirect/stubhub use, and NOT a per-event collection name either -
               see mongo_inventory.py's 2026-09-22 correction note)
cli.py       - manual-test CLI (what was used to get the 2 live runs above)
```

## Run

```bash
cd python
pip install -r requirements.txt
python3 -m patchright install chromium      # first run only

# quick manual test (no server, no Mongo needed):
export PACIOLAN_PROXY_HOST=... PACIOLAN_PROXY_PORT=... PACIOLAN_PROXY_USER="...-session-{SESSIONID}" PACIOLAN_PROXY_PASS=...
python3 -m paciolanevenue.cli --host purduesports.evenue.net --season F26 --item F06

# HTTP API:
python3 -m uvicorn paciolanevenue.api:app --port 8200
curl -X POST http://localhost:8200/api/seatavailability \
  -H "Content-Type: application/json" \
  -d '{"eventId":"F06","url":"https://purduesports.evenue.net/event/F26/F06"}'
```

Proxy (optional, all env vars, matches `ProxyConfig.cs` in the .NET demo):
`PACIOLAN_PROXY_HOST`/`PACIOLAN_PROXY_PORT`/`PACIOLAN_PROXY_USER` (may contain the literal
`{SESSIONID}` placeholder)/`PACIOLAN_PROXY_PASS`. **Never hard-code real values into any tracked
file** - a request-level `proxy` field, `PACIOLAN_PROXY_LIST_PATH` (round-robin pool, same format
as `PROXY_LIST_PATH` for `broadwaydirect`), or these env vars are the only supported ways in.

## Open questions (do not silently guess these - same list as the .NET demo's README)

1. **Fees** - `PriceLevel.per_ticket_fee`/`facility_fee` are kept separate from `price`, never
   summed. Units now look STRONGLY like cents (Purdue's real prices - $125/$95/$60/$45/$40/$30 -
   only make sense divided by 100), though this still isn't 100% confirmed against an actual
   checkout total.
2. **Accessible seating** - `marker_id`/`seat_marker_active` are passed through, unused by
   grouping - see `grouping.py`'s docstring.
3. **Grouping** - resolved 2026-09-25, see `../EVENUE_INVENTORY_RULES.md` (the old inert
   Broadway-style `DEFAULT_RULES` were replaced by rules measured on 72 real events).
