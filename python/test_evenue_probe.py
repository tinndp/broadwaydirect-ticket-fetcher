"""One-off probe (not part of the package): does patchright get past
PerimeterX on purduesports.evenue.net where .NET's vanilla Playwright
didn't? Mirrors broadwaydirect/client.py's _open_session pattern (real
browser, headless=False, navigate + wait, then check the page/cookies).
Deleted or moved into a real module once this answers the question.
"""
import asyncio
import os
import sys
from urllib.parse import urlparse, unquote

from patchright.async_api import async_playwright


def parse_proxy(proxy):
    if not proxy:
        return None
    u = urlparse(proxy)
    out = {"server": f"{u.scheme}://{u.hostname}:{u.port}"}
    if u.username:
        out["username"] = unquote(u.username)
        out["password"] = unquote(u.password) if u.password else ""
    return out


BLOCK_MARKERS = [
    "Access to this page has been denied",
    "px-captcha",
    "Please verify you are a human",
]


async def main():
    host = os.environ.get("EVENUE_HOST", "purduesports.evenue.net")
    item = os.environ.get("EVENUE_ITEM", "F06")
    season = os.environ.get("EVENUE_SEASON", "F26")
    url = f"https://{host}/event/{season}/{item}"

    proxy_host = os.environ.get("PACIOLAN_PROXY_HOST")
    proxy_port = os.environ.get("PACIOLAN_PROXY_PORT")
    proxy_user_tmpl = os.environ.get("PACIOLAN_PROXY_USER", "")
    proxy_pass = os.environ.get("PACIOLAN_PROXY_PASS", "")
    session_id = "probe" + os.urandom(4).hex()
    proxy_user = proxy_user_tmpl.replace("{SESSIONID}", session_id)
    proxy_url = None
    if proxy_host and proxy_port:
        proxy_url = f"http://{proxy_user}:{proxy_pass}@{proxy_host}:{proxy_port}"
        print(f"  using proxy {proxy_host}:{proxy_port} session={session_id}", file=sys.stderr)
    else:
        print("  NO proxy configured (PACIOLAN_PROXY_HOST/PORT unset) - direct connection", file=sys.stderr)

    print(f"  navigating to {url}", file=sys.stderr)
    async with async_playwright() as p:
        browser = await p.chromium.launch(headless=False, proxy=parse_proxy(proxy_url))
        context = await browser.new_context(viewport={"width": 1280, "height": 900})
        page = await context.new_page()
        try:
            await page.goto(url, wait_until="domcontentloaded", timeout=30000)
        except Exception as e:
            print(f"  !! goto failed: {e}", file=sys.stderr)
            await browser.close()
            return

        await page.wait_for_timeout(3000)
        try:
            await page.wait_for_load_state("networkidle", timeout=5000)
        except Exception:
            pass
        extra_wait = int(os.environ.get("EVENUE_EXTRA_WAIT_MS", "0"))
        if extra_wait:
            print(f"  extra wait {extra_wait}ms for PerimeterX sensor to settle...", file=sys.stderr)
            await page.wait_for_timeout(extra_wait)

        title = await page.title()
        html = await page.content()
        blocked = [m for m in BLOCK_MARKERS if m.lower() in html.lower()]
        px_cookies = [c["name"] for c in await context.cookies() if c["name"].startswith("_px") or c["name"] == "pxcts"]

        print(f"  title: {title!r}", file=sys.stderr)
        print(f"  px cookies: {px_cookies}", file=sys.stderr)
        print(f"  blocked markers found: {blocked}", file=sys.stderr)
        print(f"  body length: {len(html)}", file=sys.stderr)

        if not blocked:
            # try the same in-page fetch the .NET version used, to see if the
            # seat-availability API answers now
            api_probe = await page.evaluate(
                """async () => {
                    const nd = document.getElementById('__NEXT_DATA__');
                    if (!nd) return {ok:false, reason:'no __NEXT_DATA__'};
                    let j; try { j = JSON.parse(nd.textContent); } catch(e) { return {ok:false, reason:'parse:'+e}; }
                    const p = j?.props?.pageProps?.component?.props;
                    if (!p) return {ok:false, reason:'no props'};
                    const ev = p?.ssrData?.discovery_eventDetailMPT?.[0];
                    if (!ev) return {ok:false, reason:'no event row'};
                    const path = `/pac-api/seat-availability/event-id/${p.dataAccountId}:${p.seasonCd}:${p.itemCd}/seats`
                        + `?distributorId=${p.distributorId}&availability=A%7CS&policyCd=${ev.POLICYCD}&policyType=${ev.POLICYTYPE}`;
                    const r = await fetch(path, {credentials:'include'});
                    const t = await r.text();
                    return {ok:true, status:r.status, bytes:t.length, eventName: ev.EVENTNAME};
                }"""
            )
            print(f"  API probe result: {api_probe}", file=sys.stderr)

        await browser.close()


if __name__ == "__main__":
    asyncio.run(main())
