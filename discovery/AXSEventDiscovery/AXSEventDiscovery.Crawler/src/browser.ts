/**
 * Browser session for AXS discovery.
 *
 * www.axs.com sits behind Cloudflare with an interactive Turnstile check. The
 * bundled Chromium gets stuck on it; real Google Chrome (`channel: "chrome"`)
 * clears it by itself in 3-6s. So we launch Chrome, and fall back to the
 * bundled Chromium only if Chrome isn't installed.
 *
 * The data calls go to AXS's search API
 * (unifiedapisearch.discovery-prod.axs.com). A plain curl to it gets 403,
 * but the same call made with `fetch()` from inside a www.axs.com page works,
 * because that is how the site itself calls it (CORS allows www.axs.com). As
 * on Broadway we never use `context.request` for data calls.
 *
 * The proxy is optional. `{SESSIONID}` in its username is replaced with a new
 * random id on every launch, so a relaunch after a block gets a new IP.
 */
import { randomBytes } from "node:crypto";
import { chromium, type Browser, type Page } from "patchright";

const CF_WAIT_MS = 40000;
const CF_POLL_MS = 500;
const CHALLENGE_TITLES = ["just a moment", "attention required", "access denied"];

export interface Session {
  browser: Browser;
  page: Page;
  proxySession: string | null;
}

interface ProxySettings {
  server: string;
  username?: string;
  password?: string;
}

/** "http://user:pass@host:port" (username may contain {SESSIONID}) -> patchright proxy settings. */
function parseProxy(url: string, sessionId: string): ProxySettings {
  const u = new URL(url.replace(/\{SESSIONID\}/g, sessionId));
  const out: ProxySettings = { server: `${u.protocol}//${u.host}` };
  if (u.username) out.username = decodeURIComponent(u.username);
  if (u.password) out.password = decodeURIComponent(u.password);
  return out;
}

/** headless=false by default - Cloudflare blocks headless Chrome. */
export async function launch(headless = false, proxyUrl: string | null = null): Promise<Session> {
  const proxySession = proxyUrl ? "s" + randomBytes(6).toString("hex") : null;
  const proxy = proxyUrl ? parseProxy(proxyUrl, proxySession!) : undefined;
  let browser: Browser;
  try {
    browser = await chromium.launch({ headless, channel: "chrome", proxy });
  } catch (err) {
    console.error(`  (Google Chrome not available: ${String(err).split("\n")[0]} - using bundled Chromium)`);
    browser = await chromium.launch({ headless, proxy });
  }
  const context = await browser.newContext({ viewport: { width: 1280, height: 800 } });
  const page = await context.newPage();
  return { browser, page, proxySession };
}

function isChallenge(title: string): boolean {
  const t = title.toLowerCase();
  return CHALLENGE_TITLES.some((c) => t.includes(c));
}

/** Opens `url` and polls the title until the Cloudflare challenge is gone.
 * Returns false (never hangs) if it doesn't clear within CF_WAIT_MS - the
 * caller relaunches with a new proxy session. */
export async function passCloudflare(page: Page, url: string): Promise<boolean> {
  try {
    await page.goto(url, { waitUntil: "domcontentloaded", timeout: 60000 });
  } catch (err) {
    console.error(`  !! could not open ${url}: ${String(err).split("\n")[0]}`);
    return false;
  }
  let elapsed = 0;
  let title = await page.title().catch(() => "");
  while (elapsed < CF_WAIT_MS && (isChallenge(title) || title === "")) {
    await page.waitForTimeout(CF_POLL_MS);
    elapsed += CF_POLL_MS;
    title = await page.title().catch(() => "");
  }
  return !isChallenge(title) && title !== "";
}

export interface FetchResult {
  status: number; // 0 = fetch() threw (network error, or an error response without CORS headers)
  body: string | null;
}

/** Fetches `urls` from inside the page, all in parallel (one CDP round trip).
 * The page must be a www.axs.com page that never reloads itself (crawl.ts uses
 * /robots.txt): a reload mid-batch throws "Execution context was destroyed",
 * which crawl.ts handles like a closed browser (new session, same day). */
export async function fetchTextBatch(page: Page, urls: string[]): Promise<FetchResult[]> {
  return page.evaluate(async (list: string[]) => {
    const one = async (u: string) => {
      const ctl = new AbortController();
      const timer = setTimeout(() => ctl.abort(), 60000); // a hung request must not stall the run
      try {
        const r = await fetch(u, { cache: "no-store", headers: { Accept: "application/json" }, signal: ctl.signal });
        return { status: r.status, body: await r.text() };
      } catch {
        return { status: 0, body: null };
      } finally {
        clearTimeout(timer);
      }
    };
    return Promise.all(list.map(one));
  }, urls);
}
