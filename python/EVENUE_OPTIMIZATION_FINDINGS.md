# Paciolan eVenue: speed and reliability experiments (2026-09-24)

All runs: `soonersports.evenue.net` F26 (F03 Kentucky, then F04–F07), macOS, the same residential proxy with a
`{SESSIONID}` rotated per browser, about 16:00–18:00 +07. The experiment code lives in the session scratchpad, not in
the repo. Sections 1–4 describe the code **before** the change; section 5 shows the change implemented on 2026-09-25 and re-run.

## 1. Where the time goes today (Python `paciolanevenue`, per event)

| Step | Measured |
|---|---|
| `get_event`: close/relaunch the browser, `goto`, PerimeterX settle, parse | 10–34s (34s when session 1 was blocked) |
| of which the post-settle `wait_for_load_state("networkidle", 3000)` | **always the full 3.0s** (the page never goes idle) |
| seat-availability API (80,395 rows, 3.8 MB) | 1.2–2.7s |
| `api.py` | builds a **new client (new browser) per request**; Broadway pools clients |

The .NET mac `PaciolanEvenue.Fetch.Playwright` has the same shape: `GetEventAsync` always calls `OpenFreshAsync`.

## 2. Experiments

| # | Setup | Result |
|---|---|---|
| E1 | Python, seat API via `page.evaluate(..., isolated_context=True)` (patchright default) vs `False` (main world, which is what .NET does), fresh session each | isolated **3/3 = 200**; main world 1×200, **1×403**, 1× page blocked |
| E2 | Python, record XHR/fetch on the event page | **The event page calls the seat API itself** (`GET /pac-api/seat-availability/...`, same URL and params as `seat_availability_path`, 200 at ~15s) |
| baseline | .NET mac as in the repo (stock Microsoft.Playwright 1.48, own `fetch()` in page) | **0/1 runs** (seat API 403 px-captcha twice, then page blocked; 66s) |
| E3 | .NET + NuGet `Patchright` 1.63.0, own `fetch()` | **0/2 runs** (page passes, seat API 403 on 4/4 sessions; also with `Channel="chrome"`) |
| E3a | .NET + `Patchright` + **capture the app's own seat response** (`page.Response`) | **2/3 runs** (13.6s, 47.9s, fail) |
| E3b | .NET + **stock Microsoft.Playwright 1.48** + **capture** | **3/3 runs** (77.9s, **11.9s**, 75.7s; the slow runs needed 1–2 new sessions) |
| E4 | .NET + capture, next events by **navigating the same browser** to the next event page | every navigation **re-challenged** by PerimeterX (3/3), about 30s per event; F06 failed |
| E5 | Python, same warm page, next events via **in-page `fetch()` of the event HTML + seat API** (isolated world) | **6/6 events OK over 2 runs**: 4.0–4.8s/event (HTML ~1.3s + API ~3s), no challenge. F07 correctly reported as not an event |
| control | Python CLI (unchanged code), same proxy, between the .NET runs | OK, 12.9s |

## 3. Conclusions

1. The .NET mac failures come from **how the seat API is called**, not from the library. Our own in-page `fetch()` in the
   main world gets 403. The app's own call, captured, gets 200 (stock Playwright 3/3). **Patchright is NOT needed for
   Paciolan**; it did not help the own-fetch path either.
2. Even the app's own call can get 403 on a flagged session (E3a/E3b). The existing retry-with-a-new-`{SESSIONID}` logic
   stays necessary.
3. Speed for several events on one host:
   - **Python:** keep the PerimeterX-cleared page and use in-page isolated `fetch()` for the next events' HTML and seat
     API. That's **about 4.5s/event instead of 10–34s**, with no new challenge (E5).
   - **.NET:** page navigation is re-challenged (E4) and main-world `fetch()` is refused. There is no cheap multi-event
     path on .NET yet: one fresh browser per event, with capture.
4. The Windows WebView2 bot uses `ExecuteFetchAsync` (in-page `fetch()` in the **main world**, via postMessage), which
   is the pattern that failed in E3/baseline. It should switch to capturing the app's own response via
   `CoreWebView2.WebResourceResponseReceived` (as `Rowing/AXS/AXSFetchClient` does). Not testable on macOS.

## 4. Chosen plan

| Version | Change | Evidence |
|---|---|---|
| Python `client.py` | keep the warm browser across events on the same host (proxy session unchanged). Next events: in-page isolated `fetch()` of `/event/{season}/{item}` + seat API. Reopen with a new `{SESSIONID}` only on block / 403 | E5 6/6, 4–5s/event |
| Python `api.py` | pool clients per `(host, proxy)` with a lock (Broadway pattern) | removes a browser launch + PerimeterX challenge per request |
| Python settle | keep `networkidle` (3s) on the **first** load only (the WebView2 bug note: reading the DOM too early). Subsequent events don't read the DOM | – |
| .NET mac `Fetch.Playwright` | replace `FetchInPageAsync` with **capture of the app's own seat-availability response** (listener attached before `goto`), keeping stock Microsoft.Playwright 1.48 and the retry model. Don't reuse the browser across events | E3b 3/3 vs baseline 0/1 |
| Windows `PaciolanEvenueFetchClient` | same capture via `WebResourceResponseReceived` instead of `ExecuteFetchAsync` | E3/E3b + the same main-world pattern; **must be verified on Windows** |

## 5. Implemented and re-run (2026-09-25)

| Version | Change | Real result after the change |
|---|---|---|
| Python `client.py` / `cli.py` | warm-page fast path in `get_event` (in-page isolated `fetch()` of the next event page when a page on the same host already passed PerimeterX; falls back to a fresh session on any block). `--item` is repeatable | `--item F03 F04 F05 F06 F07` in **one run, 26.7s total**: F03 13.3s (fresh), **F04 3.9s, F05 4.2s, F06 4.1s** (warm), F07 "not an event". Purdue F06 31.1s (1 block, then OK), 1,163 listings |
| Python `api.py` | one client per `(host, proxy)` + `asyncio.Lock`, closed on shutdown (Broadway pattern) | import/compile OK, 44/44 tests. The same client code path as the CLI |
| .NET mac `PaciolanEvenue.Fetch.Playwright` | `GetSeatAvailabilityAsync` uses the page's own captured seat response (`IPage.Response`, listener attached before `goto`, matched on the decoded event-id segment, waits up to 15s); stock Microsoft.Playwright 1.48 kept | **Oklahoma F03 OK in 13.9s on attempt 1** (127 listings / 1,037 seats, identical to Python). **Purdue F06 OK** in 92s after new sessions (58,965 rows, 1,163 listings). Before: 0/1 |
| Windows `PaciolanEvenueFetchClient` / `SessionForm` | capture via `CoreWebView2.WebResourceResponseReceived` (subscribed before navigate/reload). App.config `PaciolanSeatAvailabilityMode` = `capture` (default) or `fetch` (old path, kept for an A/B test on Windows) | compiles for net48 / C# 7.3 against the real base class + WebView2 1.0.3179.45 (macOS); **not run on Windows** |

Checked along the way: F06 Texas A&M `available=0` vs SSR 26 appears identically on the old fresh path too.
That's site data, not the fast path.
