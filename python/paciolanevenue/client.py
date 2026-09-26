"""Fetches one eVenue event's seat availability through a real browser.

Bot wall: **PerimeterX** (cookies `_pxvid`/`_px2`/`pxcts`, sensor beacon to
`px-cloud.net`). Unlike Cloudflare (broadwaydirect/client.py) and DataDome
(stubhub/client.py) - both of which `patchright` gets past RELIABLY in this
repo, verified live - PerimeterX was NOT reliably passed in probing done
before this file existed (see docs/EVENUE_PERIMETERX_FINDINGS.md): 5 real runs
through one residential proxy produced 3 different outcomes (blocked at the
page, page OK but the API 403'd, blocked at the page again), pointing at
**proxy IP reputation** as a major factor, not just which browser library is
used.

Given that, this client does NOT claim to bypass PerimeterX outright. It
applies the two things this repo's other two clients actually have in
common (a real `headless=False` browser, and POLLING for the challenge to
actually clear instead of a fixed sleep - see `_settle` below, same idea as
Cloudflare's `passCloudflare`/DataDome's challenge poll) plus one thing
neither of them needed but this site's evidence suggests helps: **retrying
with a fresh proxy `{SESSIONID}` (a new egress IP) when a block is
detected**, up to `retries` attempts - mirroring how the .NET/WebView2
crawlers in the main ETECH.Application.MarkAutomation repo (TM/Broadway)
handle an IP getting flagged mid-run, rather than treating one blocked
attempt as final. This measurably increases the chance of getting through
in one call; it does not guarantee it every time - see README.md.
"""

import asyncio
import json
import sys
from typing import Optional
from urllib.parse import urlparse, unquote

from patchright.async_api import async_playwright

from .models import Event, SeatRow
from .parser import (parse_event_page, seat_availability_path, NotAnEventPage,
                     EVENT_MAP_GQL_PATH, event_map_query, parse_event_map)
from .grouping import parse_seat_availability

BLOCK_MARKERS = [
    "Access to this page has been denied",
    "px-captcha",
    "Please verify you are a human",
]

EXPECTED_COLUMNS = [
    "LEVELSECTIONCD", "ROWCD", "SEATCD", "PRICELEVELCD", "SEATSTATUS",
    "MARKER_ID", "SEAT_MARKER_ACTIVE", "SLP_PRICE", "AVAILABLE", "HIDDEN",
]


class PerimeterXBlocked(Exception):
    """A real PerimeterX block was detected (page or API) - distinct from
    NotAnEventPage (wrong itemCd) and from an ordinary network error."""


def parse_proxy(proxy: Optional[str]) -> Optional[dict]:
    """Same shape as broadwaydirect.client.parse_proxy - "scheme://[user:pass@]host:port" -> dict."""
    if not proxy:
        return None
    u = urlparse(proxy)
    out = {"server": f"{u.scheme}://{u.hostname}:{u.port}"}
    if u.username:
        out["username"] = unquote(u.username)
        out["password"] = unquote(u.password) if u.password else ""
    return out


def _fresh_session_id() -> str:
    import os
    return "s" + os.urandom(6).hex()


def _apply_session(proxy_url_template: Optional[str], session_id: str) -> Optional[str]:
    """Substitutes the literal "{SESSIONID}" placeholder (if present) in the
    proxy URL's username with a fresh id - same convention as this repo's
    other {SESSIONID} proxies (TM/Broadway in the .NET side, ProxyConfig.cs
    in the .NET eVenue demo)."""
    if not proxy_url_template:
        return None
    return proxy_url_template.replace("{SESSIONID}", session_id)


