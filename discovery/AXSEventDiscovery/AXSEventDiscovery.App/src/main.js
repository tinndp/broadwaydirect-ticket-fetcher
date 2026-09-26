// Electron main process. Deliberately thin: all crawler logic (Cloudflare
// bypass, discovery, event fetching) lives in
// ../AXSEventDiscovery.Crawler/src and is imported here unchanged - this file
// only wires it to a UI and to the SQL Server sync (mssqlSync.js). Copied from
// BroadwayDirectShowDiscovery.App; see ../README.md for what differs.
"use strict";

const { app, BrowserWindow, ipcMain, dialog, shell } = require("electron");
const path = require("node:path");
const fs = require("node:fs/promises");
const { pathToFileURL } = require("node:url");
const { syncSourceEvents } = require("./mssqlSync.js");

/** The crawler's modules (ESM) - loaded on demand, see CRAWLER_DIST below. */
async function crawlerModule(name) {
  // import() needs a file:// URL for an absolute path - on Windows a raw
  // "C:\\..." path is read as a URL with scheme "c:" (ERR_UNSUPPORTED_ESM_URL_SCHEME).
  return import(pathToFileURL(path.join(CRAWLER_DIST, name)).href);
}

// The crawler package builds to ESM ("type": "module") - main.js here is
// CommonJS, so its modules are loaded via dynamic import() at call time
// instead of a top-level require().
const CRAWLER_DIST = path.join(__dirname, "..", "..", "AXSEventDiscovery.Crawler", "dist");

// Same convention as the other Rowing projects: one App.config next to the app,
// <appSettings><add key="ConnectionString" .../><add key="Proxy" .../></appSettings>.
// Read automatically on every crawl - never typed into the UI.
const APP_CONFIG_PATH = path.join(__dirname, "..", "App.config");

// UI preferences that survive an app restart (currently the schedule's output
// folder). Lives in Electron's per-user data dir, independent of App.config.
const SETTINGS_PATH = path.join(app.getPath("userData"), "settings.json");

let mainWindow;
let lastCsv = null; // string | null - the most recently produced CSV, held in memory until saved
let crawlInProgress = false; // mutex - manual and scheduled runs both go through this
let scheduleTimer = null;
let scheduleState = {
  running: false,
  intervalMs: null,
  outputDir: null,
  crawlArgs: null,
  nextRunAt: null,
  lastRun: null, // {at, count, venueCount, filePath} | {at, error}
};

function createWindow() {
  mainWindow = new BrowserWindow({
    width: 900,
    height: 820,
    webPreferences: {
      preload: path.join(__dirname, "preload.js"),
      contextIsolation: true,
      nodeIntegration: false,
    },
  });
  mainWindow.loadFile(path.join(__dirname, "..", "renderer", "index.html"));
}

app.whenReady().then(createWindow);

app.on("window-all-closed", () => {
  if (process.platform !== "darwin") app.quit();
});

app.on("activate", () => {
  if (BrowserWindow.getAllWindows().length === 0) createWindow();
});

function sendLog(line) {
  if (mainWindow && !mainWindow.isDestroyed()) {
    mainWindow.webContents.send("crawl-log", line);
  }
}

function sendScheduleStatus() {
  if (mainWindow && !mainWindow.isDestroyed()) {
    mainWindow.webContents.send("schedule-status", scheduleState);
  }
}

/** Shared by both the one-off "Start Crawl" button and the scheduler -
 * same mutex, same log forwarding, same crawler call. */
