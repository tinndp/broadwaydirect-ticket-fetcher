/**
 * Stage 2: pull every performance (event) for every discovered series
 * across a month range via the Tixtrack consumer API:
 *
 *   GET /api/consumer/events/getbymonth/{seriesId}
 *       ?requestedTime={year}/{month}/01&salesChannel=Web&promoCode=
 *
 * which returns every performance scheduled in that month: id, name,
 * localDate (and more we don't need here).
 *
 * IMPORTANT finding (measured, not assumed, in the sibling Python
 * project): the Cloudflare `cf_clearance` for tickets.broadwaydirect.com
 * is domain-wide, not per-series. Passing Cloudflare ONCE on any one
 * series page is enough to fetch every OTHER series's API directly by
 * URL - verified across 36 real series with zero errors. That cut the
 * event-crawl stage from ~5-8 minutes (one Cloudflare pass per series)
 * down to ~15 seconds for the same ~216 requests. So we bootstrap once
 * (see `bootstrap`) and then fire every request as one big batched,
 * same-origin in-page fetch.
 */
import type { Page } from "patchright";
import { passCloudflare, fetchTextBatch } from "./browser.js";

const API = (seriesId: string) =>
  `https://tickets.broadwaydirect.com/api/consumer/events/getbymonth/${seriesId}`;
const SERIES_PAGE = (seriesId: string) =>
  `https://tickets.broadwaydirect.com/shop/tickets/series/${seriesId}`;

export const FETCH_CONCURRENCY = 25; // measured safe up to 200 concurrent / ~750 in under 3 min - see README

export interface EventRow {
  eventUrl: string;
  eventName: string;
  eventId: number;
  eventDatetime: string;
  seriesId: string;
  showSlug: string;
}

export type YearMonth = [year: number, month: number];

function* monthRange(start: YearMonth, end: YearMonth): Generator<YearMonth> {
  let [y, m] = start;
  const [ey, em] = end;
  while (y < ey || (y === ey && m <= em)) {
    yield [y, m];
    m++;
    if (m > 12) {
      m = 1;
      y++;
    }
  }
}

/** Passes Cloudflare once for tickets.broadwaydirect.com - call this
 * before crawlAll/crawlSeries. `anySeriesId` just needs to be a real
 * series ID (its own page doesn't matter beyond that - the resulting
 * cf_clearance covers the whole host). The bare domain root does NOT
 * work as a bootstrap target - it redirects to the marketing site
 * (broadwaydirect.com), a different origin. */
export async function bootstrap(page: Page, anySeriesId: string): Promise<boolean> {
  return passCloudflare(page, SERIES_PAGE(anySeriesId));
}

interface GetByMonthResponse {
  events?: Array<{ id: number; name?: string; localDate?: string }>;
}

/** Assumes `page` has already passed Cloudflare on
 * tickets.broadwaydirect.com (see `bootstrap`). Returns {rows,
 * failedSeries} - failedSeries is every series where EVERY month request
 * failed to even return parseable JSON (network hiccup, not "0
 * performances" which is a normal successful response), so the caller
 * can tell the two apart. */
export async function crawlAll(
  page: Page,
  seriesToSlug: Map<string, string>,
  start: YearMonth,
  end: YearMonth,
): Promise<{ rows: EventRow[]; failedSeries: string[] }> {
  const urlToSeries = new Map<string, string>();
  for (const seriesId of seriesToSlug.keys()) {
    for (const [y, m] of monthRange(start, end)) {
      const url = `${API(seriesId)}?requestedTime=${y}/${m}/01&salesChannel=Web&promoCode=`;
      urlToSeries.set(url, seriesId);
    }
  }

  const bodies = await fetchTextBatch(page, [...urlToSeries.keys()], FETCH_CONCURRENCY);

  const rowsById = new Map<number, EventRow>();
  const okSeries = new Set<string>();
  for (const [url, body] of bodies) {
    const seriesId = urlToSeries.get(url)!;
    if (!body) continue;
    let data: GetByMonthResponse;
    try {
      data = JSON.parse(body);
    } catch {
      continue;
    }
    okSeries.add(seriesId); // a valid JSON response, even with 0 events, counts as "checked"
    for (const e of data.events ?? []) {
      rowsById.set(e.id, {
        eventUrl: `https://tickets.broadwaydirect.com/shop/tickets/series/${seriesId}/${e.id}`,
        eventName: e.name ?? "",
        eventId: e.id,
        eventDatetime: e.localDate ?? "",
        seriesId,
        showSlug: seriesToSlug.get(seriesId)!,
      });
    }
  }

  const failedSeries = [...seriesToSlug.keys()].filter((sid) => !okSeries.has(sid));
  for (const sid of failedSeries) {
    console.error(`  !! series ${sid} (${seriesToSlug.get(sid)}): every month request failed, skipped`);
  }

  const bySeries = new Map<string, number>();
  for (const r of rowsById.values()) bySeries.set(r.seriesId, (bySeries.get(r.seriesId) ?? 0) + 1);
  for (const [sid, slug] of seriesToSlug) {
    if (okSeries.has(sid)) console.error(`  [${slug}] series ${sid}: ${bySeries.get(sid) ?? 0} performances`);
  }

  return { rows: [...rowsById.values()], failedSeries };
}

/** Single-series convenience entry point - passes Cloudflare itself if
 * needed. Prefer crawlAll() for multiple series (one bootstrap instead of
 * one per series). */
export async function crawlSeries(
  page: Page,
  seriesId: string,
  slug: string,
  start: YearMonth,
  end: YearMonth,
): Promise<EventRow[]> {
  const ok = await bootstrap(page, seriesId);
  if (!ok) {
    console.error(`  !! could not pass Cloudflare for series ${seriesId} (${slug}), skipping`);
    return [];
  }
  const { rows } = await crawlAll(page, new Map([[seriesId, slug]]), start, end);
  return rows;
}
