import type { EventRow } from "./events.js";

export const FIELDNAMES = ["event_url", "event_name", "event_id", "event_datetime", "series_id", "show_slug"];

function csvEscape(v: string | number): string {
  const s = String(v);
  return /[",\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
}

export function toCsv(rows: EventRow[]): string {
  const header = FIELDNAMES.join(",");
  const lines = rows.map((r) =>
    [r.eventUrl, r.eventName, r.eventId, r.eventDatetime, r.seriesId, r.showSlug].map(csvEscape).join(","),
  );
  return [header, ...lines].join("\n") + "\n";
}