async function performCrawl(crawlArgs) {
  if (crawlInProgress) {
    return { ok: false, error: "a crawl is already running" };
  }
  crawlInProgress = true;
  try {
    const { runCrawl } = await crawlerModule("crawl.js");
    const { toCsv } = await crawlerModule("csv.js");
    const { resolveDateRange, formatDay } = await crawlerModule("dates.js");

    // Start date + End date (inclusive) or Days; empty or past start = today. Same rules as the CLI (dates.ts).
    const { start, end, days, startChangedFrom } = resolveDateRange(crawlArgs);
    if (startChangedFrom) {
      sendLog(`Start date ${startChangedFrom} is in the past - AXS only lists upcoming events, using ${formatDay(start)} instead`);
    }
    const { value: proxy } = await readAppSetting("Proxy");

    // console.error is how the Crawler package reports progress - forward each
    // line to the renderer without touching the crawler modules.
    const originalError = console.error;
    console.error = (...parts) => {
      originalError(...parts);
      sendLog(parts.map(String).join(" "));
    };
    try {
      const last = new Date(end.getTime() - 86400000);
      sendLog(`AXS discovery ${formatDay(start)} .. ${formatDay(last)} (${days} day(s), ${proxy ? "proxy from App.config" : "no proxy"})`);
      const { rows: all, failed, stats } = await runCrawl({ start, end, proxy: proxy || null, log: (l) => console.error(l) });
      // Events AXS only lists (neither AXS-ticketed nor on the AXS marketplace) have no
      // inventory for the AXS crawler - dropped unless "include listed-only" is ticked.
      const rows = crawlArgs.includeListedOnly ? all : all.filter((r) => r.axsTicketed || r.axsMarketplaceEvent);
      sendLog(`${stats.upcoming} upcoming events, ${rows.length} kept, ${stats.requests} API requests, ${stats.seconds}s`);
      const csv = toCsv(rows);
      const venueCount = new Set(rows.map((r) => r.venueId)).size;
      return { ok: true, rows, csv, count: rows.length, venueCount, failedCount: failed.length, failed };
    } finally {
      console.error = originalError;
    }
  } catch (err) {
    return { ok: false, error: err instanceof Error ? err.message : String(err) };
  } finally {
    crawlInProgress = false;
  }
}

// ---- SQL Server sync (S4K_AXS_SourceEvents) ----

const XML_ENTITIES = { amp: "&", lt: "<", gt: ">", quot: '"', apos: "'" };
function decodeXmlEntities(s) {
  return s.replace(/&(amp|lt|gt|quot|apos);/g, (_, name) => XML_ENTITIES[name]);
}

/** Reads one <add key="..." value="..."/> from App.config next to the app -
 * the same place the .NET Rowing projects keep their settings. A regex is
 * enough for App.config's fixed shape (see the file itself). */
async function readAppSetting(key) {
  let xml;
  try {
    xml = await fs.readFile(APP_CONFIG_PATH, "utf-8");
  } catch {
    return { value: "", error: "App.config not found" };
  }
  const addTagMatch = xml.match(new RegExp(`<add\\b[^>]*\\bkey="${key}"[^>]*\\/?>`, "i"));
  if (!addTagMatch) return { value: "", error: `no <add key="${key}" .../> found` };
  const valueMatch = addTagMatch[0].match(/\bvalue="([^"]*)"/i);
  return { value: valueMatch ? decodeXmlEntities(valueMatch[1]) : "" };
}

async function loadConnectionStringFromAppConfig() {
  const { value, error } = await readAppSetting("ConnectionString");
  if (error) return { connectionString: "", error };
  if (!value) return { connectionString: "", error: 'ConnectionString is empty - fill in "value=" in App.config' };
  return { connectionString: value };
}

/** Auto-sync per the product decision: every successful crawl (manual or
 * scheduled) syncs to SQL automatically when App.config has a connection
 * string - there is no separate "Sync to DB" button, and nothing to
 * configure in the UI. Failures are logged but never fail the crawl
 * itself (same best-effort spirit as the CSV write path). */
async function syncRowsToDbIfConfigured(rows) {
  const { connectionString, error } = await loadConnectionStringFromAppConfig();
  if (!connectionString) {
    sendLog(`(SQL sync skipped: ${error})`);
    return null;
  }
  try {
    const result = await syncSourceEvents(connectionString, rows, sendLog);
    return result;
  } catch (err) {
    const message = err instanceof Error ? err.message : String(err);
    sendLog(`!! SQL sync failed: ${message}`);
    return { ok: false, error: message };
  }
}

ipcMain.handle("start-crawl", async (_event, args) => {
  const result = await performCrawl(args);
  if (!result.ok) return result;
  lastCsv = result.csv;
  const dbResult = await syncRowsToDbIfConfigured(result.rows);
  // don't ship `rows`/`csv` back over IPC - the renderer only needs the
  // summary, and rows can be thousands of objects.
  const { rows: _rows, csv: _csv, ...summary } = result;
  return { ...summary, dbResult };
});

ipcMain.handle("save-csv", async () => {
  if (lastCsv === null) return { ok: false, error: "no crawl result to save yet" };
  const { canceled, filePath } = await dialog.showSaveDialog(mainWindow, {
    defaultPath: "events.csv",
    filters: [{ name: "CSV", extensions: ["csv"] }],
  });
  if (canceled || !filePath) return { ok: false, canceled: true };
  await fs.writeFile(filePath, lastCsv, "utf-8");
  return { ok: true, filePath };
});

