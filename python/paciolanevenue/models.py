"""Shared data structures - same shape as broadwaydirect/models.py, adapted
to eVenue's field names."""

from dataclasses import dataclass, field
from typing import Optional


@dataclass
class Event:
    """host/season_cd/item_cd identify the event on evenue.net;
    data_account_id/distributor_id/policy_cd/policy_type are what the
    seat-availability API needs - all read off the event page's own
    __NEXT_DATA__, nothing hard-coded per school (see parser.py)."""
    host: str
    season_cd: str
    item_cd: str
    data_account_id: str = ""
    distributor_id: str = ""
    policy_cd: str = ""
    policy_type: str = ""
    event_name: str = ""
    facility_title: str = ""
    event_dt_utc: Optional[str] = None      # ISO string, a real UTC instant (has "Z")
    event_dt_fac_raw: Optional[str] = None  # NOT UTC despite its own "Z" - see parser.py
    hide_time: bool = False
    hide_date_time: bool = False
    sold_out: bool = False
    total_capacity_ssr: Optional[int] = None
    available_ssr: Optional[int] = None

    @property
    def event_url(self) -> str:
        return f"https://{self.host}/event/{self.season_cd}/{self.item_cd}"


@dataclass
class PriceLevel:
    """PL is the price-level code SeatRow.price_level_cd references; PT is
    the ticket TYPE (e.g. "P"=Public). Units for price/per_ticket_fee/
    facility_fee are UNCONFIRMED (see README.md "Open questions") - kept
    raw, never silently combined."""
    pl: str
    pl_desc: str
    pt: str
    pt_desc: str
    price: int = 0
    per_ticket_fee: int = 0
    facility_fee: int = 0


@dataclass
class SeatRow:
    """One row of the seat-availability response. seat_num is None when
    seat_cd didn't parse as an integer (bleacher/GA labels like "C-1" were
    seen) - see README.md."""
    level_section_cd: str   # raw "LEVEL:SECTION", e.g. "E:101"
    level: str
    section: str
    row_cd: str
    seat_cd: str
    seat_num: Optional[int]
    price_level_cd: str
    seat_status: str
    marker_id: Optional[str] = None
    seat_marker_active: Optional[bool] = None
    available: bool = False
    hidden: bool = False

    @property
    def seat_key(self) -> str:
        # Row:SeatCd only - Level and Section BOTH dropped per user request
        # (grouping/bucketing logic is unaffected: it still keys on the
        # full level_section_cd, see grouping.py). Level lives on
        # Listing.level, Section on Listing.section - a bare seat_key is
        # only unique/meaningful together with those 2 fields from the
        # same document, not on its own.
        return f"{self.row_cd}:{self.seat_cd}"


@dataclass
class Listing:
    # The Level half of LEVELSECTIONCD only ("OK:107" -> "OK"), plus a
    # grouping-rule suffix if any (e.g. "OK SIDES"). Output field name is
    # "Level" (Mongo) / "level" (JSON) - see mongo_inventory.py/api.py.
    level: str
    row: str
    price_level_cd: str
    seat_keys: list = field(default_factory=list)
    # The Section half of LEVELSECTIONCD ("OK:107" -> "107"), Level dropped -
    # the counterpart to `level` above. Output field name is "Section"
    # (Mongo, matches BroadwayDirectSourceInventory.Section there) / "section"
    # (JSON). Populated for every listing, including Ungrouped ones.
    section: str = ""
    seat_nums: list = field(default_factory=list)   # empty when Ungrouped
    seat_cds: list = field(default_factory=list)
    seating_type: str = "Consecutive"  # "Consecutive" | "OddEven" | "Ungrouped"

    @property
    def quantity(self) -> int:
        return len(self.seat_keys)

    @property
    def seat_range_label(self) -> str:
        if self.seat_nums:
            if len(self.seat_nums) == 1:
                return str(self.seat_nums[0])
            return f"{self.seat_nums[0]}-{self.seat_nums[-1]}"
        return "/".join(self.seat_cds) if self.seat_cds else ""
