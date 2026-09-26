/**
 * Cloudflare-bypass browser session shared by the sitemap and events stages.
 *
 * Both broadwaydirect.com (the marketing site) and tickets.broadwaydirect.com
 * (the Tixtrack ticketing platform) sit behind their own Cloudflare Managed
 * Challenge. A plain fetch/axios call gets a 403 "Just a moment..." on
 * either host.
 *
 * We also deliberately do NOT use Playwright/patchright's
 * `context.request.get()` for the actual data calls: that's a separate
 * HTTP client that does not share the real browser's TLS/HTTP fingerprint
 * even though it shares cookies, and was observed (in the sibling Python
 * project) getting blocked by Cloudflare even with a valid `cf_clearance`
 * cookie. Running `fetch()` *inside* a real page (`page.evaluate`) goes
 * through the browser's own network stack and was reliable in testing
 * (thousands of calls, zero blocks).
 */
import { chromium, type Browser, type BrowserContext, type Page } from "patchright";

const CF_WAIT_MS = 8000;
const CF_TRIES = 3;
const CF_POLL_MS = 300;

export interface Session {
  browser: Browser;
  context: BrowserContext;
}

/** headless=false is required - Cloudflare detects and blocks headless
 * Chromium even with patchright's fingerprint patches. */
export async function launch(headless = false): Promise<Session> {
  const browser = await chromium.launch({ headless });
  const context = await browser.newContext({ viewport: { width: 1280, height: 800 } });
  return { browser, context };
}

/** Navigates to `url` and waits out its Cloudflare challenge. Polls the
 * page title every 300ms instead of blind-sleeping the full CF_WAIT_MS, so
 * a page that clears in ~2s (common case) doesn't pay the full ~8s cost -
 * this matters a lot since it runs once per bootstrap. Returns false
 * (after `tries` attempts) if the challenge never clears - callers should
 * skip that host/page rather than hang forever. */
export async function passCloudflare(page: Page, url: string, tries = CF_TRIES): Promise<boolean> {
  for (let attempt = 0; attempt < tries; attempt++) {
    await page.goto(url, { waitUntil: "domcontentloaded", timeout: 30000 });

    let elapsed = 0;
    let title = await page.title();
    while (elapsed < CF_WAIT_MS && title.toLowerCase().includes("just a moment")) {
      await page.waitForTimeout(CF_POLL_MS);
      elapsed += CF_POLL_MS;
      title = await page.title();
    }

    if (!title.toLowerCase().includes("just a moment")) {
      try {
        await page.waitForLoadState("networkidle", { timeout: 3000 });
      } catch {
        // best-effort
      }
      return true;
    }
    console.error(`  !! still on Cloudflare challenge at ${url} (attempt ${attempt + 1}/${tries})`);
  }
  return false;
}

interface FetchResult {
  status: number;
  body: string | null;
}

/** Fetches many same-origin URLs from inside the page in one round trip
 * per batch (JS-side Promise.all), instead of one CDP round trip per URL -
 * this is what makes crawling hundreds of pages fast. `page` must already
 * be on a page served by the same host as every url in `urls`. */
export async function fetchTextBatch(
  page: Page,
  urls: string[],
  concurrency = 15,
): Promise<Map<string, string | null>> {
  const out = new Map<string, string | null>();
  for (let i = 0; i < urls.length; i += concurrency) {
    const chunk = urls.slice(i, i + concurrency);
    const results = await page.evaluate(async (chunkUrls: string[]) => {
      const fetchOne = async (u: string) => {
        try {
          const r = await fetch(u, { headers: { Accept: "text/html,application/xml,application/json,*/*" } });
          return { status: r.status, body: await r.text() };
        } catch {
          return { status: -1, body: null };
        }
      };
      return Promise.all(chunkUrls.map(fetchOne));
    }, chunk);
    chunk.forEach((u, idx) => out.set(u, (results as FetchResult[])[idx].body));
  }
  return out;
}
