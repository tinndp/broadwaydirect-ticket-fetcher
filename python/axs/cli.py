"""Manual-test CLI (paciolanevenue/cli.py pattern) - not the HTTP API (see api.py).

    # one event: metadata + inventory -> output/axs/{eventId}/
    python3 -m axs.cli event --url https://www.axs.com/events/1457730/tom-jones-21-event-tickets

    # inventory for the events of a discovery CSV (discovery/AXSEventDiscovery, Node)
    python3 -m axs.cli crawl --events ../discovery/AXSEventDiscovery/AXSEventDiscovery.Crawler/events.csv --limit 20

Proxy is optional: --proxy "http://user-{SESSIONID}:pass@host:port" or
AXS_PROXY_HOST/PORT/USER/PASS env vars; without either the machine's own IP
is used.
"""

import argparse
import asyncio
import csv
import json
import os
import sys
import time
from datetime import datetime, timezone
from typing import Optional

from shared.proxy_pool import normalize_proxy

from .client import AxsClient, AxsBlocked, NotOnSale
from .parser import NotAnEventPage
from .grouping import build_result
from . import output


def env_proxy() -> Optional[str]:
    host, port = os.environ.get("AXS_PROXY_HOST"), os.environ.get("AXS_PROXY_PORT")
    if host and port:
        user, pw = os.environ.get("AXS_PROXY_USER", ""), os.environ.get("AXS_PROXY_PASS", "")
        return f"http://{user}:{pw}@{host}:{port}" if user else f"http://{host}:{port}"
    return None


def pick_proxy(arg: Optional[str]) -> Optional[str]:
    return normalize_proxy(arg) if arg else env_proxy()


async def fetch_event(client: AxsClient, url: str) -> dict:
    """Event page + inventory for one event -> result dict (grouping.build_result)."""
    t0 = time.monotonic()
    event = await client.get_event(url)
    print(f"event {event.event_id}: {event.name} @ {event.venue_name} "
          f"{event.event_dt_local} ({event.event_dt_utc}) status={event.status!r}", file=sys.stderr)
    try:
        inv = await client.get_inventory(event)
    except NotOnSale as e:
        inv = {"notes": [f"not on sale: {e}"]}
    res = build_result(event, inv)
    res["coverage"] += f" elapsed={time.monotonic() - t0:.0f}s"
    return res


async def cmd_event(args) -> int:
    out_dir = args.out
    client = AxsClient(proxy=pick_proxy(args.proxy), retries=args.retries, headless=args.headless,
                       channel=None if args.bundled_chromium else "chrome",
                       on_raw=output.raw_writer(out_dir))
    failed = []
    try:
        for url in args.url:
            try:
                res = await fetch_event(client, url)
            except NotAnEventPage as e:
                print(f"NOT AN EVENT {url}: {e}", file=sys.stderr)
                failed.append(url)
                continue
            except AxsBlocked as e:
                print(f"BLOCKED {url}: {e}", file=sys.stderr)
                failed.append(url)
                continue
            path = output.write_result(out_dir, res)
            print(f"coverage: {res['coverage']} notes={res['notes']}")
            print(f"wrote {path}")
    finally:
        await client.close()
    if failed:
        print(f"{len(failed)}/{len(args.url)} event(s) failed: {failed}")
        return 2
    return 0


def load_discovered(path: str) -> list:
    """Event URLs from a discovery CSV (discovery/AXSEventDiscovery): upcoming events that
    AXS sells (axs_ticketed or axs_marketplace), soonest first."""
    now = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    with open(path, newline="", encoding="utf-8") as f:
        rows = [r for r in csv.DictReader(f)
                if (r.get("axs_ticketed") == "true" or r.get("axs_marketplace") == "true")
                and (r.get("event_datetime_utc") or "") > now]
    return [r["event_url"] for r in sorted(rows, key=lambda r: r["event_datetime_utc"])]


async def cmd_crawl(args) -> int:
    events = load_discovered(args.events)[: args.limit]
    print(f"{len(events)} upcoming on-sale events to crawl", file=sys.stderr)
    client = AxsClient(proxy=pick_proxy(args.proxy), retries=args.retries, headless=args.headless,
                       on_raw=output.raw_writer(args.out))
    ok = failed = 0
    try:
        for i, url in enumerate(events, 1):
            print(f"[{i}/{len(events)}] {url}", file=sys.stderr)
            try:
                res = await fetch_event(client, url)
                output.write_result(args.out, res)
                print(f"  {res['coverage']} {res['notes']}", file=sys.stderr)
                ok += 1
            except (AxsBlocked, NotAnEventPage) as ex:
                print(f"  !! {type(ex).__name__}: {ex}", file=sys.stderr)
                failed += 1
            await asyncio.sleep(args.delay)
    finally:
        await client.close()
    print(f"done: ok={ok} failed={failed}")
    return 0 if failed == 0 else 1


def main() -> int:
    ap = argparse.ArgumentParser(prog="axs.cli")
    sub = ap.add_subparsers(dest="cmd", required=True)

    def common(p):
        p.add_argument("--proxy", help='optional, e.g. "http://user-{SESSIONID}:pass@host:port"')
        p.add_argument("--headless", action="store_true", help="not verified against AXS - default is a visible browser")
        p.add_argument("--out", default="output/axs")

    p = sub.add_parser("event"); common(p)
    p.add_argument("--url", action="append", required=True, help="AXS event page URL (repeatable)")
    p.add_argument("--retries", type=int, default=3)
    p.add_argument("--bundled-chromium", action="store_true", help="use patchright's Chromium instead of Chrome (gets stuck on Turnstile)")

    p = sub.add_parser("crawl"); common(p)
    p.add_argument("--events", required=True, help="discovery CSV (discovery/AXSEventDiscovery)")
    p.add_argument("--limit", type=int, default=20)
    p.add_argument("--retries", type=int, default=3)
    p.add_argument("--delay", type=float, default=3.0)

    args = ap.parse_args()
    fn = {"event": cmd_event, "crawl": cmd_crawl}[args.cmd]
    return asyncio.run(fn(args))


if __name__ == "__main__":
    sys.exit(main())
