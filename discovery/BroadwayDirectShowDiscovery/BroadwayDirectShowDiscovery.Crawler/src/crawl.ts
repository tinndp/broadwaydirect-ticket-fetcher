/**
 * Orchestrates the full discover-then-crawl pipeline (used by both the CLI
 * and the Electron app) - one function so both front ends stay in sync
 * with the same Cloudflare bootstrap-once behavior documented in
 * events.ts.
 */
import { launch } from "./browser.js";
import { discoverSeriesIds } from "./sitemap.js";
import { bootstrap, crawlAll, type EventRow, type YearMonth } from "./events.js";

export interface CrawlResult {
  rows: EventRow[];
  failed: string[];
}

export async function runCrawl(
  start: YearMonth,
  end: YearMonth,
  headless: boolean,
  daysBack: number | null,
): Promise<CrawlResult> {
  const { browser, context } = await launch(headless);
  const page = await context.newPage();
  try {
    const seriesToSlug = await discoverSeriesIds(page, daysBack);
    if (seriesToSlug.size === 0) return { rows: [], failed: [] };

    // One Cloudflare pass for the whole tickets.broadwaydirect.com host
    // unlocks every series's API - see events.ts's module doc comment.
    const anySeriesId = seriesToSlug.keys().next().value as string;
    if (!(await bootstrap(page, anySeriesId))) {
      throw new Error("could not pass Cloudflare on tickets.broadwaydirect.com");
    }
    const { rows, failedSeries } = await crawlAll(page, seriesToSlug, start, end);
    return { rows, failed: failedSeries };
  } finally {
    await browser.close();
  }
}
