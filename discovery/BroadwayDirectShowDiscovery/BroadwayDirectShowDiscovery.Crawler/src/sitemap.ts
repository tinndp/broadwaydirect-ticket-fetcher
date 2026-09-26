/**
 * Stage 1: discover every currently-ticketed show's Tixtrack series ID.
 *
 * broadwaydirect.com's own Yoast SEO sitemap (show-sitemap.xml) lists every
 * /show/{slug}/ marketing page the site has ever published - including many
 * long-closed shows (Hamilton's original run, Dear Evan Hansen, Phantom,
 * Come From Away, ...). Checking ALL of them (538 as of writing) does
 * surface some closed shows whose old marketing page still links to a
 * stale tickets.broadwaydirect.com series page - checking every page is
 * the only fully-correct way to not miss a currently-selling show, but it
 * roughly triples crawl time in stage 2 (events.ts) for series that will
 * just come back with 0 performances in the requested window.
 *
 * By default we filter to show pages whose sitemap <lastmod> is within
 * `daysBack` days - this reliably captures "currently on sale" without the
 * noise, at the cost of possibly missing a show that's on sale again but
 * whose marketing page hasn't been touched recently. Pass daysBack=null to
 * check every show page (slower, most complete).
 *
 * Shows ticketed by a 3rd party (Telecharge, ATG, criterionticketing.com,
 * etc.) have no tickets.broadwaydirect.com link on their page and are
 * skipped either way - they're out of this project's scope (Tixtrack/
 * Broadway Direct platform only).
 */
import type { Page } from "patchright";
import { passCloudflare, fetchTextBatch } from "./browser.js";

const SITEMAP_INDEX = "https://broadwaydirect.com/sitemap_index.xml";
const SHOWS_PAGE = "https://broadwaydirect.com/shows/"; // used only to bootstrap past Cloudflare

const LOC_RE = /<loc>(.*?)<\/loc>\s*(?:<lastmod>(.*?)T.*?<\/lastmod>)?/gs;
const SERIES_RE = /tickets\.broadwaydirect\.com[^"']*series\/(\d+)/;

export const DEFAULT_DAYS_BACK = 75;

function findLocs(text: string): Array<{ loc: string; lastmod?: string }> {
  const out: Array<{ loc: string; lastmod?: string }> = [];
  for (const m of text.matchAll(LOC_RE)) {
    out.push({ loc: m[1], lastmod: m[2] });
  }
  return out;
}

async function showSitemapUrl(page: Page): Promise<string> {
  const text = await page.evaluate(async (u: string) => (await fetch(u)).text(), SITEMAP_INDEX);
  for (const { loc } of findLocs(text)) {
    if (loc.includes("show-sitemap")) return loc;
  }
  throw new Error("show-sitemap.xml not found in sitemap_index.xml - site structure may have changed");
}

/** Returns Map<seriesId, showSlug>. `page` must belong to a browser
 * context that will be used for the whole discovery step. */
export async function discoverSeriesIds(
  page: Page,
  daysBack: number | null = DEFAULT_DAYS_BACK,
): Promise<Map<string, string>> {
  const ok = await passCloudflare(page, SHOWS_PAGE);
  if (!ok) throw new Error("could not pass Cloudflare on broadwaydirect.com");

  const sitemapUrl = await showSitemapUrl(page);
  const text = await page.evaluate(async (u: string) => (await fetch(u)).text(), sitemapUrl);

  const cutoff = daysBack !== null ? new Date(Date.now() - daysBack * 86400_000) : null;
  const showUrls: string[] = [];
  for (const { loc, lastmod } of findLocs(text)) {
    if (!loc.includes("/show/")) continue;
    if (cutoff !== null) {
      if (!lastmod) continue;
      const d = new Date(lastmod);
      if (isNaN(d.getTime()) || d < cutoff) continue;
    }
    showUrls.push(loc);
  }
  console.error(
    `  ${showUrls.length} show pages to check` +
      (daysBack !== null ? ` (lastmod within ${daysBack}d)` : " (all)"),
  );

  const bodies = await fetchTextBatch(page, showUrls);

  const seriesToSlug = new Map<string, string>();
  let failed = 0;
  for (const [url, body] of bodies) {
    if (body === null) {
      failed++;
      continue;
    }
    const m = SERIES_RE.exec(body);
    if (m) {
      const slug = url.replace(/\/+$/, "").split("/").pop() ?? url;
      seriesToSlug.set(m[1], slug);
    }
  }
  if (failed) console.error(`  !! ${failed} show pages failed to fetch (transient network errors, skipped)`);
  console.error(`  ${seriesToSlug.size} shows are ticketed via tickets.broadwaydirect.com`);
  return seriesToSlug;
}