class PaciolanEvenueClient:
    def __init__(self, sleep: float = 0.5, retries: int = 4, timeout: int = 20,
                 headless: bool = False, settle_poll_ms: int = 500,
                 settle_max_ms: int = 15000, on_raw=None,
                 proxy: Optional[str] = None):
        """proxy: "scheme://[user:pass@]host:port", may contain the literal
        "{SESSIONID}" placeholder in the username - a fresh id is generated
        per (re)open, see _fresh_session_id.
        retries: number of DIFFERENT proxy sessions (egress IPs) to try
        before giving up on one event - see docs/EVENUE_PERIMETERX_FINDINGS.md
        for why this is higher than Broadway's default (3).
        settle_poll_ms/settle_max_ms: how _settle polls for the page to
        stop looking like a PerimeterX challenge instead of a fixed sleep."""
        self.sleep = sleep
        self.retries = retries
        self.timeout = timeout
        self.headless = headless
        self.settle_poll_ms = settle_poll_ms
        self.settle_max_ms = settle_max_ms
        self.on_raw = on_raw
        self.proxy_template = proxy
        self._playwright = None
        self._browser = None
        self._context = None
        self._page = None
        self._current_key = None  # (host, season, item)

    async def close(self) -> None:
        for obj, closer in ((self._browser, "close"), (self._playwright, "stop")):
            if obj is not None:
                try:
                    await getattr(obj, closer)()
                except Exception:
                    pass
        self._browser = None
        self._context = None
        self._page = None
        self._playwright = None

    async def __aenter__(self):
        return self

    async def __aexit__(self, exc_type, exc_val, exc_tb):
        await self.close()

    async def _open(self, url: str) -> None:
        await self.close()
        session_id = _fresh_session_id()
        proxy_url = _apply_session(self.proxy_template, session_id)
        if proxy_url:
            print(f"  opening browser via proxy session={session_id}...", file=sys.stderr)
        else:
            print("  opening browser (no proxy configured)...", file=sys.stderr)
        self._playwright = await async_playwright().start()
        self._browser = await self._playwright.chromium.launch(
            headless=self.headless, proxy=parse_proxy(proxy_url))
        self._context = await self._browser.new_context(viewport={"width": 1280, "height": 900})
        self._page = await self._context.new_page()
        try:
            await self._page.goto(url, wait_until="domcontentloaded", timeout=self.timeout * 1000)
        except Exception as e:
            raise PerimeterXBlocked(f"navigation to {url} failed/timed out: {e}")
        await self._settle()

    async def _settle(self) -> None:
        """POLLS (not a fixed sleep) until the page no longer looks like a
        PerimeterX challenge, up to settle_max_ms - same idea as
        broadwaydirect's Cloudflare poll / stubhub's DataDome poll. Raises
        PerimeterXBlocked if a block marker is still present when the
        budget runs out; otherwise returns once the page looks clear (which
        does NOT guarantee the API will also succeed - see class docs)."""
        elapsed = 0
        while elapsed < self.settle_max_ms:
            html = await self._page.content()
            if not any(m.lower() in html.lower() for m in BLOCK_MARKERS):
                try:
                    await self._page.wait_for_load_state("networkidle", timeout=3000)
                except Exception:
                    pass
                return
            await self._page.wait_for_timeout(self.settle_poll_ms)
            elapsed += self.settle_poll_ms
        html = await self._page.content()
        matched = [m for m in BLOCK_MARKERS if m.lower() in html.lower()]
        raise PerimeterXBlocked(f"page still shows a block marker after {self.settle_max_ms}ms: {matched}")

    async def _fetch_in_page(self, path: str) -> tuple[int, str]:
        payload = await self._page.evaluate(
            """async ({path, timeoutMs}) => {
                const ctrl = new AbortController();
                const t = setTimeout(() => ctrl.abort(), timeoutMs);
                try {
                    const r = await fetch(path, {credentials: 'include', signal: ctrl.signal});
                    const body = await r.text();
                    return {status: r.status, body};
                } catch (e) {
                    return {status: 0, body: String((e && e.message) || e)};
                } finally {
                    clearTimeout(t);
                }
            }""",
            {"path": path, "timeoutMs": self.timeout * 1000},
        )
        return payload["status"], payload["body"]

    async def _post_json_in_page(self, path: str, body: dict) -> tuple[int, str]:
        """Same as _fetch_in_page, for a JSON POST (GraphQL)."""
        payload = await self._page.evaluate(
            """async ({path, body, timeoutMs}) => {
                const ctrl = new AbortController();
                const t = setTimeout(() => ctrl.abort(), timeoutMs);
                try {
                    const r = await fetch(path, {method: 'POST', credentials: 'include', signal: ctrl.signal,
                                                 headers: {'content-type': 'application/json'},
                                                 body: JSON.stringify(body)});
                    return {status: r.status, body: await r.text()};
                } catch (e) {
                    return {status: 0, body: String((e && e.message) || e)};
                } finally {
                    clearTimeout(t);
                }
            }""",
            {"path": path, "body": body, "timeoutMs": self.timeout * 1000},
        )
        return payload["status"], payload["body"]

    async def get_event_map(self, event: Event) -> str:
        """Fills event.hold_codes / event.seating_types from GraphQL maps_eventMap (see
        parser.event_map_query). Call after get_event() for the same event. Never raises:
        on failure both stay None, grouping falls back to AVAILABLE == 1, and the returned
        note says so (it goes into the coverage line)."""
        if self._page is None:
            return "map=unavailable(no page)"
        try:
            status, body = await self._post_json_in_page(EVENT_MAP_GQL_PATH, event_map_query(event))
            if status != 200:
                return f"map=unavailable(HTTP {status})"
            event.hold_codes, event.seating_types = parse_event_map(json.loads(body))
            if self.on_raw:
                self.on_raw(event.host, f"{event.item_cd}_event_map", json.loads(body))
            return "map=ok"
        except Exception as e:
            event.hold_codes = event.seating_types = None
            return f"map=unavailable({str(e)[:80]})"

    async def _event_via_warm_page(self, host: str, season_cd: str, item_cd: str) -> tuple:
        """FAST PATH for the 2nd+ event on the same host: the current page already
        passed PerimeterX, so fetch the next event page's HTML with an in-page
        fetch() (patchright evaluates in an ISOLATED world by default) instead of
        relaunching the browser + navigating. Measured 2026-09-24 (see
        docs/EVENUE_OPTIMIZATION_FINDINGS.md, E5): 6/6 events OK at 4.0-4.8s/event incl.
        the seat API, vs 10-34s per event with a fresh browser. Navigating the SAME
        browser to the next event page instead was re-challenged 3/3 (E4), so this
        uses fetch(), never navigation. Raises PerimeterXBlocked when the response
        looks blocked - the caller then falls back to the normal fresh-session path."""
        status, html = await self._fetch_in_page(f"/event/{season_cd}/{item_cd}")
        if status != 200:
            raise PerimeterXBlocked(f"warm-page fetch of the event page returned HTTP {status}")
        low = html.lower()
        if any(m.lower() in low for m in BLOCK_MARKERS) or "__NEXT_DATA__" not in html:
            raise PerimeterXBlocked("warm-page fetch of the event page returned a block page / no __NEXT_DATA__")
        return parse_event_page(html, host, season_cd, item_cd)

    async def get_event(self, host: str, season_cd: str, item_cd: str) -> tuple[Event, list]:
        """Returns (Event, list[PriceLevel]). If a page on the same host already
        passed PerimeterX, tries the warm-page fast path first (see
        _event_via_warm_page). Otherwise / on a block: retries with a fresh proxy
        session up to self.retries times if PerimeterX blocks the page."""
        url = f"https://{host}/event/{season_cd}/{item_cd}"
        if self._page is not None and self._current_key and self._current_key[0] == host:
            try:
                event, price_levels = await self._event_via_warm_page(host, season_cd, item_cd)
                self._current_key = (host, season_cd, item_cd)
                return event, price_levels
            except NotAnEventPage:
                raise  # a real, readable page that is not an event - same as the fresh path
            except Exception as e:  # blocked / page gone -> fall back to a fresh session
                print(f"  !! warm-page fast path failed ({e}); opening a fresh session", file=sys.stderr)
        last_err = None
        for attempt in range(1, self.retries + 1):
            try:
                await self._open(url)
                html = await self._page.content()
                event, price_levels = parse_event_page(html, host, season_cd, item_cd)
                self._current_key = (host, season_cd, item_cd)
                return event, price_levels
            except NotAnEventPage:
                raise  # not a block - retrying with a new IP won't fix a wrong itemCd
            except PerimeterXBlocked as e:
                last_err = e
                print(f"  !! attempt {attempt}/{self.retries} blocked: {e}", file=sys.stderr)
                if attempt < self.retries:
                    await asyncio.sleep(self.sleep * attempt * 2)
        raise PerimeterXBlocked(f"gave up after {self.retries} proxy sessions: {last_err}")

    async def get_seat_availability(self, event: Event) -> tuple[list, list, str]:
        """Returns (list[SeatRow], columns_actual, coverage_note). Must be
        called after get_event() for the SAME event (reuses the open
        session/page) - if the API 403s, this retries the WHOLE session
        (fresh proxy id + re-navigate), same as get_event's retry, since a
        PerimeterX block on the API implies the page-level session is also
        suspect."""
        if self._current_key != (event.host, event.season_cd, event.item_cd):
            raise RuntimeError("get_seat_availability called for a different event than the open session - call get_event() first")

        path = seat_availability_path(event)
        last_err = None
        top = None
        for attempt in range(1, self.retries + 1):
            try:
                status, body = await self._fetch_in_page(path)
            except Exception as e:
                # page.evaluate itself can raise (target/execution context
                # destroyed, browser crash) even though _fetch_in_page's own
                # JS already catches ordinary fetch/network errors - treat
                # this the same as a failed HTTP attempt instead of letting
                # it escape the retry loop uncaught.
                status, body = 0, str(e)
            if status == 200:
                try:
                    top = json.loads(body)
                    break
                except json.JSONDecodeError as e:
                    # A 200 with a non-JSON body (e.g. an HTML block/challenge
                    # page served with status 200) is still a block - retry
                    # with a fresh session instead of crashing on parse.
                    last_err = f"HTTP 200 but body was not valid JSON: {e}"
            else:
                last_err = f"HTTP {status}: {body[:200]}"
            print(f"  !! seat-availability attempt {attempt}/{self.retries} failed: {last_err}", file=sys.stderr)
            if attempt < self.retries:
                await asyncio.sleep(self.sleep * attempt * 2)
                await self._open(event.event_url)  # fresh proxy session
        else:
            raise PerimeterXBlocked(f"seat-availability gave up after {self.retries} attempts: {last_err}")

        seats, columns = parse_seat_availability(top, EXPECTED_COLUMNS)
        if columns != EXPECTED_COLUMNS:
            print(f"  !! seat-availability columns differ from the recon baseline: {columns}", file=sys.stderr)

        available = sum(1 for s in seats if s.available)
        parts = [f"rows={len(seats)}", f"available={available}"]
        if event.total_capacity_ssr is not None:
            match = "yes" if len(seats) == event.total_capacity_ssr else f"NO(ssr={event.total_capacity_ssr})"
            parts.append(f"capacityMatch={match}")
        if event.available_ssr is not None:
            match = "yes" if available == event.available_ssr else f"NO(ssr={event.available_ssr})"
            parts.append(f"availableMatch={match}")

        if self.on_raw:
            self.on_raw(event.host, f"{event.item_cd}", top)

        return seats, columns, " ".join(parts)
