"use strict";

// Sofabird TV on YouTube pages: reports what the player does (the video, position, an ad) and carries out the
// companion's commands through YouTube's own player API. Firefox only: the page's player is reached through
// wrappedJSObject.

const SKIP_AD = ".ytp-skip-ad-button, .ytp-ad-skip-button, .ytp-ad-skip-button-modern";
const REPORT_EVERY_MS = 1000;

const port = browser.runtime.connect({ name: "page" });
let lastKey = "";
let lastSentAt = 0;

function player() {
  return document.getElementById("movie_player");
}

function api() {
  const p = player();
  return p ? p.wrappedJSObject : null;
}

function video() {
  const p = player();
  return p ? p.querySelector("video") : null;
}

function shown(element) {
  return !!element && element.getClientRects().length > 0 && getComputedStyle(element).visibility !== "hidden";
}

function call(name, ...args) {
  const a = api();
  try {
    return a && typeof a[name] === "function" ? a[name](...args) : undefined;
  } catch (e) {
    return undefined; // the player is not ready yet
  }
}

function snapshot() {
  const p = player();
  const v = video();
  if (!p || !v || location.pathname !== "/watch") return { t: "state", watching: false };
  const data = call("getVideoData");
  const index = call("getPlaylistIndex");
  const ad = p.classList.contains("ad-showing");
  const next = document.querySelector(".ytp-next-button");
  return {
    t: "state",
    watching: true,
    videoId: data && data.video_id ? String(data.video_id) : null,
    title: data && data.title ? String(data.title) : null,
    channel: data && data.author ? String(data.author) : null,
    time: Number.isFinite(v.currentTime) ? v.currentTime : 0,
    duration: Number.isFinite(v.duration) ? v.duration : 0,
    playing: !v.paused && !v.ended,
    ended: v.ended,
    ad,
    adSkippable: ad && shown(document.querySelector(SKIP_AD)),
    canNext: shown(next) && next.getAttribute("aria-disabled") !== "true",
    canPrevious: typeof index === "number" && index > 0,
    fullscreen: !!document.fullscreenElement,
  };
}

/** Sends the state when something but the position changed, and the position at most once a second. */
function report(force) {
  const state = snapshot();
  const key = JSON.stringify({ ...state, time: 0 });
  const now = Date.now();
  if (!force && key === lastKey && now - lastSentAt < REPORT_EVERY_MS) return;
  lastKey = key;
  lastSentAt = now;
  port.postMessage(state);
}

function click(selector) {
  const element = document.querySelector(selector);
  if (element) element.click();
}

/** "Video paused. Continue watching?" would stop a video on the TV that nobody touches: answered with "Yes". */
function keepWatching() {
  const v = video();
  if (!v || !v.paused) return;
  for (const dialog of document.querySelectorAll("ytd-popup-container yt-confirm-dialog-renderer")) {
    const confirm = dialog.querySelector("#confirm-button button, #confirm-button");
    if (shown(dialog) && confirm) {
      confirm.click();
      return;
    }
  }
}

port.onMessage.addListener((message) => {
  if (!message || message.t !== "cmd") return;
  const v = video();
  switch (message.action) {
    case "play":
      call("playVideo");
      break;
    case "pause":
      call("pauseVideo");
      break;
    case "toggle":
      call(v && v.paused ? "playVideo" : "pauseVideo");
      break;
    case "seek":
      if (typeof message.time === "number") call("seekTo", Math.max(0, message.time), true);
      break;
    case "next":
      call("nextVideo");
      break;
    case "previous":
      call("previousVideo");
      break;
    case "fullscreen":
      // The TV profile lets a page go fullscreen without a key press (full-screen-api.allow-trusted-requests-only).
      if (!document.fullscreenElement) click(".ytp-fullscreen-button");
      break;
    case "skipAd":
      click(SKIP_AD);
      break;
  }
  setTimeout(() => report(true), 300);
});

// Media events do not bubble, but they reach a capturing listener on the document; "timeupdate" fires 4 times a second.
for (const type of ["play", "pause", "playing", "seeked", "durationchange", "loadedmetadata", "ended", "timeupdate"]) {
  document.addEventListener(type, () => report(type !== "timeupdate"), true);
}
document.addEventListener("yt-navigate-finish", () => report(true));
document.addEventListener("fullscreenchange", () => report(true));
// Ads start and end without a media event of the main video; the skip button appears a few seconds into an ad.
setInterval(() => {
  keepWatching();
  report(false);
}, 2000);
report(true);
