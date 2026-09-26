// Electron main process. Deliberately thin: all crawler logic (Cloudflare
// bypass, discovery, event fetching) lives in ../BroadwayDirectShowDiscovery.Crawler/src and is imported
// here unchanged - this file only wires it to a UI. See ../BroadwayDirectShowDiscovery.Crawler/README.md
// for how that logic works and what's been measured about it.
"use strict";

const { app, BrowserWindow, ipcMain, dialog, shell } = require("electron");
const path = require("node:path");
const fs = require("node:fs/promises");

// The crawler's dist is ESM ("type": "module" in its package.json) - main.js here
// is CommonJS, so these are loaded via dynamic import() at call time
// instead of a top-level require().
const NODE_DIST = path.join(__dirname, "..", "..", "BroadwayDirectShowDiscovery.Crawler", "dist");

let mainWindow;
let lastCsv = null; // string | null - the most recently produced CSV, held in memory until saved
let crawlInProgress = false; // mutex - manual and scheduled runs both go through this

// Schedule state. Deliberately NOT persisted to disk - it only runs while
// this app instance is open, same as any other in-memory app state. For
// "run even when the app/computer is off" use cron/Task Scheduler with the
// CLI instead (see ../BroadwayDirectShowDiscovery.Crawler/README.md).
let scheduleTimer = null;
let scheduleState = {
  running: false,
  intervalMs: null,
  outputDir: null,
  crawlArgs: null,
  nextRunAt: null,
  lastRun: null, // {at, count, showCount, filePath} | {at, error}
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
    const { runCrawl } = await import(path.join(NODE_DIST, "crawl.js"));
    const { toCsv } = await import(path.join(NODE_DIST, "csv.js"));

    const start = crawlArgs.start.split("-").map(Number);
    const end = crawlArgs.end.split("-").map(Number);
    const daysBack = crawlArgs.allShows ? null : Number(crawlArgs.daysBack);

    // console.error is how every stage in the crawler reports progress - this
    // is the least invasive way to surface that in the UI without touching
    // the already-verified crawler modules: capture the lines as they're
    // printed, forward each to the renderer, then restore the original.
    const originalError = console.error;
    console.error = (...parts) => {
      originalError(...parts);
      sendLog(parts.map(String).join(" "));
    };
    try {
      const { rows, failed } = await runCrawl(start, end, false, daysBack);
      const csv = toCsv(rows);
      const showCount = new Set(rows.map((r) => r.seriesId)).size;
      return { ok: true, rows, csv, count: rows.length, showCount, failedCount: failed.length, failed };
    } finally {
      console.error = originalError;
    }
  } catch (err) {
    return { ok: false, error: err instanceof Error ? err.message : String(err) };
  } finally {
    crawlInProgress = false;
  }
}

ipcMain.handle("start-crawl", async (_event, args) => {
  const result = await performCrawl(args);
  if (!result.ok) return result;
  lastCsv = result.csv;
  // don't ship `rows`/`csv` back over IPC - the renderer only needs the
  // summary, and rows can be thousands of objects.
  const { rows: _rows, csv: _csv, ...summary } = result;
  return summary;
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
      scheduleState.lastRun = {
        at: new Date().toISOString(),
        count: result.count,
        showCount: result.showCount,
        filePath,
      };
      sendLog(`=== Scheduled run wrote ${result.count} performances to ${filePath} ===`);
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
