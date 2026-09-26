"""Fetches one AXS event (metadata + full inventory) through a real browser.

Bot walls (docs/RECON.md section 2):
  1. Cloudflare Managed Challenge + INTERACTIVE Turnstile on every *.axs.com
     host. patchright's bundled Chromium gets stuck on the "Verify you are
     human" checkbox; the locally installed Google Chrome
     (`channel="chrome"`) clears it on its own in 3-6s. So `channel` defaults
     to "chrome" here - the one real difference from broadwaydirect/client.py.
     We never click the checkbox.
  2. Queue-it on shop.axs.com (waiting room during hot onsales).
  3. The Veritix commerce API (unifiedapicommerce-us.axs.com) needs a session
     the app itself creates (cookie axs_ecomm, 25 min; 440 "Session Expired"
     before that), and REPLAYING its calls gets 429 / a 403 Cloudflare HTML
     page even with an identical body. So this client does NOT fetch() the
     inventory itself: it loads the ticket page and CAPTURES the responses the
     app makes (`page.on("response")`) - price, sections, offer/search.

Retry model is paciolanevenue's (the companion template): a detected block
-> close everything, reopen with a fresh proxy {SESSIONID} (new egress IP),
try again, up to `retries` sessions. Without a proxy it still retries (same
IP) - useful for the transient 429s seen in recon, but can't change IP.
"""

import asyncio
import json
import os
import re
import sys
import time
from typing import Optional
from urllib.parse import urlparse, unquote

from patchright.async_api import async_playwright

from .models import Event
from .parser import parse_event, NotAnEventPage

CHALLENGE_TITLES = ("just a moment", "axs access info", "verification in progress",
                    "attention required", "loading https://")
# AXS's own hard-block page (same "AXS Access Info" title as the challenge,
# but no Turnstile - it never clears; seen on tix.axs.com 2026-09-24 after an
# IP had loaded many ticket pages). Detect it and fail fast.
HARD_BLOCK_MARKERS = ("actively prevent automated bots",)
COMMERCE_HOST = "unifiedapicommerce"
# capture name -> substring of the commerce API path (docs/RECON.md 3b)
CAPTURES = {
    # Veritix primary inventory (es5Flow PICK_A_SEAT_2D, e.g. shop.axs.com/?c=axs&e=...)
    "phase": "/phase?",
    "price": "/price?",
    "sections": "/sections?",
    "offer_search": "/offer/search?",
    # AXS Marketplace (es5Flow BEST_AVAILABLE, isExternalPurchaseFlow=True,
    # e.g. tix.axs.com/{token}?axssid=...) - found 2026-09-24 on 3/3 newer events.
    # Only mp_offers is required; eventinfo/mapinfo are kept as raw when they
    # happen to arrive first, never waited for.
    "mp_eventinfo": "/axsmarketplace/eventinfo?",
    "mp_offers": "/axsmarketplace/offers?",
    "mp_mapinfo": "/axsmarketplace/mapinfo?",
}


class AxsBlocked(Exception):
    """Challenge never cleared, or the commerce API answered 403/429/440 -
    worth retrying with a fresh proxy session."""


class NotOnSale(Exception):
    """The event has no AXS ticket flow (no ticket URL, sold elsewhere,
    past/after-event page, no commerce calls). Not a block - don't retry."""


def parse_proxy(proxy: Optional[str]) -> Optional[dict]:
    """Same shape as broadwaydirect.client.parse_proxy."""
    if not proxy:
        return None
    u = urlparse(proxy)
    out = {"server": f"{u.scheme}://{u.hostname}:{u.port}"}
    if u.username:
        out["username"] = unquote(u.username)
        out["password"] = unquote(u.password) if u.password else ""
    return out


def _fresh_session_id() -> str:
    return "s" + os.urandom(6).hex()


def _apply_session(proxy_template: Optional[str], session_id: str) -> Optional[str]:
    return proxy_template.replace("{SESSIONID}", session_id) if proxy_template else None


