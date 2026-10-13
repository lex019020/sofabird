# ADR-0001. Ways to control VLC: three levels

Status: accepted (2026-10-12). Basis: [research/live-probe.md](../research/live-probe.md).

## Context

VLC 3.0.x on Windows offers three ways to control it from outside: the Lua HTTP interface (request/response,
JSON), the RC interface (text over TCP, with push events and commands that HTTP lacks:
the list of audio devices `adev`, the current track, `vratio/vcrop/vzoom`) and libvlc (embedding the
player in our own process). VLC does not support Bluetooth at all. The user wants the
component on the PC to be **optional**.

## Decision

The app supports three capability levels, detected automatically on connection:

1. **Level 0 — VLC only (HTTP).** Requires the web interface to be enabled and a password.
   Provides: play/pause/stop/seek/rate, VLC volume, playlist, file browsing and launching,
   fullscreen, chapters (by number), audio/subtitle delays, aspect ratio, equalizer,
   **audio/subtitle/video track selection** (ID = the `Stream N` number from `status.json`),
   cover art `/art`. Does not provide: the currently selected track (tracked optimistically on the client),
   chapter names, audio device, events, launching VLC, Bluetooth.
2. **Level 1 — VLC + our Lua pages** in `%APPDATA%\vlc\lua\http\`
   (`requests/tracks.json`, `requests/setvar.json`, etc.). Adds: the current track,
   chapter names, aspect/crop/deinterlace/stereo-mode lists, arbitrary `vlc.var` operations.
   Important: the user folder **replaces** the system `lua\http`, so the installer also copies
   the stock pages there (`requests/*.json`, `index.html`, etc.), and VLC has to be restarted
   (URLs are registered when the interface loads). After a VLC update the copy of the stock pages
   has to be refreshed. Audio devices are not available at this level (the aout variable `device` in Lua
   neither returns a list nor switches).
3. **Level 2 — a companion on the PC (.NET, tray).** Adds: Bluetooth RFCOMM transport,
   network discovery (mDNS/UDP beacon), push events and the list of audio devices via RC,
   starting/restarting VLC with the right parameters, moving the VLC window to the TV monitor,
   Windows system volume and output device, "don't sleep during a movie",
   PC sleep/shutdown, library thumbnails/metadata.

The app always talks to VLC through a single abstract interface `PlayerBackend`;
implementations: `VlcHttpBackend` (levels 0–1) and `CompanionBackend` (level 2, which itself
uses HTTP+RC+Win32 internally). Unavailable capabilities are hidden in the UI rather than broken.

## Rejected options

- **Companion only (mandatory).** Contradicts the optionality requirement; without the
  companion the app would be useless.
- **RC only instead of HTTP.** RC is single-threaded (one connection), an unversioned text format,
  no file browsing or cover art; HTTP is more robust as the base layer.
- **A libvlc player inside the companion instead of the real VLC.** Full control, but it
  duplicates the player, loses the user's settings/plugins and complicates the companion. Kept
  as a possible future "embedded mode".