ipcMain.handle("show-in-folder", async (_event, filePath) => {
  shell.showItemInFolder(filePath);
});

async function readSettings() {
  try {
    return JSON.parse(await fs.readFile(SETTINGS_PATH, "utf-8"));
  } catch {
    return {}; // missing or corrupt - fall back to defaults
  }
}

ipcMain.handle("settings-get", async () => readSettings());

ipcMain.handle("settings-set-output-dir", async (_event, outputDir) => {
  try {
    const settings = await readSettings();
    settings.outputDir = String(outputDir || "");
    await fs.mkdir(path.dirname(SETTINGS_PATH), { recursive: true });
    await fs.writeFile(SETTINGS_PATH, JSON.stringify(settings, null, 2), "utf-8");
    return { ok: true };
  } catch (err) {
    return { ok: false, error: err instanceof Error ? err.message : String(err) };
  }
});

ipcMain.handle("pick-folder", async () => {
  const { canceled, filePaths } = await dialog.showOpenDialog(mainWindow, {
    properties: ["openDirectory", "createDirectory"],
  });
  if (canceled || filePaths.length === 0) return { ok: false, canceled: true };
  return { ok: true, dir: filePaths[0] };
});

function timestampForFilename(d) {
  return d.toISOString().replace(/:/g, "-").replace(/\..+/, "");
}

/** Runs one scheduled crawl, auto-saves it (no dialog - this can fire
 * unattended), then arranges the next one `intervalMs` after THIS run
 * finishes (not a fixed clock tick) so a slow run can never overlap the
 * next one. */
async function runScheduledCrawlAndReschedule() {
  if (!scheduleState.running) return;

  sendLog(`\n=== Scheduled run starting @ ${new Date().toLocaleString()} ===`);
  const result = await performCrawl(scheduleState.crawlArgs);

  if (result.ok) {
    const filePath = path.join(scheduleState.outputDir, `events-${timestampForFilename(new Date())}.csv`);
    try {
      await fs.writeFile(filePath, result.csv, "utf-8");
      const dbResult = await syncRowsToDbIfConfigured(result.rows);
      scheduleState.lastRun = {
        at: new Date().toISOString(),
        count: result.count,
        venueCount: result.venueCount,
        filePath,
        dbResult,
      };
      sendLog(`=== Scheduled run wrote ${result.count} events to ${filePath} ===`);
    } catch (err) {
      scheduleState.lastRun = { at: new Date().toISOString(), error: String(err) };
      sendLog(`!! scheduled run: failed to write output file: ${err}`);
    }
  } else {
    scheduleState.lastRun = { at: new Date().toISOString(), error: result.error };
    sendLog(`!! scheduled run failed: ${result.error}`);
  }

  if (!scheduleState.running) {
    sendScheduleStatus();
    return; // stopped while this run was in flight
  }
  scheduleState.nextRunAt = new Date(Date.now() + scheduleState.intervalMs).toISOString();
  sendScheduleStatus();
  scheduleTimer = setTimeout(runScheduledCrawlAndReschedule, scheduleState.intervalMs);
}

ipcMain.handle("schedule-start", async (_event, { intervalMinutes, outputDir, crawlArgs }) => {
  if (scheduleState.running) return { ok: false, error: "schedule already running" };
  try {
    const { resolveDateRange } = await crawlerModule("dates.js");
    resolveDateRange(crawlArgs); // refuse an invalid range now, not on every run
  } catch (err) {
    return { ok: false, error: err.message };
  }
  try {
    await fs.access(outputDir);
  } catch {
    return { ok: false, error: `output folder not accessible: ${outputDir}` };
  }

  scheduleState = {
    running: true,
    intervalMs: Math.max(1, Number(intervalMinutes)) * 60_000,
    outputDir,
    crawlArgs,
    nextRunAt: new Date().toISOString(), // first run fires immediately
    lastRun: null,
  };
  sendScheduleStatus();
  // Fire the first run right away; it reschedules itself afterward.
  runScheduledCrawlAndReschedule();
  return { ok: true };
});

ipcMain.handle("schedule-stop", async () => {
  scheduleState.running = false;
  scheduleState.nextRunAt = null;
  if (scheduleTimer) {
    clearTimeout(scheduleTimer);
    scheduleTimer = null;
  }
  sendScheduleStatus();
  return { ok: true };
});

ipcMain.handle("schedule-get-status", async () => scheduleState);
