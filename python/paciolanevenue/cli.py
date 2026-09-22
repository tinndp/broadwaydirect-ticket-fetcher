"""Minimal CLI for manual testing - not the HTTP API (see api.py for that).

    python3 -m paciolanevenue.cli --host purduesports.evenue.net --season F26 --item F06
"""

import argparse
import asyncio
import json
import os
import sys
from typing import Optional

from .client import PaciolanEvenueClient, PerimeterXBlocked
from .parser import NotAnEventPage
from .grouping import group_into_listings, load_rules


def _env_proxy() -> Optional[str]:
    host = os.environ.get("PACIOLAN_PROXY_HOST")
    port = os.environ.get("PACIOLAN_PROXY_PORT")
    user = os.environ.get("PACIOLAN_PROXY_USER", "")
    pw = os.environ.get("PACIOLAN_PROXY_PASS", "")
    if host and port:
        return f"http://{user}:{pw}@{host}:{port}"
    # Fallback: python/paciolanevenue/_local_proxy.py, gitignored, NOT a
    # tracked file - see that file's docstring. Env vars above always win.
    try:
        from ._local_proxy import PROXY_URL
        return PROXY_URL
    except ImportError:
        return None


async def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", required=True)
    ap.add_argument("--season", required=True)
    ap.add_argument("--item", required=True)
    ap.add_argument("--retries", type=int, default=4)
    ap.add_argument("--headless", action="store_true")
    ap.add_argument("--out", default="output")
    args = ap.parse_args()

    client = PaciolanEvenueClient(proxy=_env_proxy(), retries=args.retries, headless=args.headless)
    try:
        try:
            event, price_levels = await client.get_event(args.host, args.season, args.item)
        except NotAnEventPage as e:
            print(f"NOT AN EVENT: {e}", file=sys.stderr)
            return 1
        except PerimeterXBlocked as e:
            print(f"BLOCKED: {e}", file=sys.stderr)
            return 2

        print(f"event: {event.event_name} @ {event.facility_title} (hideTime={event.hide_time})")

        try:
            seats, columns, coverage = await client.get_seat_availability(event)
        except PerimeterXBlocked as e:
            print(f"BLOCKED on seat-availability: {e}", file=sys.stderr)
            return 2

        print(f"coverage: {coverage}")
        available = [s for s in seats if s.available]
        listings = group_into_listings(available, rules=load_rules())
        print(f"{len(available)} available seats -> {len(listings)} listings")

        out_dir = os.path.join(args.out, args.host, args.season, args.item)
        os.makedirs(out_dir, exist_ok=True)
        with open(os.path.join(out_dir, "event.json"), "w") as f:
            json.dump({"event": event.__dict__, "price_levels": [pl.__dict__ for pl in price_levels]}, f, indent=2)
        with open(os.path.join(out_dir, "listings.json"), "w") as f:
            json.dump([l.__dict__ for l in listings], f, indent=2)
        print(f"wrote {out_dir}/event.json and listings.json")
        return 0
    finally:
        await client.close()


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