def _endpoint_label(url: str) -> str:
    """/veritix/pre-flow/v2/{token}/phase -> "pre-flow/phase";
    /axsmarketplace/offers -> "axsmarketplace/offers" (drops "veritix",
    version segments and the long per-event token)."""
    parts = [p for p in urlparse(url).path.split("/")
             if p and p != "veritix" and not re.fullmatch(r"[vV]\d+", p) and len(p) < 40]
    return "/".join(parts) or urlparse(url).path


def _log(msg: str) -> None:
    print(f"  {msg}", file=sys.stderr)


class AxsClient:
    def __init__(self, proxy: Optional[str] = None, retries: int = 4, headless: bool = False,
                 channel: Optional[str] = "chrome", settle_max_s: int = 40,
                 inventory_timeout_s: int = 150, offer_search_grace_s: int = 25,
                 queue_max_s: int = 600, sleep: float = 2.0, no_proxy_backoff_s: float = 60.0,
                 on_raw=None):
        """proxy: "scheme://[user:pass@]host:port", optional; may contain the
        literal "{SESSIONID}" placeholder (a fresh id per (re)open).
        channel: "chrome" = installed Google Chrome (required for AXS's
        Turnstile, see module doc); None = patchright's bundled Chromium.
        inventory_timeout_s: budget for the ticket app to load and make its
        inventory calls (30-47s observed in recon).
        offer_search_grace_s: after price+sections arrived, how long to wait
        for offer/search before accepting a result without seats (GA /
        best-available flows may never call it).
        on_raw(event_id, name, json): optional hook to persist raw JSON."""
        self.proxy_template = proxy
        self.retries = retries
        self.headless = headless
        self.channel = channel
        self.settle_max_s = settle_max_s
        self.inventory_timeout_s = inventory_timeout_s
        self.offer_search_grace_s = offer_search_grace_s
        self.queue_max_s = queue_max_s
        self.sleep = sleep
        self.no_proxy_backoff_s = no_proxy_backoff_s
        self.on_raw = on_raw
        self._pw = None
        self._browser = None
        self._context = None
        self.session_id = None

    # ---- lifecycle -------------------------------------------------------
    async def close(self) -> None:
        for obj, closer in ((self._browser, "close"), (self._pw, "stop")):
            if obj is not None:
                try:
                    await getattr(obj, closer)()
                except Exception:
                    pass
        self._browser = self._context = self._pw = None

    async def __aenter__(self):
        return self

    async def __aexit__(self, *exc):
        await self.close()

    async def _reopen(self) -> None:
        await self.close()
        self.session_id = _fresh_session_id()
        proxy_url = _apply_session(self.proxy_template, self.session_id)
        _log(f"opening browser channel={self.channel or 'bundled'} "
             + (f"via proxy session={self.session_id}" if proxy_url else "(no proxy)"))
        self._pw = await async_playwright().start()
        kw = {"channel": self.channel} if self.channel else {}
        self._browser = await self._pw.chromium.launch(headless=self.headless, proxy=parse_proxy(proxy_url), **kw)
        self._context = await self._browser.new_context(viewport={"width": 1280, "height": 900})

    async def _ensure_open(self) -> None:
        if self._context is None:
            await self._reopen()

    # ---- helpers ---------------------------------------------------------
    async def _settle(self, page, what: str) -> None:
        """POLL (never a fixed sleep) until the title stops looking like a
        Cloudflare challenge. An interactive Turnstile that never clears ends
        up here as AxsBlocked."""
        deadline = time.monotonic() + self.settle_max_s
        title = ""
        while time.monotonic() < deadline:
            try:
                title = (await page.title()) or ""
            except Exception:
                title = ""
            if title and not any(t in title.lower() for t in CHALLENGE_TITLES):
                return
            if await self._hard_blocked(page):
                raise AxsBlocked(f"{what}: AXS hard block page ('actively prevent automated bots') - IP flagged")
            await asyncio.sleep(1)
        raise AxsBlocked(f"{what}: challenge did not clear in {self.settle_max_s}s (title={title!r})")

    async def _hard_blocked(self, page) -> bool:
        try:
            title = (await page.title() or "").lower()
            if "access info" not in title:
                return False
            text = (await page.evaluate("() => document.body ? document.body.innerText : ''")).lower()
            return any(m in text for m in HARD_BLOCK_MARKERS)
        except Exception:
            return False

    def _raw(self, event_id, name, obj) -> None:
        if self.on_raw and obj is not None:
            try:
                self.on_raw(event_id, name, obj)
            except Exception as e:
                _log(f"!! on_raw({name}) failed: {e}")

    async def _with_retries(self, what: str, fn):
        last = None
        for attempt in range(1, self.retries + 1):
            t_attempt = time.monotonic()
            try:
                await self._ensure_open()
                result = await fn()
                _log(f"{what} ok on attempt {attempt}/{self.retries} in {time.monotonic() - t_attempt:.0f}s")
                return result
            except (NotAnEventPage, NotOnSale):
                raise
            except AxsBlocked as e:
                last = e
            except Exception as e:  # browser crash, navigation timeout, ...
                last = AxsBlocked(f"{type(e).__name__}: {e}")
            _log(f"!! {what} attempt {attempt}/{self.retries} failed: {last}")
            await self.close()  # next attempt = fresh proxy session / fresh browser
            if attempt < self.retries:
                # Without a proxy the retry lands on the SAME IP: hammering it
                # made AXS's commerce API 403 every call (2026-09-24), so back
                # off for real. With a proxy the next session is a new IP.
                wait = self.sleep * attempt if self.proxy_template else self.no_proxy_backoff_s * attempt
                _log(f"   retrying in {wait:.0f}s ({'new proxy session' if self.proxy_template else 'same IP, no proxy'})")
                await asyncio.sleep(wait)
        raise AxsBlocked(f"{what}: gave up after {self.retries} sessions: {last}")

    # ---- event metadata --------------------------------------------------
    async def get_event(self, event_url: str) -> Event:
        async def once():
            page = await self._context.new_page()
            try:
                await page.goto(event_url, wait_until="domcontentloaded", timeout=60000)
                await self._settle(page, "event page")
                raw = await page.evaluate("() => (document.getElementById('__NEXT_DATA__')||{}).textContent || ''")
                if not raw:
                    raise AxsBlocked("event page has no __NEXT_DATA__ (soft block?)")
                nd = json.loads(raw)
                event = parse_event(nd)
                self._raw(event.event_id, "event", nd.get("props", {}).get("pageProps"))
                return event
            finally:
                await page.close()
        return await self._with_retries("event page", once)

    # ---- inventory -------------------------------------------------------
    async def get_inventory(self, event: Event) -> dict:
        """Loads event.ticket_url and returns the captured commerce JSON:
        {"phase", "price", "sections", "offer_search", "notes": [...]} -
        missing keys are None. Raises NotOnSale / AxsBlocked."""
        if not event.ticket_url:
            raise NotOnSale(f"event {event.event_id} has no ticket URL (status={event.status!r})")

        async def once():
            captured, statuses, pending, commerce_errors = {}, {}, [], []
            page = await self._context.new_page()

            async def grab(resp, name):
                try:
                    body = await resp.body()
                    if resp.status == 200:
                        obj = json.loads(body)
                        if name == "offer_search" and name in captured:
                            # several offer/search calls (e.g. per price-level batch) -> merge offers
                            captured[name].setdefault("offers", []).extend(obj.get("offers") or [])
                        else:
                            captured[name] = obj
                except Exception as e:
                    statuses[name + "_error"] = str(e)[:120]

            def on_response(resp):
                url = resp.url
                if COMMERCE_HOST not in url:
                    return
                if resp.request.method == "OPTIONS":
                    return  # CORS preflight for the cross-origin API: same URL, no body
                # every commerce call (session, start-flow, ... too), not only the
                # captured ones: a 403 on POST /session leaves the app on
                # "Loading..." with phase=200 and nothing else (seen 2026-09-24).
                endpoint = _endpoint_label(url)
                if resp.status >= 400:
                    commerce_errors.append(f"{endpoint}:{resp.status}")
                for name, frag in CAPTURES.items():
                    if frag in url:
                        statuses[name] = resp.status
                        pending.append(asyncio.ensure_future(grab(resp, name)))

            page.on("response", on_response)
            notes = []
            try:
                await page.goto(event.ticket_url, wait_until="domcontentloaded", timeout=60000)
                t0 = time.monotonic()
                queue_seen = False
                challenge_since = None
                left_queue = False
                sections_at = None
                while True:
                    await asyncio.sleep(1)
                    url = page.url
                    if "queue-it.net" in url or "queue." in urlparse(url).netloc:
                        if not queue_seen:
                            _log("in Queue-it waiting room...")
                            notes.append("queue-it")
                            queue_seen = True
                        if time.monotonic() - t0 > self.queue_max_s:
                            raise NotOnSale(f"still in Queue-it after {self.queue_max_s}s")
                        continue
                    if queue_seen and not left_queue:
                        left_queue = True
                        t0 = time.monotonic()  # inventory budget starts after the waiting room
                    title_now = (await page.title() or "").lower()
                    if any(t in title_now for t in CHALLENGE_TITLES):
                        challenge_since = challenge_since or time.monotonic()
                        if time.monotonic() - challenge_since > self.settle_max_s:
                            raise AxsBlocked(f"ticket page stuck on challenge {self.settle_max_s}s (title={title_now!r})")
                    else:
                        challenge_since = None
                    if await self._hard_blocked(page):
                        raise AxsBlocked("AXS hard block page ('actively prevent automated bots') - IP flagged")
                    bad = [e for e in commerce_errors if e.rsplit(":", 1)[1] in ("403", "429", "440")]
                    if bad:
                        raise AxsBlocked(f"commerce API refused: {bad}")
                    if "price" in captured and "sections" in captured and sections_at is None:
                        sections_at = time.monotonic()
                    if "mp_offers" in captured:
                        # marketplace flow: offers IS the inventory - stop right away.
                        # eventinfo (page header, duplicates __NEXT_DATA__) and mapinfo
                        # (seat-map drawing, per-section totals derivable from offers)
                        # carry no ticket data, so they're never waited for (2026-09-24);
                        # if they already arrived they're still saved as raw.
                        break
                    if "offer_search" in captured and "price" in captured:
                        await asyncio.sleep(1)  # let a trailing offer/search batch land
                        break
                    if sections_at and time.monotonic() - sections_at > self.offer_search_grace_s:
                        notes.append("no offer/search call (GA or best-available flow?) - seats empty")
                        break
                    if time.monotonic() - t0 > self.inventory_timeout_s:
                        title = await page.title()
                        if "phase" not in statuses:
                            if any(t in (title or "").lower() for t in CHALLENGE_TITLES):
                                raise AxsBlocked(f"ticket page stuck on challenge (title={title!r})")
                            if "tix.axs.com" in (page.url or url):
                                # marketplace page loaded but its offers call never fired: seen
                                # via proxy (Sound of Music 2026-09-26) - a silent block, not "off sale".
                                raise AxsBlocked(f"marketplace page without offers after {self.inventory_timeout_s}s "
                                                 f"(title={title!r})")
                            raise NotOnSale(f"no Veritix flow after {self.inventory_timeout_s}s "
                                            f"(url={url[:80]}, title={title!r})")
                        raise AxsBlocked(f"inventory calls incomplete after {self.inventory_timeout_s}s: "
                                         f"captured={statuses} errors={commerce_errors}")
                if pending:
                    await asyncio.gather(*pending, return_exceptions=True)
            finally:
                await page.close()

            out = {k: captured.get(k) for k in CAPTURES}
            out["notes"] = notes
            for k in CAPTURES:
                self._raw(event.event_id, k, captured.get(k))
            return out

        return await self._with_retries("inventory", once)
