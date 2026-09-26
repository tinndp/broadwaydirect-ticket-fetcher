"use strict";

const { contextBridge, ipcRenderer } = require("electron");

contextBridge.exposeInMainWorld("crawler", {
  startCrawl: (args) => ipcRenderer.invoke("start-crawl", args),
  saveCsv: () => ipcRenderer.invoke("save-csv"),
  showInFolder: (filePath) => ipcRenderer.invoke("show-in-folder", filePath),
  pickFolder: () => ipcRenderer.invoke("pick-folder"),
  getSettings: () => ipcRenderer.invoke("settings-get"),
  setOutputDir: (dir) => ipcRenderer.invoke("settings-set-output-dir", dir),

  scheduleStart: (opts) => ipcRenderer.invoke("schedule-start", opts),
  scheduleStop: () => ipcRenderer.invoke("schedule-stop"),
  scheduleGetStatus: () => ipcRenderer.invoke("schedule-get-status"),
  onScheduleStatus: (callback) => {
    const listener = (_event, status) => callback(status);
    ipcRenderer.on("schedule-status", listener);
    return () => ipcRenderer.removeListener("schedule-status", listener);
  },

  // Database sync (S4K_AXS_SourceEvents) is fully automatic and
  // silent - main.js reads the connection string from App.config and
  // syncs after every crawl. No UI surface for it, so no IPC here either.

  onLog: (callback) => {
    const listener = (_event, line) => callback(line);
    ipcRenderer.on("crawl-log", listener);
    return () => ipcRenderer.removeListener("crawl-log", listener);
  },
});
