# Paciolan eVenue (`*.evenue.net`) - patchright probe findings

Ad-hoc probe, 2026-09-21, using `test_evenue_probe.py` in this folder (not part of the
`broadwaydirect` package - a throwaway diagnostic, kept for reference). Goal: find out whether
`patchright` (which gets past Cloudflare for `tickets.broadwaydirect.com` - see this folder's
README) also gets past PerimeterX for `purduesports.evenue.net`, since the earlier attempt with
plain (unpatched) `Microsoft.Playwright` in .NET was reliably blocked
(`ETECH.Application.MarkAutomation/ETECH.Application/Rowing/PaciolanEvenue/PaciolanEvenueCrawler`,
see that project's README "Status" section).

## Setup

- Real, visible Chromium (`headless=False`), `patchright.async_api`, same launch shape as
  `broadwaydirect/client.py`.
- Routed through a residential proxy the user supplied (env vars `PACIOLAN_PROXY_HOST/PORT/
  USER/PASS`, username templated with `{SESSIONID}` - a fresh session/exit IP each run).
- Direct connection was not an option: this sandbox cannot reach `evenue.net` at the TCP level at
  all (confirmed separately, unrelated to PerimeterX).

## Results (5 total real runs against the real site, across both .NET and Python)

| # | Tool | Proxy session | Result |
|---|---|---|---|
| 1 | .NET `Microsoft.Playwright`, headless | fresh | Blocked at page load ("Access to this page has been denied") |
| 2 | .NET `Microsoft.Playwright`, headed | fresh | Blocked at page load ("px-captcha") |
| 3 | Python `patchright`, headed | fresh | Blocked at page load ("px-captcha") |
| 4 | Python `patchright`, headed, +3s wait | fresh | **Page loaded clean** (title/body/`__NEXT_DATA__` all correct) - but the follow-up in-page `fetch()` to `/pac-api/seat-availability/...` returned **HTTP 403** |
| 5 | Python `patchright`, headed, +8s extra wait before the API call | fresh | Blocked at page load again ("px-captcha") |

## Reading of this

- **Not a clean pass/fail** - the same code, same proxy provider, only the exit IP (a fresh
  `{SESSIONID}`) changing between runs, produced 3 different outcomes: blocked at the page, page OK
  but API 403, blocked at the page again. That inconsistency points at **proxy IP reputation**
  mattering a lot here, not just which automation library is used - this residential proxy pool
  likely has a meaningful fraction of exit IPs already flagged by PerimeterX (a common problem for
  proxy providers marketed for scraping).
- `patchright` did demonstrably get further than plain `Microsoft.Playwright` at least once (a full
  clean page load, which the .NET runs never achieved even once) - so the CDP-fingerprint patching
  is doing *something*, consistent with why it works for Cloudflare. It is evidently **not
  sufficient by itself** against PerimeterX the way it was against Cloudflare's Managed Challenge -
  PerimeterX's detection surface is generally understood to be broader (deeper behavioral/
  fingerprint signals, not just CDP artifacts), and the API-route-specific 403 in run #4 suggests
  there may be an additional sensor/token PerimeterX expects before trusting an XHR/fetch call, on
  top of whatever it checks for the page itself.
- Longer wait-before-API-call (run #5) did **not** reliably fix it - that run got blocked at the
  page itself before ever reaching the API call, so the "give the sensor more time" hypothesis
  wasn't cleanly tested (the page-load block happened first, on a different flagged IP).

## What this does NOT tell us

- Whether a **higher-quality/cleaner proxy** (not this specific residential pool) would pass
  reliably - not tested, would need a different proxy to isolate IP reputation from tooling.
- Whether **more real per-run time** (patchright's own docs/community note PerimeterX sometimes
  wants tens of seconds of "natural" page dwell/interaction before trusting a session) would help -
  only one run tested a longer wait, and it was confounded by hitting a flagged IP at the page-load
  step before the wait even mattered.
- Whether real mouse movement / scroll simulation (something neither this probe nor the .NET
  crawler does) matters - PerimeterX is known to weigh interaction telemetry in some
  configurations.

## Recommendation (superseded below - keeping for the record)

Stopping further live probing here rather than continuing to iterate blind (5 attempts already,
inconsistent results, proxy-IP-quality is a confound I can't isolate further with the one proxy on
hand). Next step is a decision, not more code:

1. Try a **different/higher-reputation proxy** (ideally one advertised as not shared with scraping
   traffic) and re-run the same probe - the single highest-leverage unknown right now.
2. If that still fails, invest in interaction simulation (mouse/scroll) and a longer natural dwell
   time before any API call, and re-test.
3. Otherwise, treat eVenue as blocked for the time being and revisit later / de-prioritize relative
   to the sites that already work (Broadway, and TM/StubHub as far as their existing crawlers go).

## Update (same day, after building `paciolanevenue/`): retry-with-fresh-session works

Recommendation #2 above (interaction simulation) was NOT what fixed this. Instead: the exact same
poll-until-clear technique that already works for Cloudflare/DataDome in this repo, PLUS
**retrying with a fresh proxy `{SESSIONID}` (new egress IP) when a block is detected**, same
pattern the .NET/WebView2 TM/Broadway crawlers already use for a flagged IP - see
`paciolanevenue/client.py` and `paciolanevenue/README.md`.

Real results with that client (`retries=4`, same wiredproxies proxy used throughout this whole
investigation - no proxy change):

- Purdue `F06`: blocked attempts 1-2, **succeeded on attempt 3**. Full event + 58,965-row seat map
  fetched, `capacityMatch=yes`, grouped into 1,170 listings from 10,367 available seats.
- Oklahoma `F03`: **succeeded on attempt 1**. `capacityMatch=yes availableMatch=yes` (exact match).

So the earlier "5 inconsistent outcomes" data wasn't evidence PerimeterX can't be passed here - it
was evidence that **one attempt isn't enough**, same as it isn't for TM's Kasada-style blocking in
the main repo. Recommendation #1 (try a different proxy) is still worth doing eventually to see if
it gets attempt-1 success more often (would decide whether to lower `retries` from 4), but is no
longer blocking - the current proxy + retry=4 already works.
