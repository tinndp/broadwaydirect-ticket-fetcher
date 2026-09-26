"use strict";

const { contextBridge, ipcRenderer } = require("electron");

contextBridge.exposeInMainWorld("crawler", {
  startCrawl: (args) => ipcRenderer.invoke("start-crawl", args),
  saveCsv: () => ipcRenderer.invoke("save-csv"),
  showInFolder: (filePath) => ipcRenderer.invoke("show-in-folder", filePath),
  pickFolder: () => ipcRenderer.invoke("pick-folder"),

  scheduleStart: (opts) => ipcRenderer.invoke("schedule-start", opts),
  scheduleStop: () => ipcRenderer.invoke("schedule-stop"),
  scheduleGetStatus: () => ipcRenderer.invoke("schedule-get-status"),
  onScheduleStatus: (callback) => {
    const listener = (_event, status) => callback(status);
    ipcRenderer.on("schedule-status", listener);
    return () => ipcRenderer.removeListener("schedule-status", listener);
  },

  onLog: (callback) => {
    const listener = (_event, line) => callback(line);
    ipcRenderer.on("crawl-log", listener);
    return () => ipcRenderer.removeListener("crawl-log", listener);
  },
});
