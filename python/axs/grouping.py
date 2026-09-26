"""Seats -> listings.

Primary seats: consecutive seat numbers within one (offer, section, row,
price level) become one listing - same idea as broadwaydirect/grouping.py,
without Broadway's SIDES/CENTER theater rules (no per-venue rules for AXS
yet - add them the same way as broadwaydirect's section_rules.json if the
client supplies any). Seats whose number isn't an integer are kept as one
"Ungrouped" listing per (offer, section, row, price level), same as
paciolanevenue.

Resale (FLASHSEATS) offers are NOT re-grouped: one offer = one listing, as
the seller listed it (same reasoning as stubhub - resale arrives
pre-bundled, and splitting it would invent listings that can't be bought).
"""

from itertools import groupby

from .models import Listing


def _price_index(price_levels: list) -> dict:
    """(offer_id, price_level_id) -> PriceLevel, preferring price type
    "Regular" when an offer has several types for the same level. Resale
    offers are keyed (offer_id, "")."""
    idx = {}
    for pl in price_levels:
        key = (pl.offer_id, "" if pl.is_resale else pl.price_level_id)
        cur = idx.get(key)
        if cur is None or (pl.price_type_label.lower() == "regular" and cur.price_type_label.lower() != "regular"):
            idx[key] = pl
    return idx


def _runs(nums: list) -> list:
    runs, cur = [], []
    for n in nums:
        if cur and n != cur[-1] + 1:
            runs.append(cur)
            cur = []
        cur.append(n)
    if cur:
        runs.append(cur)
    return runs


def group_into_listings(seats: list, price_levels: list, resale_meta: dict, offer_rules: dict = None) -> list:
    idx = _price_index(price_levels)
    offer_rules = offer_rules or {}
    listings = []

    primary = [s for s in seats if not s.is_resale]
    keyf = lambda s: (s.offer_id, s.section, s.row, s.price_level_id)
    for key, grp in groupby(sorted(primary, key=keyf), key=keyf):
        offer_id, section, row, pl_id = key
        grp = list(grp)
        pl = idx.get((offer_id, pl_id))
        base = dict(offer_id=offer_id, section=section, row=row, price_level_id=pl_id,
                    price_cents=pl.price_cents if pl else 0, zone=pl.label if pl else "",
                    **offer_rules.get(offer_id, {}))
        numbered = sorted((s for s in grp if s.seat_num is not None), key=lambda s: s.seat_num)
        by_num = {}
        for s in numbered:
            by_num.setdefault(s.seat_num, s)
        for run in _runs(sorted(by_num)):
            listings.append(Listing(seat_keys=[by_num[n].key for n in run], seat_nums=run, **base))
        other = [s for s in grp if s.seat_num is None]
        if other:
            listings.append(Listing(seat_keys=[s.key for s in other], seating_type="Ungrouped", **base))

    resale = [s for s in seats if s.is_resale]
    for offer_id, grp in groupby(sorted(resale, key=lambda s: (s.offer_id, s.section, s.row)), key=lambda s: s.offer_id):
        grp = list(grp)
        meta = resale_meta.get(offer_id, {})
        pl = idx.get((offer_id, ""))
        nums = sorted(s.seat_num for s in grp if s.seat_num is not None)
        listings.append(Listing(
            offer_id=offer_id,
            section=grp[0].section,
            row=grp[0].row if len({s.row for s in grp}) == 1 else "/".join(sorted({s.row for s in grp})),
            seat_keys=[s.key for s in sorted(grp, key=lambda s: (s.row, s.seat_num or 0, s.number))],
            seat_nums=nums if len(nums) == len(grp) else [],
            seating_type="Resale",
            price_cents=pl.price_cents if pl else 0,
            total_price=meta.get("total_price"),
            fee_per_ticket=meta.get("connection_fee"),
            split_rule=meta.get("split_rule", ""),
            split_quantities=list(meta.get("split_quantities") or []),
            is_resale=True,
            zone=grp[0].neighborhood,
            **offer_rules.get(offer_id, {}),
        ))
    return listings


def mark_ladders(listings: list) -> list:
    """Marketplace "ladders": a seller posting the SAME seats as quantity 1, 2, .., n (seen on Sound
    of Music 1623554: 11 ladders = all 44 listings). Labels them, keeps every listing.
    A group = same section (whitespace collapsed), row, price, face value, all-in price, stock
    type, in-hand date and notes. It is a ladder only if it has 2+ listings, their quantities are
    exactly 1..n once each, and each listing's splits are exactly [1..quantity]. Anything else
    (e.g. two 2-ticket listings in one row) is left unlabelled."""
    groups = {}
    for l in listings:
        section = " ".join(l.section.split())
        key = (section, l.row, l.price_cents, l.face_value, l.total_price, l.stock_type, l.in_hand_date, l.notes)
        groups.setdefault(key, []).append(l)
    for key, grp in groups.items():
        qs = sorted(l.quantity for l in grp)
        if len(grp) < 2 or qs != list(range(1, len(grp) + 1)):
            continue
        if any(list(l.split_quantities) != list(range(1, l.quantity + 1)) for l in grp):
            continue
        label = f"{key[0]}|{key[1]}|{key[2]}"
        for l in grp:
            l.ladder_group, l.is_ladder_max = label, l.quantity == len(grp)
    return listings


def build_result(event, inventory: dict) -> dict:
    """event + captured inventory JSON (client.get_inventory) -> the
    normalized result shared by cli.py and api.py:
    {event, price_levels, listings, coverage, notes}. Pure - no network."""
    from .parser import parse_price, parse_seats, section_count, parse_marketplace_offers, parse_offer_rules

    if inventory.get("mp_offers") is not None:
        # AXS Marketplace (best_available) flow - listings arrive bundled.
        listings, meta = parse_marketplace_offers(inventory["mp_offers"])
        mark_ladders(listings)
        tickets = sum(l.quantity for l in listings)
        mp_id = (meta.get("event") or {}).get("id")
        if mp_id is not None:
            event.marketplace_event_id = mp_id
        # sections with tickets straight from the listings - mapinfo is not needed
        lc, tc = meta.get("listingCount"), meta.get("ticketCount")
        coverage = (f"flow=marketplace listings={len(listings)}/{lc} tickets={tickets}/{tc} "
                    f"sections_with_tickets={len({l.section for l in listings})} "
                    f"match={'yes' if (lc, tc) == (len(listings), tickets) else 'NO'}")
        notes = list(inventory.get("notes") or [])
        if not listings:
            notes.append("marketplace has no listings right now")
        return {"event": event, "price_levels": [], "listings": listings,
                "coverage": coverage, "notes": notes}

    price_levels = parse_price(inventory.get("price") or {})
    seats, resale_meta = parse_seats(inventory.get("offer_search") or {})
    offer_rules = parse_offer_rules(inventory.get("price") or {}, inventory.get("offer_search") or {})
    listings = group_into_listings(seats, price_levels, resale_meta, offer_rules)
    resale = sum(1 for s in seats if s.is_resale)
    coverage = (f"flow=veritix sections={section_count(inventory.get('sections'))} price_levels={len(price_levels)} "
                f"seats={len(seats)} (primary={len(seats) - resale} resale={resale}) "
                f"listings={len(listings)} seats_in_listings={sum(l.quantity for l in listings)}")
    return {"event": event, "price_levels": price_levels, "listings": listings,
            "coverage": coverage, "notes": list(inventory.get("notes") or [])}
