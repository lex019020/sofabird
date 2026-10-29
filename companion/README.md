# Sofabird (Windows)

The companion for the `sofabird` Android remote: a .NET 10 tray app that owns the lifecycle of
VLC 3.0.x on the PC at the TV and gives the phone level 2 of the protocol
([protocol/README.md](../protocol/README.md)): current tracks, chapter names, audio devices,
push events, starting VLC, moving the window to the TV, power, system volume.

Milestones **M2** (LAN) and **M3** (Bluetooth RFCOMM) are done; it is installed by a Velopack installer and updates
itself (see "Installing and updates"); the media library with posters (TMDB) is in M4. Next: Wake-on-LAN
(the companion tells the phone its MAC addresses) and the sleep timer.

## Contents

```
companion/
├── Sofabird.sln
├── Sofabird.Core/            net10.0 library without Windows APIs (tested on any OS)
│   ├── Protocol/              protocol messages (System.Text.Json, source-generated, polymorphism by "t")
│   ├── Vlc/                   VlcHttpClient, VlcJsonParser, VlcRcClient, RcParser
│   ├── State/                 StateBuilder (status.json + tracks.json + RC → state), StateCoalescer (≤ 4/s)
│   ├── Pairing/               PairingService (code for 5 min, 32-byte base64url token, SHA-256 stored)
│   ├── Session/               Session (NDJSON session over ILineTransport), StreamLineTransport (NDJSON over a byte stream), ISessionHost, CommandArgs
│   ├── Bluetooth/             BluetoothFormat (address AA:BB:…, SDP attribute encoding)
│   ├── Displays/              MonitorTopology (monitor names/ids/kinds), DisplayController (display sys commands,
│   │                          preferred monitor, one-time move of a new VLC window), AutoPlacement
│   ├── Lua/                   LuaPagesInstaller (copy of VLC's stock pages + ours in %APPDATA%)
│   └── Setup/                 FirewallPlanner (which firewall rules allow/block, what to remove/add), SetupPolicy
│                              (when the setup window and the startup notice appear)
├── Sofabird.Companion/       WinExe net10.0-windows10.0.19041.0: tray, Kestrel, VlcManager, Win32
│   ├── Net/                   WebServer (/ws, /info, /art), WebSocketLineTransport, UdpBeaconResponder, DnsSdAdvertiser,
│   │                          BluetoothRfcommServer + RfcommLineTransport (RFCOMM service, M3)
│   ├── Vlc/                   VlcLocator, VlcManager (process, polling, RC, commands), EmbeddedLuaPages
│   ├── Services/              DisplayService (monitors via the CCD API, the VLC window), VlcWindowHost, PowerService,
│   │                          AudioService, AutostartService
│   ├── Setup/                 SetupForm (setup and permissions window), SetupBackend, ComFirewallRuleSource (COM
│   │                          HNetCfg.FwPolicy2), FirewallElevation (elevated --apply-firewall helper), NetworkCategories
│   └── Tray/                  TrayContext (menu), PairingForm (code on the TV screen), TrayIconFactory
├── Sofabird.Core.Tests/      xunit: parsers on real VLC 3.0.23 responses (docs/research/samples), session, pairing
└── lua/                       level 1 Lua pages and install.ps1 (they work without the companion too)
```

Separation principle: `Core` knows nothing about WinForms and ASP.NET — the session runs over the
"line in / line out" abstraction (`ILineTransport`): WebSocket and RFCOMM are two transports of one and the same
session (framing of the RFCOMM stream is `StreamLineTransport` in `Core`, tested without WinRT).

## Build and tests

Requires the .NET SDK 10.0 (verified with 10.0.102) and Windows for the companion project.

```powershell
dotnet build companion\Sofabird.sln -c Release
dotnet test  companion\Sofabird.sln
```

Code analysis runs in every build (`Directory.Build.props`: `AnalysisLevel` 9.0-recommended (pinned), warnings are
errors). Exceptions live in `companion/.editorconfig`, each with its reason.

Running from source:

```powershell
dotnet run --project companion\Sofabird.Companion -c Release
```

For debugging with a separate data folder: `Sofabird.exe --data-dir D:\tmp\sofabird`
(or the `SOFABIRD_DATA_DIR` environment variable).

## Installing and updates

The companion is installed by `Sofabird-Setup.exe` from GitHub Releases (Velopack; per user, into
`%LOCALAPPDATA%\Sofabird`, with the .NET 10 runtime when it is missing) — by hand or through
`scripts\deploy-windows.ps1` (which adds VLC, the firewall rules and autostart; `-Local` builds the installer from
source). Then it updates itself (`Services/UpdateService.cs`): a few minutes after the start and every 6 hours it checks
GitHub Releases, downloads the update in the background and restarts into it as soon as VLC is not playing; the first
start of a new version shows "The companion was updated to …" in the tray. Test builds follow test builds and
releases follow releases (`updateChannel` in `settings.json`, tray: "Get test builds"); builds made with `-Local`
(`-local.*`) do not update themselves. Uninstalling (Windows "Apps", or `deploy-windows.ps1 -Uninstall`) removes the
autostart entry and keeps the settings. Details are in CLAUDE.md.
The version comes from the `VERSION` file in the root (`-p:VersionSuffix=beta0042` for test builds) and is shown in the
tray icon tooltip and in `/info`.

## Publishing

```powershell
. .\scripts\deploy-lib.ps1; New-CompanionPackage (Get-Location).Path 0.4.0-local.1 local.1 .\releases
```

`dotnet publish` (framework-dependent, a folder of ~27 MB, 24 MB of which is the Windows SDK projection
`Microsoft.Windows.SDK.NET.dll`, pulled in by the `net10.0-windows10.0.19041.0` TFM for the WinRT Bluetooth) packed
by `dotnet vpk pack` into `Sofabird-win-x64-Setup.exe` (~17 MB, published as `Sofabird-Setup.exe`) and a ~9 MB update package. The PC needs the
**.NET Desktop Runtime 10** and the **ASP.NET Core Runtime 10** (Kestrel): the installer installs them when they are
missing. A self-contained build would be ~160 MB.

