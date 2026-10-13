# vlc-remote — project plan

Date: 2026-10-13. Status: M1 (direct VLC HTTP), M2 (companion over LAN) and M3 (Bluetooth RFCOMM fallback) are
implemented, along with automatic pairing, companion-side library browsing, localization and choosing the monitor
for the VLC window. In progress: first-run permissions and setup. Next: watch history and "Continue watching".

## 0. Essence

An Android remote for VLC running on a Windows laptop connected to a TV.
There is one scenario: sit on the couch, pick a movie from `D:\shared`, start it on the TV, choose the
Russian audio track and subtitles, adjust the volume and seek, without getting up or touching the laptop.

Three levels, from simple to full (details in [ADR-0001](adr/0001-control-paths.md)):

| Level | What the PC needs | What the remote can do |
|---|---|---|
| 0 "VLC only" | VLC web interface enabled + password | transport, volume, chapters, rate, delays, **track selection**, file browsing and launching, playlist, cover art |
| 1 "VLC + Lua pages" | a copy of the stock `lua\http` folder + our pages in `%APPDATA%\vlc\lua\http\`, VLC restart | + current track, chapter names, aspect/crop/deinterlace |
| 2 "companion" | a .NET tray application | + Bluetooth, auto-discovery, push events, audio device, launching VLC, window on the TV, "keep awake", PC sleep, posters/metadata |

The main difference from existing remotes (see [prior-art](research/prior-art.md)): not cycling through
tracks blindly but a list with languages; a connection that does not "drop"; setup that the companion
does for the user; and Bluetooth as a fallback when Wi-Fi lets you down.

## 1. Goals and non-goals

**v1 goals**
- Control the real VLC 3.0.x on Windows 11 (no embedded player of our own).
- Work without the companion (level 0/1) and with it (level 2); features are enabled based on what is available.
- Two transports: LAN (HTTP to VLC or WebSocket to the companion) and Bluetooth RFCOMM to the companion.
- An "eyes-free" remote: large buttons, haptics, hardware volume keys.
- A library with posters and "Continue watching".

**v1 non-goals**
- iOS, Wear OS, Android TV, widgets (left for later; the protocol and model live in pure Kotlin modules).
- Downloading subtitles from the internet, transcoding, streaming video to the phone.
- VLC 4.x support (we prepare for it: branching on `apiversion`, not crashing on new fields).
- Access from outside the home network, TLS (a field is reserved in the protocol for future pinning).
- Controlling the TV itself (postponed, see § 6 "Postponed").

## 2. Scenarios ("from the couch")

1. Open the app → it finds the PC by itself and shows "what is playing" (or "VLC is not running — start it?").
2. Library → poster → "Watch" → VLC starts on the TV monitor in fullscreen.
3. "Audio and subtitles" sheet → pick "Russian (Dub Mosfilm)" and turn subtitles off.
4. The phone's volume keys change VLC's volume; a long press seeks ±10 s; chapters on the seek bar.
5. Wi-Fi drops → the app silently switches to Bluetooth (if the companion is installed).
6. Sleep timer: after 40 minutes, pause and turn off the monitor.
7. The movie is interrupted or finished → "Continue watching" remembers the position for next time.

## 3. What the research showed (in brief)

Full reports in `docs/research/`:
[live-probe](research/live-probe.md) · [vlc-protocols](research/vlc-protocols.md) ·
[prior-art](research/prior-art.md) · [transport-and-companion](research/transport-and-companion.md) ·
[mobile-stack-and-ux](research/mobile-stack-and-ux.md) · [tv-control](research/tv-control.md).

- **The VLC HTTP interface** is the only stable channel; `status.json?command=…` returns a
  fresh status. Verified on VLC 3.0.23: track ID = the `Stream N` number in `information.category`;
  latency ~20 ms; keep-alive works; command errors are not reported; `fullscreen` is sometimes a `bool`, sometimes `0`;
  there is no `information` in `stopped`; volume is 0..512 (256 = 100 %), the percent form did not work.
- **Our own Lua page** in the user folder is picked up by VLC without administrator rights
  and returns the current track, chapter names, aspect/crop lists; audio devices are not available through Lua.
  The user folder **replaces** the system one, so the stock pages are copied together with ours,
  and VLC is restarted.
- **VLC 4** is still `4.0.0-dev`, the stable one is 3.0.23 (January 2026). In 4.x `audio_track`
  becomes a toggle, `playlist.json` a flat array, and in RC `atrack N` is an index, not an ID.
- **Useful details**: VLC can be closed over HTTP via `in_play&input=vlc://quit`; RC refuses
  most commands while paused ("Press pause to continue") — seek/volume/tracks while paused only
  over HTTP; HTTP keep-alive works only if the client does not send a `Connection` header (OkHttp
  adds `Keep-Alive` by itself — remove it with a network interceptor); Lua errors arrive as `200` with
  an "Error loading" HTML body.
