"""Shared data structures - same shape philosophy as broadwaydirect/models.py
(Event / PriceLevel / Seat / Listing), adapted to AXS field names. See
docs/RECON.md for where each field comes from."""

from dataclasses import dataclass, field
from typing import Optional


@dataclass
class Event:
    """One AXS event, read off the event page's own __NEXT_DATA__
    (props.pageProps.discoveryEventData) - see parser.parse_event."""
    event_id: int                      # www.axs.com/events/{event_id}/{slug}
    slug: str = ""
    name: str = ""
    venue_id: Optional[int] = None
    venue_name: str = ""
    venue_city: str = ""
    venue_state: str = ""
    performer_ids: list = field(default_factory=list)
    event_dt_local: Optional[str] = None   # "2026-10-04T19:00:00" - venue wall clock, no offset
    event_dt_utc: Optional[str] = None     # "2026-10-04T23:00:00Z" - real UTC ("Z" added, AXS omits it)
    event_tz: str = ""                     # IANA, e.g. "America/New_York"
    door_dt_utc: Optional[str] = None
    onsale_dt_utc: Optional[str] = None
    status_id: Optional[int] = None        # ticketing.statusId
    status: str = ""                       # ticketing.status, e.g. "Buy Tickets"
    ticket_url: Optional[str] = None       # https://shop.axs.com/?c=axs&e=... (None = not sold on AXS/Veritix)
    publish_status: Optional[int] = None
    marketplace_event_id: Optional[int] = None  # AXS Marketplace's own id (offers.meta.event.id), marketplace flow only

    @property
    def event_url(self) -> str:
        return f"https://www.axs.com/events/{self.event_id}/{self.slug}"


@dataclass
class PriceLevel:
    """One (offer, price level, price type) price from inventory/v4/.../price.
    Amounts are CENTS as returned by AXS (7500 = $75.00) - converted to
    dollars only when writing Mongo/JSON output, never summed with fees."""
    offer_id: str
    offer_name: str
    price_level_id: str
    label: str                 # e.g. "P5"
    price_type_id: str
    price_type_label: str      # e.g. "Regular"
    price_cents: int
    available: Optional[int] = None   # availability.amount
    is_resale: bool = False


@dataclass
class Seat:
    """One seat from inventory/V2/.../offer/search. Primary seats carry
    section_id/row_id/price_level_id; resale (FLASHSEATS) seats carry only
    labels, the offer id identifies the resale listing."""
    offer_id: str
    section: str
    row: str
    number: str
    seat_num: Optional[int]    # None when `number` isn't an integer
    price_level_id: str = ""
    section_id: str = ""
    row_id: str = ""
    seat_id: str = ""
    status_label: str = ""     # "Open" | "Aisle" | "ADA" | ...
    seat_type: str = ""        # "STANDARD" | "ACCESSIBLE" | "FLASHSEATS" | ...
    neighborhood: str = ""
    is_ga: bool = False
    is_resale: bool = False

    @property
    def key(self) -> str:
        return f"{self.section}-{self.row}-{self.number}"


@dataclass
class Listing:
    """Primary: contiguous seats of one (offer, section, row, price level).
    Resale: one FLASHSEATS offer as the seller listed it (already bundled,
    same as StubHub listings - never re-grouped).
    Marketplace: one AXS Marketplace listing (best_available flow,
    /axsmarketplace/offers) - no seat numbers, only a quantity."""
    offer_id: str
    section: str
    row: str
    price_level_id: str = ""
    seat_keys: list = field(default_factory=list)
    seat_nums: list = field(default_factory=list)
    seating_type: str = "Consecutive"      # "Consecutive" | "Ungrouped" | "Resale" | "Marketplace"
    price_cents: int = 0                   # face/list price per ticket, cents
    total_price: Optional[float] = None    # resale only: AXS totalPrice per ticket incl. fees, dollars
    fee_per_ticket: Optional[float] = None # resale only: connectionFee, dollars
    split_rule: str = ""                   # resale only: purchasableQuantityRule (FIXED/ALL/EVEN)
    split_quantities: list = field(default_factory=list)
    is_resale: bool = False
    zone: str = ""                         # price level label (primary) or neighborhood
    quantity_override: Optional[int] = None  # marketplace: listing quantity (no seat keys)
    stock_type: str = ""                   # marketplace: e.g. "Ticketmaster Transfer"
    in_hand_date: str = ""                 # marketplace: e.g. "11/03/26" (as returned)
    notes: str = ""                        # marketplace: seller notes
    seat_features: list = field(default_factory=list)  # marketplace: AXS labels, e.g. "Restricted/Obstructed View"
    face_value: Optional[float] = None     # marketplace: faceValue as returned (null on most listings)
    # Marketplace "ladder" (2026-09-25, docs/AXS_LISTING_RULES.md): the same seats posted as quantity
    # 1, 2, .., n. Only LABELLED - every listing is kept. None when the listing is not in a ladder.
    ladder_group: Optional[str] = None     # "<section>|<row>|<price in cents>"
    is_ladder_max: Optional[bool] = None   # True on the n-ticket listing of the ladder
    # Purchase-quantity rules exactly as AXS returns them (no crawler logic):
    min_quantity: Optional[int] = None     # primary: price offerPrices[].min
    max_quantity: Optional[int] = None     # primary: offerPrices[].max; marketplace: meta.maxTicketCount
    quantity_increment: Optional[int] = None  # primary: offerPrices[].increment
    allow_empty_single_seats: Optional[bool] = None  # primary: offer/search offers[].allowEmptySingleSeats
    require_contiguous_seats: Optional[bool] = None  # primary: offer/search offers[].requireContiguousSeats

    @property
    def quantity(self) -> int:
        return self.quantity_override if self.quantity_override is not None else len(self.seat_keys)

    @property
    def seat_range_label(self) -> str:
        if not self.seat_nums:
            return ""
        if len(self.seat_nums) == 1:
            return str(self.seat_nums[0])
        return f"{self.seat_nums[0]}-{self.seat_nums[-1]}"
