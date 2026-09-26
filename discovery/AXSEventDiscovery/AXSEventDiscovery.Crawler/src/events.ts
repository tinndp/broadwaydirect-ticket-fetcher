/**
 * One AXS search hit -> one EventRow. Field names follow Broadway's EventRow
 * (eventUrl, eventName, eventId, eventDatetime, eventDatetimeUtc, venue, city,
 * description), plus the AXS fields the inventory crawler and SQL sync need.
 */

/** The fields we read from one `results[]` item of /v1/Discovery/Events. */
export interface SearchHit {
  eventId: number;
  eventURL?: string | null;
  eventSlug?: string | null;
  eventTitle?: string | null;
  headlinersText?: string | null;
  description?: string | null;
  eventDatetimeLocal?: string | null;
  eventDatetimeUtc?: string | null;
  eventEndDatetimeUtc?: string | null;
  eventTimezone?: string | null;
  eventDatetimeTbd?: boolean | null;
  onsaleDateTimeUtc?: string | null;
  majorCategory?: string | null;
  performerIds?: string[] | null;
  ticketingStatusText?: string | null;
  axsTicketed?: boolean | null;
  axsMarketplaceEvent?: boolean | null;
  venue?: {
    venueId?: number;
    venueTitle?: string | null;
    address?: { city?: string | null; stateProvince?: string | null; countryCode?: string | null } | null;
  } | null;
}

export interface EventRow {
  eventUrl: string;
  eventName: string;
  eventId: number;
  eventDatetime: string | null; // local venue time, as returned (no offset)
  eventDatetimeUtc: string | null; // real UTC with "Z" (the API omits it)
  eventEndDatetimeUtc: string | null; // multi-day events only
  timezone: string | null;
  venue: string | null;
  venueId: number | null;
  city: string | null;
  state: string | null;
  countryCode: string | null;
  description: string | null;
  performerIds: string | null; // comma-joined
  category: string | null;
  // How the event is sold on AXS - decides which inventory flow applies:
  // axsTicketed = primary tickets on AXS (Veritix), axsMarketplaceEvent = AXS resale marketplace.
  // Neither = listed on axs.com but sold elsewhere (nothing for the inventory crawler).
  axsTicketed: boolean;
  axsMarketplaceEvent: boolean;
  ticketingStatus: string | null; // e.g. "Buy Tickets", "Sold Out", "Cancelled"
  dateTbd: boolean;
  onsaleDatetimeUtc: string | null;
}

/** "2026-09-26T22:00:00" (UTC without zone) -> "2026-09-26T22:00:00Z". */
export function utc(s: string | null | undefined): string | null {
  if (!s) return null;
  return /[zZ]|[+-]\d\d:?\d\d$/.test(s) ? s : `${s}Z`;
}

export function toRow(h: SearchHit): EventRow {
  const addr = h.venue?.address ?? null;
  return {
    eventUrl: h.eventURL || `https://www.axs.com/events/${h.eventId}/${h.eventSlug ?? ""}`,
    eventName: (h.eventTitle || h.headlinersText || "").trim(),
    eventId: h.eventId,
    eventDatetime: h.eventDatetimeLocal ?? null,
    eventDatetimeUtc: utc(h.eventDatetimeUtc),
    eventEndDatetimeUtc: utc(h.eventEndDatetimeUtc),
    timezone: h.eventTimezone ?? null,
    venue: h.venue?.venueTitle ?? null,
    venueId: h.venue?.venueId ?? null,
    city: addr?.city ?? null,
    state: addr?.stateProvince ?? null,
    countryCode: addr?.countryCode ?? null,
    description: h.description || null,
    performerIds: h.performerIds && h.performerIds.length ? h.performerIds.join(",") : null,
    category: h.majorCategory ?? null,
    axsTicketed: !!h.axsTicketed,
    axsMarketplaceEvent: !!h.axsMarketplaceEvent,
    ticketingStatus: h.ticketingStatusText ?? null,
    dateTbd: !!h.eventDatetimeTbd,
    onsaleDatetimeUtc: utc(h.onsaleDateTimeUtc),
  };
}

/** Upcoming = starts in the future, or a multi-day event that hasn't ended yet. */
export function isUpcoming(r: EventRow, nowIso: string): boolean {
  const last = r.eventEndDatetimeUtc ?? r.eventDatetimeUtc;
  return !!last && last > nowIso;
}
