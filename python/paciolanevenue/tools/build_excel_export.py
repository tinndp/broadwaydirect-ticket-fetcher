"""Builds a readable Excel export of the paciolanevenue demo's crawled
output (event info, price levels, listings) for the user to review before
deciding on grouping rules / fee formula. Reads output/*.json written by
paciolanevenue.cli - not part of the package itself."""

import json
from openpyxl import Workbook
from openpyxl.styles import Font, PatternFill, Alignment
from openpyxl.utils import get_column_letter

FONT = "Arial"
HEADER_FILL = PatternFill(start_color="D9E1F2", end_color="D9E1F2", fill_type="solid")
HEADER_FONT = Font(name=FONT, bold=True)
NOTE_FONT = Font(name=FONT, italic=True, size=9, color="808080")

EVENTS = [
    ("purduesports.evenue.net", "F06", "Purdue"),
    ("soonersports.evenue.net", "F03", "Oklahoma"),
]


def listing_quantity(l: dict) -> int:
    return len(l["seat_keys"])


def listing_seat_range(l: dict) -> str:
    # Mirrors Listing.seat_range_label - not present in the dumped JSON
    # since it's a @property, not a dataclass field (dataclasses.__dict__
    # only has actual fields).
    nums = l.get("seat_nums") or []
    if nums:
        return str(nums[0]) if len(nums) == 1 else f"{nums[0]}-{nums[-1]}"
    cds = l.get("seat_cds") or []
    return "/".join(cds) if cds else ""

wb = Workbook()
wb.remove(wb.active)


def style_header_row(ws, row, ncols):
    for c in range(1, ncols + 1):
        cell = ws.cell(row=row, column=c)
        cell.font = HEADER_FONT
        cell.fill = HEADER_FILL
        cell.alignment = Alignment(horizontal="center")


def autosize(ws, widths):
    for i, w in enumerate(widths, start=1):
        ws.column_dimensions[get_column_letter(i)].width = w


def set_default_font(ws, max_row, max_col):
    for row in ws.iter_rows(min_row=1, max_row=max_row, max_col=max_col):
        for cell in row:
            if cell.font is None or cell.font.name != FONT:
                if cell.font and (cell.font.bold or cell.font.italic):
                    continue
                cell.font = Font(name=FONT)


# --- Events summary sheet ---------------------------------------------------
ws = wb.create_sheet("Events")
headers = ["School", "Host", "Season", "ItemCd", "Event Name", "Facility", "Event Date (UTC)",
           "Time TBA?", "Sold Out?", "Total Capacity (SSR)", "Available (SSR)",
           "Rows Fetched", "Available Fetched", "Listings"]
ws.append(headers)
style_header_row(ws, 1, len(headers))

for host, item, school in EVENTS:
    ev = json.load(open(f"output/{host}/F26/{item}/event.json"))["event"]
    listings = json.load(open(f"output/{host}/F26/{item}/listings.json"))
    available_fetched = sum(listing_quantity(l) for l in listings)
    ws.append([
        school, host, ev["season_cd"], ev["item_cd"], ev["event_name"], ev["facility_title"],
        ev["event_dt_utc"], ev["hide_time"], ev["sold_out"],
        ev["total_capacity_ssr"], ev["available_ssr"],
        ev["total_capacity_ssr"], available_fetched, len(listings),
    ])

autosize(ws, [10, 26, 8, 8, 16, 26, 22, 10, 10, 18, 14, 12, 16, 10])
ws.freeze_panes = "A2"
ws["A" + str(len(EVENTS) + 3)] = ("Note: crawled 2026-09-21 via paciolanevenue (patchright + "
                                   "retry-on-PerimeterX-block). Seat availability is LIVE data, "
                                   "so \"Available Fetched\" can differ slightly from \"Available "
                                   "(SSR)\" captured moments earlier on the page itself.")
ws["A" + str(len(EVENTS) + 3)].font = NOTE_FONT

# --- Per-event sheets --------------------------------------------------------
for host, item, school in EVENTS:
    ev = json.load(open(f"output/{host}/F26/{item}/event.json"))
    listings = json.load(open(f"output/{host}/F26/{item}/listings.json"))

    # Price levels
    pl_sheet = wb.create_sheet(f"{school} Prices")
    pl_headers = ["PL Code", "PL Description", "PT Code", "PT Description",
                  "Price (raw)", "Price ($, /100)", "Per-Ticket Fee (raw)", "Facility Fee (raw)"]
    pl_sheet.append(pl_headers)
    style_header_row(pl_sheet, 1, len(pl_headers))
    for pl in ev["price_levels"]:
        pl_sheet.append([
            pl["pl"], pl["pl_desc"], pl["pt"], pl["pt_desc"],
            pl["price"], round(pl["price"] / 100, 2), pl["per_ticket_fee"], pl["facility_fee"],
        ])
    autosize(pl_sheet, [10, 20, 8, 14, 12, 16, 18, 14])
    pl_sheet.freeze_panes = "A2"
    note_row = len(ev["price_levels"]) + 3
    pl_sheet.cell(row=note_row, column=1,
                  value=("Note: units are UNCONFIRMED - \"Price ($, /100)\" assumes cents, based "
                         "on these numbers matching plausible real ticket prices, not a real "
                         "checkout. per_ticket_fee/facility_fee are NOT added into price here - "
                         "see README.md \"Open questions\"."))
    pl_sheet.cell(row=note_row, column=1).font = NOTE_FONT

    # Listings
    l_sheet = wb.create_sheet(f"{school} Listings")
    l_headers = ["Level", "Section", "Row", "Price Level Cd", "Seating Type", "Quantity",
                 "Seat Range", "Seat Keys"]
    l_sheet.append(l_headers)
    style_header_row(l_sheet, 1, len(l_headers))
    for l in listings:
        l_sheet.append([
            l["level"], l["section"], l["row"], l["price_level_cd"], l["seating_type"],
            listing_quantity(l), listing_seat_range(l), "; ".join(l["seat_keys"]),
        ])
    autosize(l_sheet, [10, 10, 8, 14, 14, 10, 14, 40])
    l_sheet.freeze_panes = "A2"

wb.active = 0
wb.save("PaciolanEvenue_Demo_Export.xlsx")
print("wrote PaciolanEvenue_Demo_Export.xlsx")