- **The RC interface** (TCP 4212, no authentication) provides `atrack *`, `adev` with the list of Windows devices,
  `status change:` push events; one client at a time — only through the companion on `127.0.0.1`.
- **VLC does not announce itself** (the mDNS patch was rejected); without the companion — a `/24` scan on 8080
  (a `401` response with the realm `VLC stream`) + manual entry + `pc-name.local`.
- **A Bluetooth RFCOMM server** on Windows from a regular .NET application works without a manifest or
  administrator rights (verified on this PC); on Android — `createRfcommSocketToServiceRecord`
  + `BLUETOOTH_CONNECT`, pairing is handled by the system itself. Bluetooth PAN is a no-code fallback.
- **mDNS on Windows** — the built-in responder via `DnsServiceRegister` (verified); the libraries are dead.
- **Firewall** — adding any rule requires UAC; plan: a one-time helper with UAC.
- **Mobile stack** — Kotlin + Jetpack Compose (as in the user's existing project),
  Media3 for the notification and "remote volume", a foreground service of type `connectedDevice`,
  targetSdk 36 (37 introduces the runtime permission `ACCESS_LOCAL_NETWORK`).
- **The main complaints about competitors**: dropped connections, painful password setup, ads,
  overly sensitive seeking, choosing tracks "blindly".
- **TV control** (Samsung UE55H6200, 2014, Orsay): no HDMI-CEC on the laptop's GTX 1050 output and no DDC/CI;
  volume is possible via UPnP RenderingControl, power-off via the encrypted H-series protocol, power-on only with a
  Pulse-Eight CEC adapter ([tv-control](research/tv-control.md)).

## 4. Architecture

### 4.1. Components

```
┌─────────────────────────────┐          ┌──────────────────────────────────────────┐
│ Android app                 │          │ Windows 11 (laptop at the TV)            │
│  UI (Compose)               │          │                                          │
│  PlayerController           │  LAN     │  VLC 3.0.x ── http intf :8080 (Lua)      │
│   ├ VlcHttpBackend ─────────┼──────────┼─▶  + requests/tracks.json, setvar.json   │
│   │   (levels 0/1)          │          │    + rc intf 127.0.0.1:4212              │
│   └ CompanionBackend        │          │            ▲            ▲                │
│       ├ WsTransport ────────┼─ ws ─────┼─▶ Companion (.NET 9, tray)               │
│       └ BtTransport ────────┼─ RFCOMM ─┼─▶   Kestrel WS :47555 · RFCOMM · mDNS    │
│  Discovery: NSD + UDP + scan│          │     VlcManager (process, vlcrc, RC bridge)│
│  MediaSessionService (FGS)  │          │     Win32: windows/monitors/power/sound   │
│  Settings (DataStore)       │          │     Library (browse, names, TMDB, thumbs) │
└─────────────────────────────┘          └──────────────────────────────────────────┘
```

- **The Android app** is the only mandatory component.
- **Lua pages** — our `requests/tracks.json`, `requests/setvar.json` plus a copy of VLC's stock
  pages; installed by the companion or by the `companion/lua/install.ps1` script, they also work without the companion.
- **The companion** — a single exe in the tray, without administrator rights (except for a one-time helper
  for the firewall), autostart via HKCU\Run, quiet updates via Velopack.

### 4.2. Capability levels and `PlayerBackend`

The `PlayerBackend` interface (pure Kotlin, `:core:protocol`):

```kotlin
interface PlayerBackend {
    val capabilities: StateFlow<Capabilities>      // what the current connection can do
    val state: StateFlow<PlayerState>              // the single state model
    suspend fun send(cmd: PlayerCommand): Result<Unit>
    suspend fun browse(uri: String): Result<List<Entry>>
    suspend fun playlist(): Result<List<PlaylistItem>>
}
```

`Capabilities` is a set of flags: `currentTrack`, `chapterNames`, `audioDevices`, `pushEvents`,
`launchVlc`, `windowControl`, `power`, `systemVolume`, `metadata`, `bluetooth`. The UI shows
elements only when the flag is present. After connecting, `VlcHttpBackend` tries
`GET /requests/tracks.json`: `200` → level 1, `404` → level 0.

### 4.3. Transports and discovery ([ADR-0002](adr/0002-transports-and-discovery.md))

Order at startup: the saved PC over LAN (WS to the companion → otherwise HTTP to VLC) →
mDNS `_vlcremote._tcp` + UDP beacon (companion) → with no network and a paired PC —
RFCOMM, switching back to Wi-Fi automatically when it returns → otherwise the "Connection" screen with a `/24` scan,
manual entry and QR. Reconnecting is silent, with exponential backoff, commands are buffered for 5 s, the connection
status is always in the header.

### 4.4. Phone ↔ companion protocol

NDJSON (one JSON line per message, UTF-8, `\n`), identical over WebSocket and RFCOMM.
Messages have `t` (type), `id` (for replies), `v` (protocol version, in `hello`). The source of truth is
[protocol/README.md](../protocol/README.md).

| Direction | Type | Purpose |
|---|---|---|
| → PC | `hello {token or pairCode, device, v}` | handshake; reply `welcome {caps, vlc: {running, version}, token?}` |
| → PC | `cmd {name, args}` | player commands: `play`, `pause`, `seek`, `volume`, `track`, `chapter`, `rate`, `delay`, `fullscreen`, `open {uri, subtitle?, audio?}`, `enqueue`, `launch`, `quit` |
| → PC | `sys {name, args}` | `moveToDisplay`, `displayPower`, `keepAwake`, `sleep`, `shutdown`, `audioDevice`, `systemVolume` |
| → PC | `browse {uri}` / `library {query}` | file lists / library with metadata |
| ← PC | `state {…diff}` | push of `PlayerState` changes (at most 4 Hz, seek/volume are coalesced) |
| ← PC | `event {name, …}` | `vlcStarted`, `vlcExited`, `inputChanged`, `endReached` |
| ↔ | `ping`/`pong` | every 15 s over RFCOMM, 30 s over WS |
| ← PC | `err {id, code, msg}` | command error (`msg` in the phone's language) |

Pairing: the phone asks to pair, a window with a 6-digit code pops up on the PC (and on the TV), the user enters
the code on the phone (QR `vlcr://pair?host=&port=&code=&bt=&fp=` is an alternative);
the phone receives a long-lived token, the PC stores its hash. No TLS in v1 (home network, token);
the `fp` field is reserved for pinning a self-signed certificate.

### 4.5. State model

`PlayerState { connection, vlc: {running, version, apiVersion}, playback: {state, time, length,
position, rate, updatedAt}, item: {uri, title, artUri}, tracks: {audio[], subtitle[], video[],
currentAudio?, currentSubtitle?}, chapters: {index, count, names?}, volume: {vlc0_512, muted,
system?}, video: {fullscreen, aspect, crop}, delays: {audio, subtitle}, playlist: [...],
audioDevices?: [...] }`. The position between polls is interpolated locally from `time` + a monotonic
clock; on `seek` we show the target with a `seeking` flag until it is confirmed.

### 4.6. Android app

- Modules: `:app` (Compose UI, services) and `:core:protocol` (pure Kotlin: models, `status.json`
  parser, companion client, NDJSON). Planned: `:core:naming` (file name parsing), `:core:testing`
  (a Ktor fake VLC reproducing the quirks from live-probe; for now the fixtures in `docs/research/samples/`
  are used directly in tests).
- Stack: Kotlin 2.x, Compose (Material 3), Media3 Session, Ktor client (OkHttp), kotlinx.serialization,
  Koin, DataStore, Coil 3. The exact versions are pinned to the installed SDK (see `CLAUDE.md`); reference points in
  [mobile-stack-and-ux § 1.5](research/mobile-stack-and-ux.md) (a spot check showed that some of the
  numbers there could not be confirmed by search).
- Network: poll VLC once per 1 s while playing, 3 s when paused/stopped, no polling in the background without an FGS;
  one request in flight per type, sequence numbers, stale responses are discarded; a command's response is
  applied immediately as status, plus a "clean" status after 500 ms. Do not send the `Connection`
  header (otherwise VLC closes the connection after each request); treat a body with
  `<title>Error loading` as a command error despite the `200`.
- Track ID: `Original_ID` from `Stream N` if present, otherwise `N` (category keys are localized —
  search by `Type`, not by the key name).
- Volume: `dispatchKeyEvent` in the activity; with the screen off — a Media3 `Player` with
  `DeviceInfo.PLAYBACK_TYPE_REMOTE` (check on a real phone).
- Secrets: the VLC password and the companion token — in `EncryptedSharedPreferences`/Keystore.
- Errors by class: `403` "no password set in VLC", `401` "wrong password", refused "web interface
  off/port", timeout "firewall/other network/PC asleep", empty response "VLC is just starting".
- UI language follows the system (Android 13+ per-app language too), English by default, Russian translation.

### 4.7. Companion (Windows)

- Project `companion/VlcRemote.Companion` — `net9.0-windows10.0.19041.0`, WinForms `NotifyIcon`,
  Kestrel (`CreateSlimBuilder`) for WS and REST diagnostics, WinRT `RfcommServiceProvider`,
  `DnsServiceRegister` via P/Invoke, UDP beacon `:47556`, NAudio, CsWin32 for Win32, Serilog.
- `VlcManager`: finds VLC via `HKLM\SOFTWARE\VideoLAN\VLC`, starts `vlc.exe --extraintf http:rc
  --http-host 0.0.0.0 --http-password <generated> --rc-host 127.0.0.1:4212 --rc-quiet
  --qt-fullscreen-screennumber N`, holds one RC socket and multiplexes it, polls HTTP
  locally and sends the phone a diff; for VLC started externally (from Explorer) it configures `vlcrc`
  automatically, only while VLC is not running (VLC rewrites the file entirely). It reports VLC's status honestly,
  including "open without control" (VLC running without the web interface) with a restart that reopens the same
  file. RC is used only for what HTTP lacks (`adev`, events, `quit`); RC refuses commands while paused, so the
  track state comes from the Lua page, and all actions go over HTTP. RC commands starting with `f`
  toggle fullscreen — send only exact names.
- `DisplayService`: `EnumDisplayMonitors` with friendly EDID names and stable ids; moving the VLC window by PID
  (`EnumWindows` + `SetWindowPos`) and `fullscreen` on the chosen monitor; any new VLC window (including files opened
  by double-click) is placed there automatically; the monitor is chosen from the phone or the tray;
  `SetThreadExecutionState` during playback; `SC_MONITORPOWER`; sleep via `SetSuspendState`.
- `AudioService`: VLC's output device via RC `adev`; system volume via NAudio;
  the system default device via `IPolicyConfig` (undocumented COM, behind a flag).
- `LibraryService`: browsing is done by the companion (works without VLC running, including network shares);
  planned: an index of `D:\shared` (configurable roots), name parsing (guessit/PTN rules),
  TMDB with a cache of ≤ 6 months and attribution, thumbnails via `vlc.exe` snapshot or ffmpeg (if present);
  watch history and resume positions (the companion is the source of truth, shared by all phones).
- Installation: today `scripts/deploy-windows.ps1` (`%LOCALAPPDATA%\VlcRemote\app`, no admin rights), HKCU\Run,
  one UAC prompt for the firewall rules (companion + vlc.exe, private profile); Velopack installs and updates it. Lua pages are
  copied into `%APPDATA%\vlc\lua\http\requests\` at the first start.
- UI language follows Windows, English by default, Russian translation; `err.msg` is rendered in the phone's
  language.

## 5. UX

MVP screens (mockups in [mobile-stack-and-ux § 5.6](research/mobile-stack-and-ux.md)):
1. **Remote / Now Playing** — cover art, title, seek bar with chapters, play/pause at the bottom in the
   thumb zone, ±10/±30 s with auto-repeat, volume with a number, chips "Audio: RU · Subtitles: off".
2. **"Audio and subtitles" sheet** — radio lists with the language first, delay ±50/±500 ms, "remember for this file".
3. **"More" sheet** — chapters (with names at level ≥ 1), rate, aspect/crop, fullscreen,
   sleep timer, audio device/monitor (level 2).
4. **Library** — folders (`browse.json` or the companion's browsing) + a poster grid + "Continue watching";
   favourite folders.
5. **Playlist** with a mini player.
6. **Connection/onboarding** — "Find the PC → Password or code → Check (pause on the TV)";
   without the companion — an honest step-by-step guide with a "check now" button.
7. **Settings** — volume keys, seek steps, theme (dark by default, true black),
   background behaviour.
8. **Background** — `MediaSessionService` + a notification with buttons, a Quick Settings play/pause tile.
9. **First run** — an explanation screen and all permissions at once (notifications, Bluetooth).

Principles: the remote works without looking; the connection never kicks you off the screen; nothing
is hidden silently (unavailable means grey with a "needs the companion" hint); haptics on every tick;
Now Playing stays after stop.

## 6. Milestones

### M0 — Spikes
Goal: remove technical risks before writing the UI. In practice the spikes were done directly in the implementation
(no separate `spikes/` folder).
- [x] Android ↔ Windows RFCOMM: pairing, NDJSON exchange, reconnect (verified on Galaxy S24 ↔ TV laptop).
- [x] `NsdManager` finds the service announced by `DnsServiceRegister`; UDP beacon as a fallback.
- [x] Moving the VLC window to a second monitor + `fullscreen`; `--qt-fullscreen-screennumber`.
- [ ] Media3 "remote volume" with the screen off on the target phone.
- [ ] Ktor fake VLC with fixtures from `docs/research/samples/` (the fixtures are used directly in unit tests).

### M1 — MVP "Lite" (levels 0/1) — done
- [x] `:core:protocol`: `status.json`/`playlist.json`/`browse.json` parser, commands, `Capabilities`.
- [x] Connection screen: `/24` scan, manual entry, test with distinguishable errors, Keystore.
- [x] Remote: transport, seek bar, volume, phone keys, chapters by number.
- [x] Track sheet by `Stream N`; at level 1 — current track and chapter names.
- [x] File browsing with a "drives" root, favourite folders, play/enqueue without duplicates.
- [x] Playlist; `MediaSessionService` + notification.
- [x] Script `companion/lua/install.ps1`: copies the stock `lua\http` + our pages into
      `%APPDATA%\vlc\lua\http`, offers to restart VLC; "Close VLC" via `vlc://quit`.

Criterion: a movie from `D:\shared` starts and is fully controlled from the phone without the companion.

### M2 — Companion v1 (level 2, LAN) — done
- [x] Tray + Kestrel WS + NDJSON session + pairing by code/QR.
- [x] Automatic pairing: the code window pops up on the PC, the 6-digit code is entered on the phone.
- [x] `VlcManager`: starting/stopping VLC, RC bridge (current track, `adev`, events), diff push.
- [x] Automatic `vlcrc` configuration; honest VLC status including "open without control" and a restart with the
      same file.
- [x] mDNS + UDP beacon; `CompanionBackend` on the phone with automatic channel selection.
- [x] Library browsing by the companion (works without VLC, network shares).
- [x] Window on the TV, keep-awake, PC sleep, monitor power-off.
- [x] Choosing the monitor for the VLC window from the phone and the tray (friendly EDID names, stable ids,
      automatic placement of any new VLC window including double-click opens, fullscreen on the chosen monitor).
- [x] Autostart (HKCU\Run), installing the Lua pages from the companion.
- [x] Localization: system language, English by default, Russian translation; `err.msg` in the phone's language.
- [x] First-run setup window: firewall rules for the companion and VLC via one UAC prompt, autostart,
      VLC configuration, keep-awake, media station.
- [x] Velopack installer and quiet auto-update (see the companion README).

Criterion: the phone finds the PC by itself, starts VLC on the TV, switches the audio device.

### M3 — Bluetooth — done
- [x] RFCOMM server in the companion, Bluetooth transport on the phone (choosing from paired devices instead of
      `CompanionDeviceManager`).
- [x] Automatic LAN ↔ BT switching without losing state, including the switch back to Wi-Fi
      (verified on Galaxy S24 ↔ TV laptop).
- [x] First-run permissions on the phone: notifications + Bluetooth with an explanation screen.

Criterion: with Wi-Fi off on the phone, the remote keeps working.

### M4 — Library, history and polish
- [x] **Watch history and resume ("Continue watching")**: the companion records every played item (including
      files opened manually on the PC) with position, length, a finished flag and the last-watched time, offers to
      resume on open; the phone shows a "Continue watching" row and a history screen. The companion is the source
      of truth, so several phones share it.
- [x] **Polish the app layout** (user request): audit of all screens on the phone in Russian and English at
      font scales 1.0/1.3/2.0 → fixes: labels of tabs, buttons, chips and badges stay on one line, chips wrap as a
      whole, file names break at dots, the playback controls are pinned on the Remote screen, a single row of
      quick tiles (audio / subtitles / display / more). Roborazzi screenshot tests of the shared controls.
      Left: landscape and tablet check.
- [x] **Refactoring and code cleanliness** (user request): split the large files and classes (the Remote screen,
      the companion host, the deploy scripts), remove duplication and dead code, consistent naming and
      error handling, enable and fix the analysers (detekt/ktlint for Kotlin, .NET analyzers and
      `TreatWarningsAsErrors` for the companion, PSScriptAnalyzer for the scripts) and run them in CI; no
      behaviour changes, covered by the existing tests. Screens get a stateless content composable
      (`RemoteContent(ui, actions)` etc.), so whole screens can be screenshot-tested with fake state.
      Done: .NET analysers (warnings as errors, unused usings), `VlcManager` split and its command mapping/command
      line in Core with tests; detekt with zero findings in CI (shared backoff, VLC volume scale, constants);
      stateless Remote and Connection screens split into files, with whole-screen screenshot tests;
      PSScriptAnalyzer in CI. Then an architecture review (ADR-0005: the simplest code that is correct, no
      framework for its own sake): one owner per piece of state (settings writes, keep-awake, pairing cache,
      immutable shared state), one VLC lifecycle operation at a time, cancellation never swallowed, background
      work tied to the companion's lifetime, the polling decisions as tested pure functions; on Android every
      screen is `XxxScreen` + stateless `XxxContent(ui, actions)` with screenshot tests, one local state flow per
      ViewModel, the position ticker in the scrub bar; dead code removed on both sides.
- [ ] **Library with posters** (user request): media folders in the setup window, file name parsing, TMDB metadata
      via the companion (built-in token from CI + the user's own), cache ≤ 6 months; the phone shows posters next to
      the folders. Done: the companion side and the protocol (`getLibrary`, `getShow`, `state.library`).
      Left: the phone's poster grid, series and movie pages.
- [x] Installer and auto-update for the companion (Velopack): `Setup.exe` per user with the .NET runtime, quiet
      updates from GitHub Releases applied when VLC is not playing, test builds follow test builds.
- [x] **Media station** (user request): the PC at the TV keeps working with the lid closed and never sleeps (power plan
      item in the setup window, tray warning when Windows changes it back); after a monitor change the VLC window
      returns to the TV fullscreen, playback pauses while the TV is gone; the phone-chosen audio device is restored
      when it comes back.
- [x] Settings, true black.
- [ ] Sleep timer.
- [ ] Landscape / tablet layouts — postponed indefinitely (user decision).
- [x] Tests: Roborazzi screenshots of every screen (2 languages × 2 font scales), a checklist on real devices.
- [ ] Fake-VLC scenario tests.

### M5 — Later
Quick Settings tile, Glance widget, Wear OS, subtitles via the companion (OpenSubtitles/Subdl), VLC 4.x, TLS pinning,
iOS via Compose Multiplatform.

### Postponed
- **TV control** (user decision 2026-10-13): Samsung UE55H6200 (2014, Orsay) — no HDMI-CEC on the laptop's
  GTX 1050 output and no DDC/CI; volume is possible via UPnP RenderingControl, power-off via the encrypted
  H-series protocol, power-on only with a Pulse-Eight CEC adapter. See [tv-control](research/tv-control.md).
- **BLE transport** — RFCOMM covers the fallback; BLE would only add complexity.
- **Bluetooth PAN hint** — not needed while RFCOMM works.

### Development tooling — done
- [x] Release pipeline: a test build (`v<VERSION>-beta<NNNN>`) on every merge to `main`, release notes collected
      from PRs, stable releases on demand.
- [x] Deploy scripts for Windows and Android (device allowlist, automatic phone discovery over Wi-Fi debugging).
- [x] SSH-based remote debugging of the TV laptop (`scripts/dev-laptop.ps1`).
- [x] Deploy scripts download releases anonymously (public repository), `gh` only as a fallback.

## 7. Risks and open questions

| Risk | Mitigation |
|---|---|
| `NsdManager` is capricious on different firmwares | always the UDP beacon + saved IP + manual entry |
| The RFCOMM listener does not survive the laptop's sleep | restart the listener on a power event; the phone falls back and reconnects |
| Remote volume with the screen off does not work on the target phone | fallback: FGS notification with buttons, keys only in the foreground |
| VLC 4 changes track IDs and fields | branching on `apiversion`; 4.x support is a separate milestone |
| The firewall blocks VLC/the companion | detection (responds from localhost, not from the network) + UAC helper + instructions |
| Windows firewall prompt on first start, or "block" rules created when the user dismissed it | the setup window adds explicit allow rules (private profile) via one UAC prompt; a "block" rule for the companion or VLC is reported with instructions |
| `IPolicyConfig` is undocumented and may break | behind a flag; the main path is `adev` inside VLC |
| TMDB — non-commercial use only | attribution, the user's own key, no monetization |
| The percent form of `volume` does not work | only absolute/relative values |
| A VLC update changes the stock Lua pages, and the user copy becomes outdated | the installer checks the VLC version and rebuilds the copy; the companion does this itself at start |
| `in_play&input=vlc://quit` lets anyone who knows the HTTP password close VLC | the password is generated by the companion, the interface listens only on the LAN; a reminder in onboarding |

Note: the BSODs seen on the developer PC are unrelated to the project and are not a risk for the remote or the
companion.

## 8. Developer environment (this PC)

- Android SDK `D:\Android\android-sdk` (platforms 34/35, build-tools 35.0.0), JDK in
  `C:\Program Files\Android\jdk`; Android Studio was not found (possibly IntelliJ/VS).
- .NET SDK 9.0.310 and 10.0.102; Visual Studio 18.2 with MAUI workloads (not used).
- VLC 3.0.23 in `C:\Program Files\VideoLAN\VLC`; MediaTek Bluetooth adapter; a PAN adapter is present.
- The target laptop at the TV is a separate device, reachable over SSH (`scripts/dev-laptop.ps1`); monitors, audio,
  sleep and Bluetooth are checked there. Test phone: Samsung Galaxy S24.

## 9. Repository structure

```
vlc-remote/
  README.md, CLAUDE.md, VERSION
  docs/            PLAN.md, adr/, research/ (+ samples/ — real VLC responses, test fixtures)
  protocol/        protocol description, message schema (JSON Schema), direct VLC HTTP reference
  android/         Gradle project: app, core/protocol
  companion/       VlcRemote.Core, VlcRemote.Companion, tests (.NET), lua/ (tracks.json, setvar.json,
                   install.ps1), tools/ (localization generator)
  scripts/         deploy-windows, deploy-android, deploy-lib, dev-laptop, laptop-setup, release_notes.py
  .github/         CI, PR checks, release workflow, PR template
```

## 10. Next steps

1. **Library**: a setup step for the media folders, file name parsing, posters/backdrops (TMDB) via the companion,
   cache; on the phone a poster grid next to the folders.
2. **Later**: Quick Settings tile, widget, Wear OS, subtitle download, VLC 4.x, TLS pinning, iOS.

Postponed (see § 6 "Postponed"): TV control, BLE transport, Bluetooth PAN hint.
