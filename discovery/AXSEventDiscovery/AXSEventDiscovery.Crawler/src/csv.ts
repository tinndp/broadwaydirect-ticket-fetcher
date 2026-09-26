import type { EventRow } from "./events.js";

// Broadway's first columns (event_url, event_name, event_id, event_datetime), then the AXS extras.
const COLUMNS: [string, (r: EventRow) => unknown][] = [
  ["event_url", (r) => r.eventUrl],
  ["event_name", (r) => r.eventName],
  ["event_id", (r) => r.eventId],
  ["event_datetime", (r) => r.eventDatetime],
  ["event_datetime_utc", (r) => r.eventDatetimeUtc],
  ["event_end_datetime_utc", (r) => r.eventEndDatetimeUtc],
  ["timezone", (r) => r.timezone],
  ["venue", (r) => r.venue],
  ["venue_id", (r) => r.venueId],
  ["city", (r) => r.city],
  ["state", (r) => r.state],
  ["country_code", (r) => r.countryCode],
  ["performer_ids", (r) => r.performerIds],
  ["category", (r) => r.category],
  ["axs_ticketed", (r) => r.axsTicketed],
  ["axs_marketplace", (r) => r.axsMarketplaceEvent],
  ["ticketing_status", (r) => r.ticketingStatus],
  ["date_tbd", (r) => r.dateTbd],
  ["onsale_datetime_utc", (r) => r.onsaleDatetimeUtc],
  ["description", (r) => r.description],
];

export const FIELDNAMES = COLUMNS.map(([n]) => n);

function csvEscape(v: unknown): string {
  const s = v == null ? "" : String(v);
  return /[",\n\r]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
}

export function toCsv(rows: EventRow[]): string {
  const lines = rows.map((r) => COLUMNS.map(([, get]) => csvEscape(get(r))).join(","));
  return [FIELDNAMES.join(","), ...lines].join("\n") + "\n";
}
