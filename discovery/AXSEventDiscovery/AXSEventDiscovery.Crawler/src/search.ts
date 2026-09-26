/**
 * AXS search API - the one call that lists events with their dates:
 *
 *   GET https://unifiedapisearch.discovery-prod.axs.com/v1/Discovery/Events
 *       ?page=N&results=100&sortOrder=Date&sortDirection=Asc
 *       &filterCriteria=Date&dateStart=YYYY-MM-DDT00:00:00&dateEnd=...
 *
 * This is what axs.com's own search/category pages call. Measured on 2026-09-26
 * (see NOTES.md):
 *  - `results` must be 1..100 (500 -> HTTP 400).
 *  - One query returns at most 10,000 hits: `total` reads exactly 10000 and
 *    pages past 100 x 100 -> HTTP 500 (an Elasticsearch result window). So a
 *    date range is walked in windows small enough to stay under 10,000; one
 *    day held 340-2,049 events, and a window that still hits the cap is split
 *    in two.
 *  - Without `filterCriteria=Date` the dates are ignored (total stays 10000).
 *  - No geo parameters = every AXS site (US, UK, SE, AU, JP...).
 */
import type { Page } from "patchright";
import { fetchTextBatch } from "./browser.js";
import type { SearchHit } from "./events.js";

const API = "https://unifiedapisearch.discovery-prod.axs.com/v1/Discovery/Events";
const PAGE_SIZE = 100;
const RESULT_CAP = 10000;
const RETRY_DELAYS_MS = [2000, 5000, 10000];

export interface Window {
  start: Date;
  end: Date; // exclusive
}

/** Thrown when every retry of a request came back 0/403/429 - the session (IP) is
 * blocked or rate-limited; the caller relaunches with a new proxy session. */
export class BlockedError extends Error {}

function fmt(d: Date): string {
  return d.toISOString().slice(0, 19); // YYYY-MM-DDTHH:MM:SS, the format the site sends
}

export function windowLabel(w: Window): string {
  return `${fmt(w.start)}..${fmt(w.end)}`;
}

function searchUrl(w: Window, page: number): string {
  const q = new URLSearchParams({
    page: String(page),
    results: String(PAGE_SIZE),
    sortOrder: "Date",
    sortDirection: "Asc",
    filterCriteria: "Date",
    includeAdditionalDateFilter: "false",
    dateStart: fmt(w.start),
    dateEnd: fmt(w.end),
  });
  return `${API}?${q}`;
}

/** Whole days [start, end), one window per day. */
export function dayWindows(start: Date, end: Date): Window[] {
  const out: Window[] = [];
  for (let d = new Date(start); d < end; d = new Date(d.getTime() + 86400000)) {
    out.push({ start: d, end: new Date(Math.min(d.getTime() + 86400000, end.getTime())) });
  }
  return out;
}

interface SearchResponse {
  results?: SearchHit[];
  total?: number;
}

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

/** Fetches `urls` (in parallel) and retries the failed ones with backoff.
 * `counter.n` counts every request actually sent (retries included). */
async function fetchJsonWithRetry(page: Page, urls: string[], counter: { n: number }): Promise<(SearchResponse | null)[]> {
  const out: (SearchResponse | null)[] = urls.map(() => null);
  let todo = urls.map((u, i) => i);
  let lastStatuses: number[] = [];
  for (let attempt = 0; attempt <= RETRY_DELAYS_MS.length && todo.length; attempt++) {
    if (attempt > 0) await sleep(RETRY_DELAYS_MS[attempt - 1]);
    const res = await fetchTextBatch(page, todo.map((i) => urls[i]));
    counter.n += todo.length;
    const failed: number[] = [];
    lastStatuses = [];
    res.forEach((r, k) => {
      const i = todo[k];
      if (r.status === 200 && r.body) {
        try {
          out[i] = JSON.parse(r.body) as SearchResponse;
          return;
        } catch {
          // HTML challenge page with status 200 - counts as blocked
        }
      }
      lastStatuses.push(r.status === 200 ? 403 : r.status);
      failed.push(i);
    });
    todo = failed;
  }
  if (todo.length && lastStatuses.every((s) => s === 0 || s === 403 || s === 429)) {
    throw new BlockedError(`search API refused ${todo.length} request(s) (HTTP ${[...new Set(lastStatuses)].join("/")})`);
  }
  return out;
}

export interface WindowResult {
  window: Window;
  hits: SearchHit[];
  total: number; // what AXS reports for the window
  failedPages: number; // pages that kept failing with a non-block error (e.g. HTTP 500)
  capped: boolean; // still at the 10,000 cap after splitting down to 1 hour - hits are incomplete
}

/** Every hit in one window. Splits the window in two while it hits the 10,000 cap. */
export async function crawlWindow(
  page: Page, w: Window, concurrency: number, counter: { n: number }, log: (s: string) => void,
): Promise<WindowResult[]> {
  const [first] = await fetchJsonWithRetry(page, [searchUrl(w, 1)], counter);
  if (!first) return [{ window: w, hits: [], total: 0, failedPages: 1, capped: false }];
  const total = first.total ?? 0;
  const spanMs = w.end.getTime() - w.start.getTime();
  if (total >= RESULT_CAP && spanMs > 3600000) {
    const mid = new Date(w.start.getTime() + Math.floor(spanMs / 2 / 3600000) * 3600000);
    log(`  ${windowLabel(w)}: ${total}+ hits (cap) - splitting at ${fmt(mid)}`);
    return [
      ...(await crawlWindow(page, { start: w.start, end: mid }, concurrency, counter, log)),
      ...(await crawlWindow(page, { start: mid, end: w.end }, concurrency, counter, log)),
    ];
  }
  const hits = [...(first.results ?? [])];
  const pages = Math.ceil(Math.min(total, RESULT_CAP) / PAGE_SIZE);
  let failedPages = 0;
  for (let p = 2; p <= pages; p += concurrency) {
    const nums = Array.from({ length: Math.min(concurrency, pages - p + 1) }, (_, k) => p + k);
    const res = await fetchJsonWithRetry(page, nums.map((n) => searchUrl(w, n)), counter);
    for (const r of res) {
      if (r) hits.push(...(r.results ?? []));
      else failedPages++;
    }
  }
  return [{ window: w, hits, total, failedPages, capped: total >= RESULT_CAP }];
}

