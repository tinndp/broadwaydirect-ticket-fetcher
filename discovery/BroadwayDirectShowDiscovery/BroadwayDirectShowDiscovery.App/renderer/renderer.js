"use strict";

const form = document.getElementById("form");
const startInput = document.getElementById("start");
const endInput = document.getElementById("end");
const daysBackInput = document.getElementById("daysBack");
const allShowsInput = document.getElementById("allShows");
const startBtn = document.getElementById("startBtn");
const statusSection = document.getElementById("status");
const spinner = document.getElementById("spinner");
const statusText = document.getElementById("statusText");
const logEl = document.getElementById("log");
const resultSection = document.getElementById("result");
const resultText = document.getElementById("resultText");
const saveBtn = document.getElementById("saveBtn");
const savedPathEl = document.getElementById("savedPath");

const intervalValueInput = document.getElementById("intervalValue");
const intervalUnitInput = document.getElementById("intervalUnit");
const outputDirInput = document.getElementById("outputDir");
const pickFolderBtn = document.getElementById("pickFolderBtn");
const scheduleStartBtn = document.getElementById("scheduleStartBtn");
const scheduleStopBtn = document.getElementById("scheduleStopBtn");
const intervalWarning = document.getElementById("intervalWarning");
const scheduleStatusEl = document.getElementById("scheduleStatus");

function monthValue(date) {
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, "0")}`;
}

const today = new Date();
startInput.value = monthValue(today);
endInput.value = monthValue(new Date(today.getFullYear(), today.getMonth() + 6, 1));

allShowsInput.addEventListener("change", () => {
  daysBackInput.disabled = allShowsInput.checked;
});

// Log streaming is shared by manual runs and scheduled runs - registered
// once, appended to always; only the manual "Start Crawl" click clears it.
window.crawler.onLog((line) => {
  logEl.textContent += line + "\n";
  logEl.scrollTop = logEl.scrollHeight;
});

function currentCrawlArgs() {
  return {
    start: startInput.value,
    end: endInput.value,
    daysBack: daysBackInput.value,
    allShows: allShowsInput.checked,
  };
}

form.addEventListener("submit", async (e) => {
  e.preventDefault();

  logEl.textContent = "";
  resultSection.classList.add("hidden");
  savedPathEl.textContent = "";
  statusSection.classList.remove("hidden");
  spinner.classList.remove("hidden");
  statusText.textContent = "Running (a Chromium window will open briefly to pass Cloudflare)...";
  startBtn.disabled = true;

  const result = await window.crawler.startCrawl(currentCrawlArgs());

  spinner.classList.add("hidden");
  startBtn.disabled = false;

  if (!result.ok) {
    statusText.textContent = `Failed: ${result.error}`;
    return;
  }

  statusText.textContent = "Done.";
  resultSection.classList.remove("hidden");
  resultText.textContent = `${result.count} performances across ${result.showCount} shows.` +
    (result.failedCount ? ` (${result.failedCount} shows could not be checked.)` : "");
});

saveBtn.addEventListener("click", async () => {
  const res = await window.crawler.saveCsv();
  if (res.canceled) return;
  if (!res.ok) {
    savedPathEl.textContent = `Save failed: ${res.error}`;
    return;
  }
  savedPathEl.textContent = `Saved to ${res.filePath}`;
  window.crawler.showInFolder(res.filePath);
});

// ---- Schedule ----

pickFolderBtn.addEventListener("click", async () => {
  const res = await window.crawler.pickFolder();
  if (!res.ok) return;
  outputDirInput.value = res.dir;
});

function intervalMinutes() {
  const v = Number(intervalValueInput.value) || 0;
  return intervalUnitInput.value === "hours" ? v * 60 : v;
}

function updateIntervalWarning() {
  intervalWarning.classList.toggle("hidden", intervalMinutes() >= 30);
}
intervalValueInput.addEventListener("input", updateIntervalWarning);
intervalUnitInput.addEventListener("change", updateIntervalWarning);
updateIntervalWarning();

scheduleStartBtn.addEventListener("click", async () => {
  if (!outputDirInput.value) {
    scheduleStatusEl.textContent = "Choose an output folder first.";
    return;
  }
  const res = await window.crawler.scheduleStart({
    intervalMinutes: intervalMinutes(),
    outputDir: outputDirInput.value,
    crawlArgs: currentCrawlArgs(),
  });
  if (!res.ok) {
    scheduleStatusEl.textContent = `Could not start schedule: ${res.error}`;
  }
});

scheduleStopBtn.addEventListener("click", async () => {
  await window.crawler.scheduleStop();
});

function renderScheduleStatus(status) {
  const running = status && status.running;
  scheduleStartBtn.classList.toggle("hidden", running);
  scheduleStopBtn.classList.toggle("hidden", !running);
  scheduleStopBtn.disabled = !running;
  [intervalValueInput, intervalUnitInput, pickFolderBtn].forEach((el) => (el.disabled = running));

  const parts = [];
  if (running) {
    parts.push(`Schedule running - saving to ${status.outputDir}`);
    if (status.nextRunAt) {
      const secs = Math.max(0, Math.round((new Date(status.nextRunAt) - Date.now()) / 1000));
      const mins = Math.floor(secs / 60);
      parts.push(`next run in ${mins}m ${secs % 60}s`);
    }
  } else {
    parts.push("Schedule stopped.");
  }
  if (status && status.lastRun) {
    if (status.lastRun.error) {
      parts.push(`Last run failed: ${status.lastRun.error}`);
    } else {
      parts.push(
        `Last run: ${status.lastRun.count} performances at ` +
          `${new Date(status.lastRun.at).toLocaleTimeString()}`,
      );
    }
  }
  scheduleStatusEl.textContent = parts.join(" · ");
}

let latestScheduleStatus = null;
window.crawler.onScheduleStatus((status) => {
  latestScheduleStatus = status;
  renderScheduleStatus(status);
});

// Live-updating "next run in Xm Ys" without waiting for a new IPC push.
setInterval(() => {
  if (latestScheduleStatus && latestScheduleStatus.running) renderScheduleStatus(latestScheduleStatus);
}, 1000);

window.crawler.scheduleGetStatus().then((status) => {
  latestScheduleStatus = status;
  renderScheduleStatus(status);
});
