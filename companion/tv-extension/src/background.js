"use strict";

// Sofabird TV: relays between the YouTube page in the TV browser and the Sofabird companion. The native messaging
// host "app.sofabird.tv" is Sofabird.exe, which forwards to the running companion on this PC; nothing leaves the PC.

const HOST = "app.sofabird.tv";
const WATCH_URL = /^https:\/\/www\.youtube\.com\/watch\?/;

let native = null;
let page = null; // the port of the YouTube tab that reported last
let retryMs = 1000;

function connect() {
  const port = browser.runtime.connectNative(HOST);
  native = port;
  port.onMessage.addListener((message) => {
    retryMs = 1000;
    onCommand(message);
  });
  port.onDisconnect.addListener(() => {
    if (native === port) native = null;
    // The companion restarts (an update) or is not installed: try again, less and less often.
    setTimeout(connect, retryMs);
    retryMs = Math.min(retryMs * 2, 30000);
  });
  send({ t: "hello", version: browser.runtime.getManifest().version });
}

function send(message) {
  if (!native) return;
  try {
    native.postMessage(message);
  } catch (e) {
    // disconnected meanwhile: onDisconnect reconnects
  }
}

function onCommand(message) {
  if (!message || message.t !== "cmd") return;
  if (message.action === "open") {
    open(message.url);
  } else if (page) {
    page.postMessage(message);
  }
}

/** Loads another video into the TV tab (no new window, no reload of the browser). */
async function open(url) {
  if (typeof url !== "string" || !WATCH_URL.test(url)) return;
  let tabId = page && page.sender && page.sender.tab ? page.sender.tab.id : undefined;
  if (tabId === undefined) {
    const [tab] = await browser.tabs.query({ active: true, currentWindow: true });
    tabId = tab ? tab.id : undefined;
  }
  if (tabId !== undefined) await browser.tabs.update(tabId, { url });
}

browser.runtime.onConnect.addListener((port) => {
  if (port.name !== "page") return;
  page = port;
  port.onMessage.addListener((message) => {
    page = port;
    send(message);
  });
  port.onDisconnect.addListener(() => {
    if (page !== port) return;
    page = null;
    send({ t: "state", watching: false });
  });
});

connect();