`companion/global.json` pins the SDK to 10.0.1xx (or a newer feature band). Publishing uses `--no-self-contained`:
SDK 10 reads `--self-contained false` differently and would produce a 150 MB self-contained build (the csproj also
sets `<SelfContained>false</SelfContained>` just in case). .NET 10 is an LTS release, supported until November 2028.

## What it does on startup

0. **Setup and permissions window** on the first start (and once more after an update that adds an item) — see
   "Setup and permissions" below. If the firewall has no rules for the companion/VLC yet, the window is shown
   *before* the network listeners start, so Windows never shows its own "allow access" prompt. Otherwise it opens
   next to the running companion. On every later start the companion checks the firewall and offers a fix in a tray
   notification (see below).
1. Starts Kestrel on `0.0.0.0:47555`: `ws://…/ws` (NDJSON session), `GET /info` (no
   authorization), `GET /art?plId=N` (header `Authorization: Bearer <token>`, proxies VLC's `/art`).
2. Answers the UDP beacon `SOFABIRD_DISCOVER 1` on port 47556 with unicast JSON `{"t":"beacon",…}`.
3. Advertises `_sofabird._tcp` through the built-in Windows DNS-SD responder (`DnsServiceRegister`);
   on error it writes to the log and keeps working (the beacon and manual address entry remain).
4. Finds VLC (`HKLM\SOFTWARE\VideoLAN\VLC`, then Program Files). If VLC is not running, it writes
   the web interface and RC into `vlcrc` (see "VLC auto-configuration"). If VLC is running and answers over HTTP with our
   password, it takes control of it; if it is running without control — `vlc.status = noControl`, a tray
   notification and the menu item "Restart VLC with phone control" (see "VLC open without control").
5. Polls `status.json` every 500 ms during playback and every 2 s otherwise, keeps one
   RC socket `127.0.0.1:4212` (`status change:` events, `adev`, the current track when
   `tracks.json` is missing, `quit`), builds the full `state` object and sends it to all phones on
   changes, at most 4 times per second.
6. Installs the level 1 Lua pages into `%APPDATA%\vlc\lua\http` (a copy of VLC's stock folder +
   `requests/tracks.json`, `requests/setvar.json`, the `sofabird.version` file). Reinstalls them when the
   VLC version or the pages themselves change. Does this only when VLC is not running; otherwise postpones it
   until VLC closes (the menu item shows "after VLC closes").
7. Publishes the "Sofabird" Bluetooth RFCOMM service for a phone without Wi‑Fi (see "Bluetooth"). No adapter or
   Bluetooth off — it writes to the log and works without it.

VLC launched by the companion:

```
vlc.exe --extraintf http:rc --http-host 0.0.0.0 --http-port 8080 --http-password <from settings.json>
        --rc-host 127.0.0.1:4212 --rc-quiet [--qt-fullscreen-screennumber=N] [--fullscreen] [uri]
```

## VLC auto-configuration (vlcrc)

So that VLC opened in any way (double-clicking a movie in Explorer, the Start menu) can be controlled from the
phone right away, the companion writes into `%APPDATA%\vlc\vlcrc` (into the file's "native" sections):

| Section | Key | Value |
|---|---|---|
| `[core]` | `extraintf` | `http` and `rc` are added to the interfaces already listed (e.g. `qt` → `qt:http:rc`) |
| `[core]` | `http-port` | `vlcHttpPort` from `settings.json` (8080) |
| `[lua]` | `http-password` | `vlcHttpPassword` from `settings.json` |
| `[oldrc]` | `rc-host` | `127.0.0.1:<vlcRcPort>` (4212) |
| `[oldrc]` | `rc-quiet` | `1` — otherwise RC opens a console window on Windows |
| `[qt]` | `qt-continue` | `0` — VLC's own "continue playback?" is off; the phone asks instead (see "Watch history") |

`http-host` is not touched (by default VLC listens on all interfaces — the phone can also talk to VLC directly).

- The file is edited **only when no `vlc.exe` is running** (VLC overwrites vlcrc from memory): when the
  companion starts and every time VLC closes.
- Commented-out defaults (`#extraintf=`) are uncommented in place; a missing key is appended to the
  end of its section, a missing section — to the end of the file. The encoding (UTF-8 with BOM) and line endings are
  preserved, the write is atomic (temp file + replace). If there is no file (VLC has never been started), a
  minimal file with these sections is created.
- Before the **first** change a copy `%APPDATA%\vlc\vlcrc.sofabird-backup` is made (it is never overwritten afterwards).
  To restore everything: close VLC, delete `vlcrc` and rename the copy back.
- If everything is already configured, the file is not touched; only the fact of a change is logged.
- To disable: `"configureVlc": false` in `settings.json` (restart the companion). `"vlcrcPath"` — a different path
  to the file (for tests).

## VLC open without control

If VLC was opened before the companion configured vlcrc (or the setting was turned off), the process exists but
the web interface does not answer with the companion's password — this is `vlc.status = noControl` (`running = false`). The companion:

- once per VLC process shows the notification "VLC is open without phone control. Restart it, and from then on VLC
  will always start with phone control."; clicking the notification restarts VLC;
- on `cmd launch` from the phone (the "Restart with control" button) and on the menu item "Restart VLC with phone
  control" restarts VLC: takes the file/URI from the process command line (the last argument that
  looks like a path or URI; `--started-from-file` and option values are skipped), closes VLC gracefully
  (closing the main window, up to 5 s, then forcibly), writes vlcrc and starts VLC with the same file.
  The playback position is lost.

For the first 20 s after the process starts VLC is considered "starting" (`starting`), so that a web interface still
coming up is not mistaken for lack of control; three failed requests in a row to a controlled VLC are not a reason to
declare `noControl` right away either.

## Pairing

A phone without a token (or with a forgotten token) sends `hello` without a code — the companion answers
`err pairing_required` and **opens the code window by itself** on top of all windows: on the screen where the VLC window
is now, otherwise on the preferred one (the TV), otherwise on the primary one. The header reads "Phone “<name>” wants to
connect", below it the code, the address and a countdown. The phone shows the "Enter the code from the computer screen"
field and reconnects with `pairCode`.

- While the code is active, repeated requests (including from another phone) do not create a new code — the window is
  only raised to the top.
- A wrong code → `err unauthorized`, the window stays. If the code has expired or the attempts (5) are used up, the
  phone's next attempt opens a window with a new code.
- At most one window per 10 s per phone and at most one new code per 10 s overall — a phone reconnecting
  in a loop does not flood the screen with windows, and brute-forcing the code does not speed up.
- On success the window shows "✓" and closes after 3 s, the tray shows "Phone “<name>” paired".
- Closing the window (click, Esc) = cancelling the code. The window can be opened by hand with the menu item "Pair a
  phone…" or by double-clicking the icon.

## Bluetooth

A fallback channel when the phone has no network in common with the PC (protocol — `protocol/README.md`, "Transport" and
"Discovery → Bluetooth").

**How it works.** The companion publishes an RFCOMM service in SDP with UUID `47fe5bd6-98ad-4d9f-9812-174b3394037d` and
the name `Sofabird` (WinRT `RfcommServiceProvider` + `StreamSocketListener`, no administrator rights). The PC does
**not** become discoverable: only a phone already paired with it can connect. The channel carries the same NDJSON as
the WebSocket (UTF-8, one line per message, separator `\n`, at most 1 MiB per line), the same `hello` with the
token and the same code window for a new phone; in the log the session looks like `bt <phone name> (<address>)`. Up to
4 Bluetooth connections at a time.

The companion reports the adapter address (`AA:BB:CC:DD:EE:FF`) in the `bt` field of the `/info` response, the UDP beacon
and `welcome` while the radio is on; while the service is published, `caps` contains `bluetooth`. The phone remembers the
address when it connects over Wi‑Fi and switches to Bluetooth by itself if the PC does not answer on the network.

**Requirements.**

- A Bluetooth Classic (BR/EDR) adapter and Bluetooth turned on in Windows.
- The phone is **paired with the PC** using Windows ("Settings → Bluetooth & devices → Add device") or
  Android. The companion does not initiate Bluetooth pairing. The app token is still needed: the first connection is
  the usual code window on the PC screen.
- The `bluetooth` setting in `settings.json` (`true` by default; `false` — the service is not published, there is no
  `bt` field).
- The channel protection level is `BluetoothEncryptionAllowNullAuthentication`: encryption is mandatory, any pairing
  will do, including "Just Works" without code comparison (as in the Microsoft sample and in the check on this PC). The
  phone is authenticated by the protocol token. `bluetoothRequireAuthentication: true` requires a MITM-protected key
  (`BluetoothEncryptionWithAuthentication`) — then a phone paired without code comparison will not connect.

**Resilience.** No adapter or the radio is off — the companion works without Bluetooth and re-checks once a
minute. It recreates the service and the listener after waking from sleep (`PowerModeChanged → Resume`, after 5 s) and
when the radio is turned on (`Radio.StateChanged`); WinRT errors are retried with pauses from 2 s to 5 min. The state is
shown in the tray menu: "Bluetooth: on / starting… / turned off on this PC / unavailable / disabled in settings".

**If the phone does not connect over Bluetooth.**

- The tray shows "Bluetooth: unavailable" — the log has the line `Bluetooth RFCOMM service failed to start … (0x…)`;
  check that the Windows service "Bluetooth Support Service" (`bthserv`) is running, and restart the companion.
- "Bluetooth: turned off on this PC" — turn Bluetooth on in Windows (airplane mode also turns the radio off).
- The tray shows "on" but the phone cannot find the service — Android caches the PC's service list: remove the PC from
  the paired devices on the phone and pair again (or turn Bluetooth off and on on the phone).
- The link drops after idling — in Device Manager, clear the Bluetooth adapter's checkbox "Allow the computer to turn
  off this device to save power" (if there is one); the phone reconnects by itself.
- The phone is paired with "Just Works" and `bluetoothRequireAuthentication` is on — turn the setting off or re-pair
  the phone with code comparison.

## TV screen (the display for VLC, YouTube and Moonlight)

The companion remembers the **preferred monitor** and opens VLC on it — both when it is launched from the phone and when
it is opened by hand (double-clicking a movie in Explorer). The TV browser (YouTube) and Moonlight's stream window go
there too: both open on the primary monitor or where they were last, so the companion moves their window to the
preferred monitor (else to the one with VLC's window) when it shows up, as `MoveWindowToMonitor` does for a borderless
fullscreen VLC.

- **Monitors.** The list is built from `Screen.AllScreens` (bounds, primary) and the Windows CCD API (`QueryDisplayConfig` +
  `DisplayConfigGetDeviceInfo`): a friendly name from EDID ("SAMSUNG", "L27q-35"), the connection type (`internal` — the
  laptop's built-in panel: eDP/LVDS/"internal"; `external` — HDMI/DP/…) and a **stable id** — the monitor's device
  path (`\\?\DISPLAY#SAM0F99#…`). The id does not change when the TV is turned off and on (unlike the
  `\\.\DISPLAYn` number and the index in the list). No name in EDID — "Display N" (in the phone's / Windows language),
  for the built-in panel — "Built-in display"; no device path — id = the GDI name. In duplicate mode — one row per
  source (names joined with " + ", the id is the TV's). At startup and on every change of the display configuration a
  line per monitor is written to the log: name, kind, primary, `gdi`, bounds, id — so the list can be checked remotely
  (`dev-laptop.ps1 -Logs`). Over SSH `Screen.AllScreens` sees only a dummy screen — check through the companion.
- **Choice.** On the phone (`sys moveToDisplay` / `preferredDisplay`) or in the tray ("TV screen ▸"): the VLC window is
  moved (leave fullscreen → move → back to fullscreen if it was) **and the monitor is remembered**
  (`preferredDisplayId` in `settings.json`). VLC not running — it is only remembered. An open TV browser and a Moonlight
  stream move to the chosen monitor right away (`DisplayController.TvScreenChosen`, also when it was the preferred one
  already). "Don't manage the display" in the
  tray (`sys preferredDisplay {"displayId": null}`) — the companion no longer touches the VLC window.
- **A new VLC window** is moved to the preferred monitor **once**, if it is connected and the window is not there
  (we wait up to 10 s for the main window). After that the window is left alone — if you drag it back, the companion
  does not argue. Windows of a VLC started before the companion are not touched either. TV off — the window stays where
  it opened.
- **Launch by the companion** (from the phone or the tray; the preferred monitor is connected and there are 2+ monitors):
  VLC starts without a file and with `--qt-fullscreen-screennumber=-1` (fullscreen goes to the main window's monitor;
  Qt's screen numbers do not match ours), the window is moved, and only then the file is opened over HTTP — with
  `launchFullscreen` the movie goes fullscreen on the right display at once.
- **Moving the window.** A normal window keeps its size (no larger than the work area) and is centred, a maximized one
  is maximized there as well. A controlled fullscreen VLC is handled via HTTP `fullscreen`; without control (opened
  before vlcrc was configured) a borderless monitor-sized window is simply stretched over the new monitor.
- **Monitor changes** (laptop lid closed, TV switched off at the mains or unplugged, HDMI re-plugged). Windows rebuilds
  the desktop in a few steps; 2 s after the last one the companion reacts (`TopologyRecovery` in Core):
  - the preferred monitor is there and VLC was on it (or its own monitor went away) — the window is restored if the
    change minimized it, moved back and made **fullscreen again if it was** (closing the lid makes VLC drop fullscreen
    and minimize). A window the user minimized or moved to another monitor on purpose is left alone;
  - the preferred monitor went away with VLC on it — playback is **paused**; when the monitor is back, the window
    returns there (fullscreen as it was), playback stays paused until the phone resumes it;
  - no preferred monitor — nothing is done.

  A Samsung TV switched off with its remote keeps the HDMI link (measured 2026-10-13: neither the monitor nor its
  HDMI audio device disappeared), so this matters for lid changes, mains power and cables.
- **Old format.** `preferredDisplay` (an index) from earlier versions is replaced at the first start with the id of the
  monitor with that index; `moveToDisplay {"display": int}` and `display` in `launch`/`open` are still understood
  (`display` in `launch` is a one-time choice, not remembered).

## Watch history

The companion is the source of truth for what was watched (protocol: "Watch history and resume", capability
`history`). Code: `Sofabird.Core/History/` — `HistoryStore` (the file), `HistoryTracker` (decisions from VLC polls),
`HistoryRules` (pure rules), `MediaUri` (URI normalization); `VlcManager` performs the tracker's actions on VLC.

- **What is recorded.** Every *file* VLC plays (opened from a phone, from Explorer, from VLC's playlist; streams and
  discs are not recorded): title, length, play count, last watched, position. The position is saved every 10 s while
  playing and on pause, stop, input change, VLC exit and companion shutdown. Less than 5% left → `finished`
  (position 0); a file played to its end is finished too. A finished file that is watched again becomes "in progress"
  once it passes 5 minutes; reaching the credits and seeking back in the same playback keeps it finished.
- **Keys.** The URI is normalized (percent-decoded and re-encoded, drive letter and host in lower case, compared
  case-insensitively), so VLC's, .NET's and the phone's spellings match. File name + size is a second key: when the
  same file is opened by another path (`D:\shared\x.mkv` ↔ `\\htpc\shared\x.mkv`) the entry moves to the new URI.
- **Resume prompt.** A file that starts (time ≤ 30 s, so a companion restart during a movie does not interrupt it) with
  ≥ 300 s watched and not finished is paused at once (`pl_forcepause`, repeated once per second up to 10 times while
  VLC is still opening it); `state.resume` + event `resumePrompt` go to the phones. `cmd resume {"choice":"resume"}`
  seeks and plays, `"restart"` plays from 0; the first answer wins, later ones are ignored (`ok`). Without an answer VLC
  stays paused indefinitely. Play pressed in VLC (or `cmd play` from a phone) clears the question and playback goes on
  from the beginning; the old resume point is kept until this playback passes 5 minutes, so a few accidental seconds
  do not lose it. Another file, stop or VLC exit while waiting also clears it without touching the resume point.
- **Fullscreen and the question.** A window opened manually is made fullscreen on the preferred monitor once VLC has a
  video output; while the question keeps VLC paused the wait does not time out (a paused file may have no video output
  yet), and fullscreen follows the answer. A companion launch uses `--fullscreen` and is not affected.
- **Next episode.** When a file becomes finished (or ends) and its folder has a next video file in natural order
  ("E2" before "E10") that is not finished, `state.next` + event `nextEpisode` are sent. Nothing is played; the offer
  is cleared by `cmd dismissNext` or when another file starts. VLC moving on to the next playlist item by itself does
  not produce an offer. Video extensions: `HistoryRules.VideoExtensions`.
- **Track choices per folder.** Audio / subtitle choices are remembered for the file's folder — from `cmd track` and
  from VLC itself (a change of the current track seen ≥ 3 s after the start; VLC's own initial choice and the echo of
  the companion's choice are ignored; changes made in VLC's menu are only seen with the Lua pages installed). Every new
  file of the folder gets the best match (language → name → codec; a track must match the language or the name;
  "subtitles off" stays off) as soon as its tracks are known, also while the resume question is open. An `open` with
  explicit `audioTrack`/`subtitleTrack`/`subtitle` wins over the folder's choices.
- **Requests.** `getHistory` (`continue` | `all`, default `all`; `limit` default 50, max 500; `offset`; `total` before
  paging), `historyUpdate` (`finished` true/false — marking an unplayed file as watched adds it; `remove`),
  `historyClear` (entries and track choices). `browse` items get `progress` (1 when finished) and `finished` for files
  in the history (also matched by name + size). `state.history.revision` changes on every visible change (not on the
  periodic 10 s saves). `artUrl` is not sent for history items.
- **File.** `history.json` in the data dir: schema version 1, written atomically (temp file + replace), at most every
  2 s and on exit. A file that cannot be read is renamed to `history.json.corrupt-<time>` and the history starts empty;
  a file from a newer companion is read as far as it is understood. Entries not watched for 183 days are deleted at
  startup and once a day (with the track choices of folders left without entries).
- **Logs.** INFO: resume prompt, answer, cleared without an answer, next-episode offer, applied/remembered track
  choices, phone marks/removals.

## Library

The **media folders** (setup window → "Media folders", `mediaRoots` in `settings.json`; local disks or network
shares) are the phone's library of movies and series (`Services/LibraryService.cs`, `Sofabird.Core/Library/`):

- **Scan** at the start, right after the folders change and every 30 minutes: every video file under them (hidden,
  system and unreadable entries, `sample` files and `Extras`/`Featurettes`/`Trailers` folders are skipped). A folder
  that cannot be reached is reported (`missingFolders` in `library`, a warning in the log and in the setup window).
- **Network folders** are kept as network paths: a folder picked on a mapped drive letter (`Y:\`) is saved as
  `\\server\share`, and saved letters are converted at the start while they are mapped (`NetworkDrives`) — a letter
  mapped without "Reconnect at sign-in" is gone after a restart, the network path is not.
- **Network folders and sleep:** reading a network folder wakes the computer it is on, so the 30-minute timer reads
  local folders only (`LibraryRescanPolicy`); a network folder is read at the start, when the folders change, and when
  a phone asks for the library (`getLibrary`) and its last scan is older than 30 minutes. Until then its last list
  stays.
- **Names** (`ReleaseNameParser`, the PTN / guessit approach): the title is what comes before the first anchor — the
  year, `SxxEyy` / `1x02`, a quality tag (`2160p`, `BDRemux`, `x265`, …). "1917.2019…" is "1917" from 2019; an
  episode without a season (`E05.mkv`) takes it from a season folder ("Season 2", "S02", "Сезон 2"), the series name
  from the folder above; a movie file without a year in a folder with one ("Inception (2010)/movie.mkv") takes the
  folder's name. A series is every episode with the same title, wherever it is; two files of the same movie are its
  versions.
- **TMDB** (`TmdbClient`): the movie / series by title and year (and again without the year), and the seasons'
  episode names and stills, in Windows' UI language. Answers are kept in `library-metadata.json` for at most 6 months
  (TMDB terms); a title TMDB did not know is asked again after a week. The token of the release builds comes from the
  `TMDB_API_TOKEN` secret at build time (never in the repository); `tmdbToken` in `settings.json` overrides it.
  Without a token the library has the parsed titles only. Attribution is in the setup window (and the phone's
  library).
- **Phones**: `state.library.revision` changes when files appear/disappear or metadata arrives (at most every 3 s);
  `getLibrary` / `getShow` (protocol/README.md "Library").

## Moonlight: another PC's screen on the TV

When Moonlight (Moonlight-qt, installed and paired with a Sunshine host by the user) is found in Program Files or
`%LOCALAPPDATA%\Programs`, the companion reports the `moonlight` cap. `sys moonlightStart` pauses VLC, closes
Moonlight's own window and runs `Moonlight.exe stream <host> Desktop --display-mode borderless --background-gamepad --keep-awake`;
`sys moonlightStop` runs `Moonlight.exe quit <host>` and closes whatever Moonlight window is left. The paired
computers and their apps are read from Moonlight's settings (`HKCU\Software\Moonlight Game Streaming Project\Moonlight\hosts`);
`state.system.moonlightHost` is the first one, `moonlightStreaming` is true while any `Moonlight.exe stream …` runs (also one
started by hand). The stream opens on the display of Moonlight's window: with the lid closed that is the TV.

## YouTube on the TV

With a browser for it (Firefox per user in `%LOCALAPPDATA%\Mozilla Firefox`, Firefox in Program Files, or LibreWolf; the
`browserPath` setting overrides) the companion reports the `youtube` cap. It keeps a profile of its own for the TV,
`%AppData%\Sofabird\tv-browser`, separate from the user's everyday browser; its `user.js` is written before each start
(no first-run pages, autoplay, no session restore, no fullscreen warning, a link from the phone replaces the page, its
own taskbar/media identity, no "open at sign-in": Firefox turns that on by itself once per profile, and the companion
removes such a `Mozilla-Firefox-…` Run value for the per-user Firefox it uses; add-ons placed in the profile's
`extensions` folder are enabled without asking; a page may go fullscreen without a key press). The user signs in to
YouTube in that profile once (keyboard and mouse on the PC).

`sys youtubeOpen` parses the link (`YouTubeLink`: only the video id, playlist and start time are kept), pauses VLC and
runs `firefox.exe -profile <tv-browser> -kiosk <watch url>` (or loads the url into the running profile). The browser
starts behind VLC and on the primary monitor, so the companion waits for its window, moves it to the monitor VLC opens
on (the preferred one, else the one with VLC's window), brings it to the front, waits until the video plays and
presses `f` for YouTube's fullscreen player. Play/pause/seek/next/previous go through Windows' media controls: Firefox
publishes the page's media session (title, channel, position); the TV profile's session is learnt once by matching its
title with the browser's window title and kept in `youTubeSessionId`. `close` sends `WM_CLOSE` to the profile's windows;
so does VLC starting something from the phone (`cmd open`, `launch`, `play` with a `plId`), so that the browser does not
cover it.

### Setting it up, blocking ads

The setup window's "YouTube on the TV" item shows the browser found; without one, **Install Firefox** downloads the
latest release from Mozilla's download service in Windows' language (`FirefoxDownload`), checks that it carries a valid
Authenticode signature of Mozilla Corporation (`WinVerifyTrust` + the signer) and installs it silently for the user into
`%LOCALAPPDATA%\Mozilla Firefox` (no shortcuts, no maintenance service); the `youtube` cap appears without a restart.
**Sign in to YouTube…** opens the TV profile in a normal window on the PC's screen at Google's sign-in (the user
accepts Firefox's terms there if it asks).

**Block ads on YouTube** (opt-in, the setup window or `sys youtubeAdBlock` from the phone, `blockAds` in
`settings.json`): `TvAddons` downloads uBlock Origin from addons.mozilla.org once (checked to be a Mozilla-signed
`uBlock0@raymondhill.net`, kept in `tv-addons\` in the data folder; it is not shipped with the companion) and puts it
into the profile's `extensions` folder before the browser starts, only when it is not there, so the version Firefox
updated it to stays; turning it off removes it. An open TV browser is restarted at once and opens the same video at the
same position. **Skip sponsor segments** works the same way with SponsorBlock (`sponsorBlocker@ajay.app`, LGPL-3.0,
`sys youtubeSponsorBlock`, `skipSponsors`; it asks sponsor.ajay.app for each video's segments) — both are `TvAddon`s.
The TV extension closes other add-ons' own pages (SponsorBlock's welcome page at its first start), so that the TV
keeps showing YouTube.

### The TV extension

`companion/tv-extension/src` is a small Firefox extension, *Sofabird TV* (`tv@sofabird.app`): its content script reads
YouTube's player on watch pages (`#movie_player` through `wrappedJSObject`: the video id, title, channel, position,
duration, playing, an ad — `ad-showing` — and its "Skip" button, next/previous) and carries out commands with the
player's own API (`playVideo`, `pauseVideo`, `seekTo`, `nextVideo`, `previousVideo`, the fullscreen and "Skip"
buttons); it also answers YouTube's "Video paused. Continue watching?" so that a video on the TV does not stop by itself.
It reports on every change and the position at most once a second.

- **Signing.** Release Firefox loads only extensions signed by Mozilla: `python scripts/sign_extension.py sign` packs
  `src/`, signs it as an *unlisted* add-on on addons.mozilla.org (not published, signed automatically; keys in
  `AMO_JWT_ISSUER`/`AMO_JWT_SECRET` or `%USERPROFILE%\.sofabird\amo.json`) and saves `tv@sofabird.app.xpi`, which is
  committed. CI runs `sign_extension.py check`: the signed XPI must hold exactly `src/` (AMO re-indents
  `manifest.json`). After changing `src/`, bump its `version` and sign again.
- **Installation.** The XPI is embedded in `Sofabird.exe` and copied into `<tv-browser>\extensions\` before the
  browser starts (`extensions.autoDisableScopes` 14 enables it without a prompt).
- **The link.** Its background script talks to the native messaging host `app.sofabird.tv`: the companion registers
  itself under `HKCU\Software\Mozilla\NativeMessagingHosts` (a manifest in the data folder pointing at
  `Sofabird.exe`, allowed only for `tv@sofabird.app`; removed on uninstall). Firefox starts `Sofabird.exe <manifest>
  tv@sofabird.app`, which relays the messages (4-byte length + JSON) to the running companion over a named pipe of the
  current user (`TvExtensionHost` ↔ `TvExtensionBridge`, one JSON message per line). Nothing goes over the network.
- **Use.** With the extension connected `state.youtube` comes from the page (`ad`, `adSkippable` included), commands go
  to the page, `youtubeOpen` loads the video into the open tab instead of starting `firefox.exe`, fullscreen waits for
  the video itself (not an ad) and needs no key press. Without it (an older profile, the extension not loaded yet) the
  media session and the `f` key are used as before.

## Tray menu

- the VLC status line, the number of connected phones and the Bluetooth state;
- **Pair a phone…** — a borderless window on top of all windows on the VLC screen / the preferred screen (the TV) with
  a 6-digit code and the `host:port` address; the code is valid for 5 minutes, at most 5 attempts (it also opens by
  itself when an unpaired phone tries to connect — see "Pairing");
- **Start VLC**, **Restart VLC with phone control**, **Close VLC**;
- **TV screen ▸** — monitors with friendly names, the check mark is the preferred one (VLC, YouTube and Moonlight open
  on it); a click = move what is on the TV and remember it, as from the phone; **Don't manage the display** — forget it
  (see "TV screen");
- **Setup and permissions…** — the setup window (state re-read on every open);
- **Install Lua pages**, **Start with Windows** (`HKCU\…\Run`), **Keep the PC awake during a movie**;
- **Open log**, **Open settings folder**, **Exit**.

## Where settings and logs are stored

`%AppData%\Sofabird\` (`%LocalAppData%\Sofabird\` is the installation itself, which updates replace). The first start of
Sofabird takes `settings.json`, `history.json` and `library-metadata.json` over from the companion's earlier name,
VLC Remote Companion (`%LocalAppData%\VlcRemote\`), stops and uninstalls it and moves its autostart; phones pair again.

| File | What |
|---|---|
| `settings.json` | ports (`wsPort` 47555, `beaconPort` 47556), `listenAddress`, the VLC path (`vlcPath`), `vlcHttpHost/Port/Password` (the password is generated on first start), `vlcRcPort`, `launchFullscreen`, `vlcExtraArgs`, `mediaRoots`, `preferredDisplayId` / `preferredDisplayName` (the preferred monitor; the old `preferredDisplay` is migrated automatically), `keepAwake`, `mediaStation` (the power plan of a media station, see "Setup and permissions"), `vlcAudioDevice` (the VLC output device chosen from the phone: when it disappears — TV off — and comes back, VLC is switched back to it), `autoInstallLuaPages`, `autoLaunchVlc`, `configureVlc` (vlcrc auto-configuration, `true` by default), `vlcrcPath`, `advertiseDnsSd`, `bluetooth` (the RFCOMM service, `true` by default), `bluetoothRequireAuthentication` (`false` by default, see "Bluetooth"), `installationId`; `setupCompleted` / `setupVersion` (the setup window was completed, for which item-set version), `manageFirewall` (network access was chosen in the setup window: check the firewall at startup, default `true`) |
| `paired.json` | paired devices: token SHA-256, name, id, date |
| `history.json` | watch history and per-folder track choices (see "Watch history"); a corrupt file is renamed to `history.json.corrupt-<time>` |
| `logs\companion-YYYYMMDD.log` | Serilog, daily rotation, 14 files |

The VLC HTTP interface password is never written to the log.

## Setup and permissions

Every permission the companion needs and every change it makes to the system is listed in one window,
**"Setup and permissions"** (`Setup/SetupForm.cs`), with what it is and why it is needed. Nothing changes until the
user presses **Apply**; **Later** closes the window (it opens again at the next start until it is completed).

| Item | What Apply does | Default |
|---|---|---|
| Network access (Windows Firewall) | allow rules for the companion and VLC, **private networks only**; removes rules that block them (one UAC prompt, see below) | on |
| Start with Windows | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (no admin rights) | on |
| Configure VLC for phone control | `configureVlc` + `autoInstallLuaPages`: vlcrc (with a backup copy) and the Lua pages; if VLC is running — after it closes | on |
| Keep the PC awake during a movie | `keepAwake` | on |
| This PC is a media station | `mediaStation`: the active power plan gets lid close action "do nothing", sleep and hibernate "never", **plugged in and on battery** (powrprof, no admin rights); unticking does not restore the old values | on |
| Bluetooth (info only) | nothing: Windows needs no permission; a link opens `ms-settings:bluetooth` to pair the phone | — |

Each item shows its current state (✓ / not set up / ⚠ / ✗ with the reason) and, after Apply, the result. If a
connected network is **public**, the firewall item warns that Windows will not let the phone in there and links to
the network settings (the category is read via `INetworkListManager`; the companion does not change it).

When the window appears by itself: `setupCompleted` is `false`, or `setupVersion` is lower than
`SetupPolicy.CurrentVersion` (Core). The tray item **Setup and permissions…** opens it at any time.

With `mediaStation` on, the tray re-checks the power plan 5 s after the start and every 30 minutes; if Windows (an
update, the user) changed it back, a notification offers to fix it (once per set of changes; a click opens the window).

> **Rule:** every new permission or system change (firewall, registry, files of other programs, power, devices…)
> must be added as an item of this window, with a short "what and why" text in `tools/gen_localization.py`, and
> `SetupPolicy.CurrentVersion` must be bumped so users who completed the window see it once more.

### Firewall

Windows shows its "allow access" prompt the first time a program listens on the network. **Cancel** there silently
creates **block** rules, and block wins over allow — the phone then cannot find the PC. The companion therefore
manages the rules itself:

- **Detection** (no admin rights): COM `HNetCfg.FwPolicy2` (`ComFirewallRuleSource`), only rules bound to
  `Sofabird.exe` / `vlc.exe`. A program is *allowed* when an enabled inbound allow rule for its path covers
  the private profile and TCP (plus UDP for the companion's discovery beacon); it is *blocked* when an enabled inbound
  block rule for it covers the private profile. Block rules for the public profile only (left by "Allow" with only
  "Private networks" ticked) are fine and kept. Decisions are pure functions in Core (`FirewallPlanner`,
  `SetupPolicy`) with unit tests.
- **Applying** needs admin rights, the companion runs without them. It starts **one elevated copy of itself**
  (`Verb=runas`, one UAC prompt):
  `Sofabird.exe --apply-firewall --companion-exe "<path>" [--vlc-exe "<path>"] --data-dir "<dir>"`.
  The helper removes the blocking rules, replaces our own rules for these paths with fresh ones — inbound allow,
  **private profile**, any protocol, names `Sofabird` and `VLC (Sofabird)` (the same as
  `deploy-windows.ps1 -Firewall`), group `Sofabird` — reads the rules again and exits: `0` OK, `2` bad arguments,
  `3` error, `4` rules still not right (e.g. a Group Policy rule). It logs every change to the companion log and never
  starts the tray, the listeners or VLC. Declined UAC → the window says so; Apply can be pressed again.
- **Every start** (when `manageFirewall` is on): if a block rule exists, or the allow rules are gone after the window
  was completed, a tray notification offers the fix; a click opens the window.

Manual equivalent (administrator PowerShell):

```powershell
netsh advfirewall firewall add rule name="Sofabird" dir=in action=allow profile=private program="<path to Sofabird.exe>" enable=yes
netsh advfirewall firewall add rule name="VLC (Sofabird)" dir=in action=allow profile=private program="C:\Program Files\VideoLAN\VLC\vlc.exe" enable=yes
```

## What has been verified on this PC (one monitor, VLC 3.0.23)

A live check with a headless VLC (`-I dummy --vout dummy --aout dummy`, HTTP `127.0.0.1:8099`,
RC `4212`) and the companion on `127.0.0.1:47557` with a separate data folder:

- `/info`, the UDP beacon, `/art` (401 without a token, 200 `image/jpeg` with Bearer), DNS-SD registration;
- `hello` with a wrong token → `err unauthorized` and close; with a token → `welcome` + the first `state`;
- `pause/play/seek/track/volume/mute/chapter/rate/random/playlistRemove/open/launch/quit`,
  `getState/getPlaylist/getTracks/getAudioDevices/browse`, `sys listDisplays/keepAwake/moveToDisplay`;
- monitors: the CCD API sees "L27q-35" (`external`, id `\\?\DISPLAY#LEN66D5#…`); `listDisplays` with `current`/
  `preferred`/`preferredId`, `moveToDisplay` by id and by the old index without VLC (only remembered, no error),
  an unconnected id, `preferredDisplay null`, `state.system.preferredDisplayId`, migration of the old `preferredDisplay`
  in settings.json; a headless VLC opened "by hand" — auto-move waits 10 s for a window and moves nothing;
- current tracks via RC `atrack/strack` (without Lua pages in VLC) and track selection after `open`;
- events `vlcStarted`, `inputChanged`, `vlcExited`; push `state` ~2–3 times per second during playback;
- `LuaPagesInstaller` — only against a temp folder (unit tests), not installed into `%APPDATA%`.
- Watch history (2026-10-13, headless VLC 3.0.23 on `127.0.0.1:18099`, companion on `127.0.0.1:47655` with a temp data
  dir, the 2160p LOTR remux): played from `--start-time 400` for ~11 s with `cmd track audio 2` → `history.json` got
  position 411 and the folder's audio choice; reopened → `resumePrompt` ~60 ms after the input was seen, VLC paused at
  0 and stayed paused, `state.resume.pending`, the folder's audio track applied while paused (current audio 2 after the
  answer); `cmd resume resume` → playing from ~411; a second answer → `ok`, ignored; third open → `pl_forceresume`
  sent to VLC directly ("Play in VLC") → `state.resume` cleared, the resume point (416) kept after a few seconds;
  `getHistory continue/all`, `browse` with `progress`/`finished`, `historyUpdate finished`, `historyUpdate` of a
  non-file URI → `bad_request` in Russian. Next episode and VLC-side track changes — unit tests only.
- Bluetooth (MediaTek adapter, no devices paired or connected, the radio state was not changed): the RFCOMM service
  is published from a regular process without administrator rights (`… advertising on adapter 04:68:74:9A:2B:FC`), the
  `bt` field is present in `/info`, the beacon and `welcome`, `caps` contains `bluetooth`; with `bluetooth: false` the
  field is absent; `--shutdown` removes the service. RFCOMM framing (`StreamLineTransport`) — unit tests.

### To check on the TV laptop

- Setup window: first start with a fresh data dir (`--data-dir`) — the window appears before the listeners when rules
  are missing; Apply → exactly one UAC prompt, rules `Sofabird` / `VLC (Sofabird)` (private profile,
  group "Sofabird") appear, no Windows "allow access" prompt afterwards, the phone connects. Declining UAC → "not
  given" status, Apply again works. With the existing allow rules (current state of the laptop) the window opens
  next to the running companion with ✓ for network access. A block rule created by hand
  (`netsh advfirewall firewall add rule name=test dir=in action=block profile=private program=<companion exe>`) →
  tray notification at the next start, Apply removes it. Text must not be cut at the laptop's and the TV's DPI
  (window moved between them), in English and Russian Windows.

- Monitors (built-in panel + TV over HDMI): names and `kind` in `listDisplays`/the log, the TV's id after turning the TV
  off and on; moving the VLC window (normal, maximized, fullscreen with and without control) and back;
  auto-move of a new window — launch from the phone with `launchFullscreen` (the movie goes straight to the TV) and
  double-clicking a movie; TV off — the window is not touched; duplicate mode; different scaling (DPI) of the panel and the TV.
- `adev` with a real `mmdevice` (with `--aout dummy` the list is empty) and device selection; "default"
  (`id=""`) is not supported by RC — the companion does it via `setvar.json?obj=aout&name=device&val=`,
  i.e. only with the Lua pages installed.
- VLC volume with `--aout dummy` always reads as 0 — check `volume`/`mute` with a real output.
- `displayPower` (`SC_MONITORPOWER`) under Modern Standby, `sleep` (`SetSuspendState`), `shutdown.exe`
  and keep-awake during a movie — deliberately not invoked on this PC.
- System volume via NAudio: reading verified (`system.volume`), writing — not.
- Discovery of `_sofabird._tcp` from the phone (`NsdManager`) and receiving the broadcast beacon through the Wi‑Fi router.
- Launching the GUI VLC from the companion (`--fullscreen`) and restarting a VLC started from Explorer without the API
  (closing the Qt main window, restoring the file from the command line, the notification and clicking it).
- Auto-configuration of the real `%APPDATA%\vlc\vlcrc` and starting VLC from Explorer afterwards (here — only a copy
  in a temp folder).
- The pairing window opening by itself on top of a fullscreen VLC on the TV screen.
- Watch history with the real GUI VLC: VLC's own "continue playback?" bar no longer appears after `qt-continue=0`;
  a movie double-clicked in Explorer with a resume point → paused, then fullscreen on the TV after the phone's answer
  (and after Play pressed in VLC); a movie launched from the phone (`--fullscreen`) → paused fullscreen; the time from
  the start to the pause (a fraction of a second of picture/sound is expected); the next-episode card with a real
  series folder on the share; a track change made in VLC's menu (needs the Lua pages) remembered for the folder.
- Bluetooth with a phone: a paired phone connecting to the service (Android `createRfcommSocketToServiceRecord`),
  `hello`/code window/commands over RFCOMM, latency; a phone paired with "Just Works"; switching Wi‑Fi ↔ Bluetooth;
  turning Bluetooth off/on on the laptop while running and restoring the service after sleep (Modern Standby).

## Deferred

- **Library with posters** (next): media folders in the setup window, file name parsing, TMDB metadata and artwork.
- TLS with pinning, HDMI-CEC.
