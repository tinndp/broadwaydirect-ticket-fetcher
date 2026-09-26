/**
 * Date range for a discovery run, shared by the CLI and the Electron app.
 *
 * Input: start day, and EITHER an end day (inclusive - the last day to crawl)
 * OR a number of days. Output: [start, end) as UTC midnights for crawlRun.
 *  - empty or past start -> today (AXS only lists upcoming events);
 *  - neither end nor days -> DEFAULT_DAYS (180 = +6 months, like Broadway);
 *  - at most MAX_DAYS; end before start is an error.
 */
export const DEFAULT_DAYS = 180;
export const MAX_DAYS = 730;
const DAY_MS = 86400000;

export interface DateRange {
  start: Date;
  end: Date; // exclusive
  days: number;
  startChangedFrom: string | null; // the past start date that was replaced by today
}

function parseDay(s: string, what: string): Date {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(s)) throw new Error(`${what} must be YYYY-MM-DD, got "${s}"`);
  const d = new Date(`${s}T00:00:00Z`);
  if (Number.isNaN(d.getTime()) || d.toISOString().slice(0, 10) !== s) throw new Error(`${what} is not a real date: "${s}"`);
  return d;
}

export function formatDay(d: Date): string {
  return d.toISOString().slice(0, 10);
}

export function resolveDateRange(opts: { start?: string | null; end?: string | null; days?: string | number | null }): DateRange {
  const today = new Date(formatDay(new Date()) + "T00:00:00Z");
  let start = opts.start ? parseDay(opts.start, "start date") : today;
  let startChangedFrom: string | null = null;
  if (start < today) {
    startChangedFrom = formatDay(start);
    start = today;
  }

  let days: number;
  if (opts.end) {
    const last = parseDay(opts.end, "end date");
    if (last < start) throw new Error(`end date ${opts.end} is before the start date ${formatDay(start)}`);
    days = Math.round((last.getTime() - start.getTime()) / DAY_MS) + 1;
  } else if (opts.days !== undefined && opts.days !== null && opts.days !== "") {
    days = Number(opts.days);
    if (!Number.isInteger(days) || days < 1) throw new Error(`days must be a whole number >= 1, got "${opts.days}"`);
  } else {
    days = DEFAULT_DAYS;
  }
  if (days > MAX_DAYS) throw new Error(`the range is ${days} days - at most ${MAX_DAYS}`);
  return { start, end: new Date(start.getTime() + days * DAY_MS), days, startChangedFrom };
}
