/**
 * Entry point: discover every Tixtrack-ticketed Broadway Direct show and
 * crawl every performance in a month range, writing one CSV row per
 * performance (event_url, event_name, event_id, event_datetime,
 * series_id, show_slug).
 *
 * Usage:
 *   npm run build && npm run cli -- --start 2026-09 --end 2027-02 --out events.csv
 */
import { writeFile } from "node:fs/promises";
import { DEFAULT_DAYS_BACK } from "./sitemap.js";
import type { YearMonth } from "./events.js";
import { runCrawl } from "./crawl.js";
import { toCsv } from "./csv.js";

function parseYm(s: string): YearMonth {
  const [y, m] = s.split("-").map(Number);
  return [y, m];
}

function defaultEndYm(): string {
  const today = new Date();
  let y = today.getFullYear();
  let m = today.getMonth() + 1 + 6; // JS months are 0-indexed
  if (m > 12) {
    m -= 12;
    y += 1;
  }
  return `${String(y).padStart(4, "0")}-${String(m).padStart(2, "0")}`;
}

interface Args {
  start: string;
  end: string;
  out: string;
  headless: boolean;
  daysBack: number | null;
}

function parseArgs(argv: string[]): Args {
  const today = new Date();
  const defaultStart = `${today.getFullYear()}-${String(today.getMonth() + 1).padStart(2, "0")}`;

  const args: Args = {
    start: defaultStart,
    end: defaultEndYm(),
    out: "events.csv",
    headless: false,
    daysBack: DEFAULT_DAYS_BACK,
  };
  let allShows = false;

  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    const next = () => argv[++i];
    switch (a) {
      case "--start":
        args.start = next();
        break;
      case "--end":
        args.end = next();
        break;
      case "--out":
        args.out = next();
        break;
      case "--headless":
        args.headless = true;
        break;
      case "--days-back":
        args.daysBack = Number(next());
        break;
      case "--all-shows":
        allShows = true;
        break;
      default:
        throw new Error(`unknown argument: ${a}`);
    }
  }
  if (allShows) args.daysBack = null;
  return args;
}

async function main() {
  const args = parseArgs(process.argv.slice(2));
  const { rows, failed } = await runCrawl(
    parseYm(args.start), parseYm(args.end), args.headless, args.daysBack);

  await writeFile(args.out, toCsv(rows), "utf-8");

  const shows = new Set(rows.map((r) => r.seriesId));
  console.error(`\nWrote ${rows.length} performances across ${shows.size} shows to ${args.out}`);
  if (failed.length) {
    console.error(
      `!! ${failed.length} series had every month request fail (network hiccup, not just 0 ` +
        `performances - genuinely unchecked): ${failed.join(", ")}`,
    );
  }
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
