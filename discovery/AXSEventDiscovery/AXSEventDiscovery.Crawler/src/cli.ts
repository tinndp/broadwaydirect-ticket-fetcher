/**
 * Entry point: list every upcoming AXS event in a date range and write one CSV
 * row per event (see csv.ts for the columns).
 *
 * Usage:
 *   npm run build && npm run cli -- --out events.csv                 # today + 180 days
 *   npm run cli -- --start 2026-10-01 --end 2026-10-31 --out oct.csv  # --end is the last day, included
 *   npm run cli -- --days 30 --proxy "http://user-{SESSIONID}:pass@host:port"
 *
 * The proxy can also come from the AXS_PROXY environment variable.
 */
import { writeFile } from "node:fs/promises";
import { runCrawl } from "./crawl.js";
import { toCsv } from "./csv.js";
import { formatDay, resolveDateRange, type DateRange } from "./dates.js";

interface Args {
  range: DateRange;
  out: string;
  headless: boolean;
  proxy: string | null;
  concurrency: number;
  axsOnly: boolean;
}

function parseArgs(argv: string[]): Args {
  let start: string | null = null, end: string | null = null, days: string | null = null;
  const args = { out: "events.csv", headless: false, proxy: process.env.AXS_PROXY || null, concurrency: 4, axsOnly: false };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    const next = () => {
      const v = argv[++i];
      if (v === undefined) throw new Error(`missing value for ${a}`);
      return v;
    };
    switch (a) {
      case "--start": start = next(); break;
      case "--end": end = next(); break;
      case "--days": days = next(); break;
      case "--out": args.out = next(); break;
      case "--headless": args.headless = true; break;
      case "--proxy": args.proxy = next(); break;
      case "--concurrency": args.concurrency = Number(next()); break;
      case "--axs-only": args.axsOnly = true; break;
      default: throw new Error(`unknown argument: ${a}`);
    }
  }
  if (end && days) throw new Error("use either --end or --days, not both");
  if (!Number.isInteger(args.concurrency) || args.concurrency < 1) throw new Error("--concurrency must be a whole number >= 1");
  return { range: resolveDateRange({ start, end, days }), ...args };
}

async function main() {
  const a = parseArgs(process.argv.slice(2));
  const { start, end, days, startChangedFrom } = a.range;
  if (startChangedFrom) console.error(`start ${startChangedFrom} is in the past - AXS only lists upcoming events, using today`);
  const last = new Date(end.getTime() - 86400000);
  console.error(`AXS discovery ${formatDay(start)} .. ${formatDay(last)} (${days} day(s), ` +
    `${a.proxy ? "proxy" : "no proxy"}, concurrency ${a.concurrency})`);
  const { rows, failed, stats } = await runCrawl({
    start, end, headless: a.headless, proxy: a.proxy, concurrency: a.concurrency,
  });
  const out = a.axsOnly ? rows.filter((r) => r.axsTicketed || r.axsMarketplaceEvent) : rows;
  await writeFile(a.out, toCsv(out), "utf-8");

  const ticketed = rows.filter((r) => r.axsTicketed).length;
  const marketplace = rows.filter((r) => r.axsMarketplaceEvent).length;
  console.error(`\nWrote ${out.length} events to ${a.out}`);
  console.error(`  ${stats.upcoming} upcoming (${ticketed} AXS-ticketed, ${marketplace} marketplace), ` +
    `${stats.unique} unique ids, ${stats.hits} hits / ${stats.reportedTotal} reported over ${stats.days} day(s)`);
  console.error(`  ${stats.requests} API requests, ${stats.sessions} session(s), ${stats.seconds}s`);
  if (failed.length) {
    console.error(`!! ${failed.length} window(s) incomplete (genuinely unchecked, not just empty): ${failed.join("; ")}`);
    process.exitCode = 2;
  }
}

main().catch((err) => {
  console.error(err instanceof Error ? `Error: ${err.message}` : err);
  process.exit(1);
});
