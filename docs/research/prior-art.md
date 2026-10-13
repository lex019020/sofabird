# Prior art: existing remotes and libraries for controlling VLC

Date: 2026-10-12. Goal: collect the experience of existing open-source (and a few closed-source) solutions in order to design a modern Android remote for VLC on a Windows PC (home theater: the PC is connected to a TV).

Related documents: [live-probe.md](live-probe.md) (a live check of the VLC 3.0.23 HTTP/RC interfaces on this PC; it is linked below wherever it confirms or refutes assumptions taken from other people's code).

## 0. Method and sources

The repositories were cloned with `git clone --depth 1` into a scratch directory
`...\scratchpad\prior-art\<repo>` (because of the Windows path length limit, cloning went through a short staging directory followed by `mv`; they are convenient to read via the junction `C:\Users\Alexey\AppData\Local\Temp\pa\<repo>`). The code was only read (`cat`/`grep`/`sed`/`find`); nothing from the clones was run. "Last commit" dates come from `git log -1` of the clone; stars/license come from the GitHub API as of the document date.

| Clone (subdirectory) | Repository | Last commit |
|---|---|---|
| `VlcFreemote` | github.com/nicolasbrailo/VlcFreemote | 2021-08-21 |
| `android-vlc-remote` | github.com/samicemalone/android-vlc-remote | 2014-07-08 |
| `VLC-TV-Remote` | github.com/CloneTV/VLC-TV-Remote | 2021-03-07 |
| `RagePeanut-vlc-remote` | github.com/RagePeanut/vlc-remote | 2022-01-27 |
| `node-vlc-client` | github.com/alexandrucancescu/node-vlc-client | 2022-11-09 |
| `aiovlc` | github.com/MartinHjelmare/aiovlc | 2026-10-12 |
| `ha-core` (sparse: `homeassistant/components/vlc_telnet`) | github.com/home-assistant/core | 2026-10-12 |
| `jeena-vlc_remote` | git.jeena.net/jeena/vlc_remote | 2019-12-23 |
| `gambalaya-vlc_remote` | github.com/gambalaya/vlc_remote | 2022-03-18 |
| `rest-vlc` | github.com/timelessnesses/rest-vlc | 2022-06-21 |
| `vlcrc` | github.com/evdlabs/vlcrc | 2024-12-23 |
| `vlc-mcp-server` | github.com/piebro/vlc-mcp-server | 2025-06-11 |
| `vlc-remote-control` | github.com/franciscobmacedo/vlc-remote-control | 2023-09-27 |
| `harbour-vlc_remote` | github.com/Mariusmssj/harbour-vlc_remote | 2018-04-03 |
| `vlc` (sparse: `share/lua/http`, `share/lua/intf`, `NEWS`) | github.com/videolan/vlc (master = 4.0-dev) | 2026-10-12 |
| `_fetched/` | individual files: `httprequests.lua`, `common.lua`, `http.lua`, `requests/README.txt` from the 3.0.x branch; `python_vlc_http/vlc.py`; Unified Remote scripts `Main/VLC/remote_win.lua`, `Main/VLC Web/remote.lua`; HTML of Play Store pages | — |

What could not be verified (marked below as "not verified"):
- `wiki.hobbyistsoftware.com` does not resolve (the "VLC connection troubleshooting" page is unavailable; web.archive.org cannot be read from this environment). Used instead: `hobbyistsoftware.com/vlc`, `/vlc-more`, `/VLCSetup-win-manual`, `/vlc-versions`, `blog.hobbyistsoftware.com/vlc-remote-troubleshooting/`, `/wrong-wifi/`.
- The sources of the Android part of KDE Connect (`MprisPlugin.kt`) could not be downloaded (invent.kde.org returns 403, the GitHub mirror returns 404); the README of the desktop plugin `mpriscontrol` was used instead.
- `code.videolan.org` is behind Anubis protection — the VLC issue tracker was not read.
- Play Store reviews were not parsed (only the description, rating, number of installs, update date).
- Peter Baldwin's original repository (`code.google.com/p/android-vlc-remote`) is dead; the samicemalone fork was studied instead.
- "Remote for VLC" by MetaTiger — no such project was found either on GitHub or in the stores; probably a wrong name.

## 1. Map of VLC protocols (context for the comparison)

### 1.1 HTTP interface (Lua `http`, port 8080)

Source: `vlc/share/lua/intf/http.lua`, `vlc/share/lua/intf/modules/httprequests.lua`, `vlc/share/lua/http/requests/README.txt` (master) and the same files from the 3.0.x branch (`_fetched/`).

- Endpoints: `/requests/status.{xml,json}` (status + commands via `?command=`), `/requests/playlist.{xml,json}`, `/requests/browse.{xml,json}?uri=<file-uri>` (or the deprecated `?dir=`), `/art` and `/art?item=<id>` (cover art), `/requests/vlm*.xml`, and in master also `rd.{xml,json}` (renderer discovery — Chromecast). Web UI: `index.html`, `mobile.html`, `mobile_browse.html`, `mobile_equalizer.html`, `mobile_view.html`.
- Authentication: HTTP Basic, empty user name, password `--http-password`. If no password is set, the Lua interface does not serve the pages at all:
  ```lua
  -- vlc/share/lua/intf/http.lua
  password = vlc.var.inherit(nil,"http-password")
  h = vlc.httpd()
  if password == "" then
      vlc.msg.err("Password unset, insecure web interface disabled")
      vlc.msg.info("Set --http-password on the command line if you want to enable the web interface.")
      p = h:handler("/",nil,nil,callback_nopassword,nil)   -- responds with "Status: 403"
  ```
  Consequence for diagnostics: **403 on `/` = no password set in VLC**, **401 = a password is set but did not match**. The same (since 2.1.0) is described in the wiki `Documentation:Modules/http_intf`: before 2.1 there was a `.hosts` ACL file (403 when accessed from anywhere other than localhost); since 2.1 there is only the password.
- `--http-host` (all interfaces by default) and `--http-port` are core options; `config.host` in Lua is ignored with the message "Pass --http-host=IP ... on the command line instead". I.e. the interface *can* be bound to `127.0.0.1` (Unified Remote "VLC Web" and vlc-mcp-server do this, since their client lives on the same PC), and then the phone will not see it.
- Command errors are not signalled: an unknown command/hotkey → `200` (see live-probe). A page generation error is an HTML `<title>Error loading /requests/...</title>` with code 200 (VlcFreemote recognizes it by text search, see §2.1).
- Semantics of the key commands (`httprequests.lua`, the same in 3.0.x and master):
  - `pl_pause` without `id`: if `stopped` — `vlc.playlist.play()`, otherwise toggle pause; `pl_forcepause`/`pl_forceresume` are idempotent; `pl_play&id=` — `gotoitem(id)`.
  - `seek&val=` — `common.seek`: a `%` suffix → relative/absolute seek by position (`+10%`, `50%`), otherwise time via `common.parsetime` (`+1H:2M`, `-10`, `1000`). URL-encode `+` (`%2B`) and `%` (`%25`). In live-probe `val=50%25` did not work for volume; for seek, the percentage forms are used by VlcFreemote and Unified Remote.
  - `volume&val=` — `common.volume`: `+N`/`-N` relative, otherwise `vlc.volume.set(tostring(value))`; scale 0..512, 256 = 100 % (confirmed by live-probe). The `N%` form is mentioned in the README, but `common.volume` in both the 3.0.x branch and master only handles the `+`/`-` sign — a percentage is passed as is to `vlc.volume.set` (behaviour not verified, better not to use it).
  - `key&val=<hotkey>` — `common.hotkey("key-"..val)` → `vlc.misc.action_id`; hotkeys `audio-track`, `subtitle-track`, `subtitle-toggle`, `aspect-ratio`, `crop`, `chapter-prev/next`, `nav-*`, `subdelay-up/down`, `audiodelay-up/down`, `toggle-fullscreen`, etc. This is exactly how all the old Android remotes "switch tracks" (cyclically).
  - `audio_track`/`subtitle_track`/`video_track&val=<id>` — in 3.0.x `vlc.var.set(vlc.object.input(),"audio-es",val)`; in master `vlc.player.toggle_audio_track(val)`. `status.json` does **not return** track IDs, only `"Stream N"` categories; that `N` == the ES id in VLC 3.0.23 was confirmed experimentally in live-probe. In VLC 4 track identifiers in `libvlc_player` are strings (`"audio/1"`), so a numeric `val` will probably stop working there — not verified.
  - `in_play&input=<uri>&option=<opt>` accepts item options (`noaudio`, `novideo`, in vlc-mcp-server — `sub-track=<id>`); `in_enqueue`; `addsubtitle&val=<uri>`; `aspectratio&val=16:9`; `rate&val=`; `audiodelay`/`subdelay&val=<sec>`; `chapter&val=`, `title&val=`; `snapshot` (the screenshot is saved on the PC and not returned to the client); equalizer `enableeq`, `preamp`, `equalizer&band=`, `setpreset`; `pl_sort`, `pl_random/loop/repeat` (toggle only, no state parameter); `fullscreen` (toggle, only when a vout exists).
  - The README warns explicitly: "`pl_delete` is completly UNSUPPORTED" (and yet all remotes use it).
- `browse`: `dir=""` on Windows returns the list of drives; `dir="X:\.."` is specially mapped to `""`; `uri=file:///` gives the same (live-probe); `~` → home folder. Elements: `type` (`dir`/`file`/`unknown`), `path`, `uri`, `name`, `size`, `modification_time`, `mode`, `..`.
- **Differences between apiversion 3 (VLC 3.0.x) and 4 (master/4.0-dev)**, from the `getstatus` diff:
  - `information.chapters`/`titles` (arrays of indices) are replaced with `chapters_count`/`titles_count` — clients that typed `chapters: number[]` (node-vlc-client `src/Types.ts`) will break on VLC 4.
  - `pl_sd` (services discovery) was removed; `set_renderer`/`unset_renderer` and `rd.json` were added (Chromecast from HTTP).
  - `fullscreen` without a vout is the number `0`, with a vout a boolean (in both 3.0.x and master; RagePeanut already works around this: "fullscreen param is not instantly initialized").
  - In master NEWS: "Support HTTPS in the Lua HTTP interface when --http-cert is set"; the web interface (`share/lua/http`) is kept in 4.0-dev, but the Qt GUI and the Lua API were rewritten (`vlc.player.*` instead of `vlc.input`).
- The built-in `mobile.html` (jQuery UI): play/pause/stop/prev/next, seek ±, fullscreen, position slider, volume slider (`value = volume/5.12`, label `volume/2.56 %`, i.e. the slider maximum = 512), cover art, "Open" (file browser), equalizer, Stream Output. `updateStatus` polling once every 1000 ms (500 ms after a command). **There is no audio/subtitle selection in the web UI** (grep for `audio_track|subtitle_track` in `http/` is empty).

### 1.2 RC/telnet (Lua `cli`, port 4212)

Source: `vlc/share/lua/intf/cli.lua`, `aiovlc/src/aiovlc/model/command.py`, `vlcrc/src/vlcrc/vlc_remote_control.py`, live-probe.

- A text protocol; with a password (`--telnet-password`) — a `Password:` prompt and a `Welcome`/`Wrong password` response (aiovlc `Password.parse_output` looks for these substrings).
- Commands: `status` → three lines `( new input: <uri> )`, `( audio volume: N )`, `( state X )` (aiovlc parses them positionally via `split(" ")`); `get_time`, `get_length`, `info` (blocks `+----[ Category ]` / `| key: value`), `atrack|vtrack|strack [id]` — **a list of tracks with ID, name, language and a `*` on the current one**, `adev` — audio devices, `volume`, `seek`, `add/enqueue/clear/next/prev/random/repeat/loop`, `quit`:
  ```lua
  -- vlc/share/lua/intf/cli.lua
  function atrack(name, client, value)
      if value then
          vlc.player.toggle_audio_track(value)
      else
          client:append("+----[ audio tracks ]")
          local tracks = vlc.player.get_audio_tracks()
          for _, track in ipairs(tracks) do
              local mark = track.selected and "*" or " "
              client:append("|"..mark..tostring(track.id).." - "..track.name)
  ```
- Push events (`status change: ( play state: 3 )` and the like) exist only here.
- Downsides visible from other people's code: a single long-lived TCP connection (HA forum: "unrecoverable broken socket" after a VLC/network restart, HA had to be restarted); vlcrc works around this by opening a **new socket for every command** and doing `shutdown(1)`; on Windows the interface is called `rc` (`--extraintf rc --rc-host=HOST:PORT`), on Linux `oldrc --rc-fake-tty` (vlcrc README).

### 1.3 VLC for Android "Remote access" — why it does not fit

VLC for Android 3.6.0 (January 2025) added "Remote access": a Ktor/Netty server is started inside the *Android app* (`application/remote-access-server/build.gradle`: `ktor-server-netty`, `-websockets`, `-sessions`, `-auth`, `-cors`, `ktor-network-tls-certificates`, bouncycastle), a URL + one-time code are shown, and a browser on another device controls **this phone/tablet** over HTTPS with a self-signed certificate: browsing the media library, starting playback on the device, exchanging files. This is the server part of the `org.videolan.android:remote-access` library, which does not exist in desktop VLC; it has nothing to do with controlling VLC on Windows. It is useful only as a UX reference: OTP pairing, a "how to accept the certificate" page. The docs.videolan.me page `.../more/remoteaccess/index.html` as of the document date contains only headings ("Availability", "How does it work?") with no text.

### 1.4 Unified Remote (closed server, open remote scripts)

Repository `unifiedremote/Remotes` (license not verified):
- `Main/VLC/remote_win.lua` — **keyboard emulation**: `win.switchtowait("vlc.exe")` followed by `keyboard.stroke("space")`; status = the `vlc.exe` window title, polling `timer.interval(actions.update, 500)`; it can launch VLC (`os.start("C:\\Program Files\\VideoLAN\\VLC\\vlc.exe")`). Requires window focus and does not know the state.
- `Main/VLC Web/remote.lua` — HTTP: `GET /requests/status.xml?command=...`, Basic `":"..password`, `settings.prop`: `host: localhost`, `port: 8080`, `password: vlcremote`. Polling every 500 ms (`libs.timer.timeout(update_status, 500)`), `/art` when the title changes, library from `playlist.xml`, seek in percent with the comment "Seeking precision is only 1%", a `seeking` flag while dragging. On failure it shows an instruction dialog: enable Web in Main interfaces, "VLC 2.1+ you MUST specify a password ... 'vlcremote'", restart VLC.

## 2. Android apps

### 2.1 VlcFreemote (nicolasbrailo/VlcFreemote)

- Stack: Java, AndroidX, `minSdk 15`, `targetSdk 30`, no third-party libraries (`HttpURLConnection` + its own XML parser `XmlMogrifier`). License GPL-3.0-or-later. 23★, 17 open issues. Last commit 2021-08-21; F-Droid 1.15 (January 2021). Not on Google Play ("laziness and greed", README).
- Connection: HTTP only, and **XML** at that (`requests/status.xml`, `browse.xml?dir=`, `playlist.xml`); every command is `status.xml?command=...` and the response is parsed as a status.
- PC discovery: a TCP scan of the /24 on 8080 and 22 (`net_utils/ServerScanner.java`): sequentially 1..254 for each IPv4 interface, 100 ms timeout, IPv6 is dropped; a found SSH port is saved "for the future" (launching VLC via SSH was never implemented — a TODO in the README). Manual IP:port entry; servers are remembered in SQLite (`local_settings/RememberedServers.java`: `ip, port, display_name, pass, last_used, last_path`; the password in plain text, `// TODO: Is it worth encrypting this?`).
- Polling/reconciliation: the status comes in the response to any command; periodic polling only when `isPlaying()` — a `postDelayed(..., PERIODIC_VLC_STATUS_UPDATE_DELAY = 2500)` chain (`MainActivity.java:36`). Commands are serialized: one in flight, the rest in a `PriorityQueue` by priority; **low-priority ones (`CanIgnore`: status, volume, seek) do not accumulate — only the latest is kept** (`vlc_connector/RemoteVlc.java`):
  ```java
  private void queueCommand(final VlcCommand cmd) {
      if (cmd.getPriority() == VlcCommand.Priority.CanIgnore) {
          lowPriorityCommand = cmd;      // the slider "spams" — only the last one gets executed
      } else {
          pendingCommands.add(cmd);
      }
  }
  ```
  There are no optimistic updates: the UI is updated from the response.
- Authentication/errors: `Authorization: Basic base64(":"+pass)`; 10 s timeouts; `401` → `onAuthError()` → back to the server screen + toast "The supplied password is not valid"; `IOException` → toast "Could not connect to VLC at %s. Please select a new server."; any non-200/status parse error → `onVlcStatusFetchError()` → also "reconnect". The password dialog is shown when a server is selected (pre-filled with the saved one).
- File browser: the start path is `"/"` (`model/VlcPath.java: VLC_DEFAULT_START_PATH = "/"`), on error → retry with `"/"`, then a fatal `VlcPath_ApplicationError` ("the default VlcPath (/) is not valid on your setup"). **On Windows this is a crash**: issue #21 — stack `VlcPath$2.onContentError ← Cmd_DirList$1.onHttpNotOkResponse ← ...onResponse`, because `browse.xml?dir=/` returns an HTML error; issue #6 "Can't get the directory listing for Win 7"; #22 — crash when connecting to 3.0.23 (author: "looks like it only affects Windows users"). The error is detected by text:
  ```java
  // vlc_connector/Cmd_DirList.java
  if (result.contains("<title>Error loading /requests/browse.xml</title>")) { onHttpNotOkResponse(); return; }
  ...
  case "uri": object.path = value.substring("file://".length()); break;
  ```
  Browser features: folder bookmarks (per server), "already played" (a history of 1000 files, highlighting and manual toggling), a random nested directory, remembering the last path per server.
- Tracks: only cyclic hotkeys, no list:
  ```java
  // vlc_connector/Cmd_CycleAudioTrack.java
  public String getCommandPath() { return "requests/status.xml?command=key&val=audio-track"; }
  // vlc_connector/Cmd_CycleSubtitle.java
  public String getCommandPath() { return "requests/status.xml?command=key&val=subtitle-track"; }
  ```
- Seek: a `SeekBar` 0..100 % → on every `onProgressChanged(fromUser)` it sends `seek&val=<pct>%25` (no debounce — only queue coalescing saves it); "automagic movie-skip" buttons ±0.5 % of the length (`Cmd_JumpRelativePercent(±0.5f)` → `seek&val=+0.50%25`).
- Volume: a 0..100 slider with the conversion `4*percent` / `vlc/4` (`model/VlcStatus.java`) — i.e. "100 %" in the app = 400 = 156 % of the real volume, while 256 (0 dB) ends up at 64 %. An example of a wrong scale.
- Playlist: `playlist.xml` → `leaf` (`id, uri, name, duration`), `pl_play&id`, `pl_delete`, `pl_empty`, `in_enqueue`. A known gap: after `in_enqueue` in the `stopped` state playback does not start (a TODO in `MainActivity.onAddToPlaylistRequested`: VLC "remembers" the last item, and `pl_pause` will start that one).
- Other: a home screen widget, a compact layout, Dutch localization. Missing: notifications, hardware volume buttons (issue #5 FR), sleep timer, WoL.

### 2.2 android-vlc-remote ("Remote for VLC", Peter Baldwin; samicemalone fork)

- Stack: Java, an Eclipse-era project (no Gradle), `minSdk 14`, `targetSdk 19`, version 0.7.5 (versionCode 66), Apache HttpClient classes for Basic. GPL-3.0. 46★, 30 forks, 10 issues. Last commit 2014-07-08. Tested with VLC 1.1–2.2 nightlies. The original (`org.peterbaldwin.client.android.vlcremote`) was on Google Code — dead.
- Connection: HTTP; **`status.json` first, on 404 it falls back to `status.xml`** (`service/StatusService.java: USE_XML_STATUS`, reset when the server changes) — support for VLC 1.x without JSON; `playlist.xml?search=`, `browse.xml?dir=`, `/art`.
- Discovery: `sweep/PortSweeper.java` — HTTP `GET /requests/status.xml` to /24 hosts in "closest to own IP first" order (`delta` 1..127 in both directions), N threads, 1 s timeout; **it stores the response code (200/401/403) and shows the server with a "password required"/"forbidden" icon**; reverse DNS lookup for the name (`address.getHostName()`); reacts to Wi-Fi changes. There is a "Test connection" before adding (`net/ServerConnectionTest.java`: 1 s, toast by 200/401/403). Servers with nicknames, the password is not shown in the list.
- Polling/reconciliation: `fragment/StatusFragment.java` — a 1 s timer → `status().programmatic().get()`; all requests go through `StatusService` on separate `HandlerThread`s (status / command / art / remoteviews), **sequence numbers discard stale responses**, when a command arrives pending status requests are dropped, and for absolute `seek`/`volume` earlier commands of the same type are dropped too; 500 ms after a command a "clean" `status` is issued (`sendMessageDelayed(..., 500)`); `loop`/`repeat` after a toggle wait `DELAY=500` via `AlarmManager`. No optimistic updates (except a local volume cache for the keys).
- Errors: an `ACTION_ERROR` broadcast; after an error, the browser tab is reloaded on the next successful status; known problem #2 — a looping "No files were found" toast every couple of seconds when the server is unreachable.
- File browser: `net/xml/XmlDirectoryContentHandler.java` detects Windows by the first `path` that does not start with `/`, converts `/`→`\`, the Windows root = `""` (list of drives), hides `..` at the root, normalizes `a/../b`; "Libraries" — virtual folders made of several directories (like Windows libraries), "Home" and "Set as Home", "folders first" sorting, MRL via `Uri.fromFile`, images via `fake://`; streaming to the phone via `:sout=#transcode{...}:rtp{sdp=rtsp://0.0.0.0:5554/...}`; `pl_sd` for services discovery.
- Tracks: hotkeys via `command=key&val=` (`model/Hotkeys.java`: `audio-track`, `subtitle-track`, `crop`, `aspect-ratio`, `toggle-fullscreen`, `chapter-prev/next`, `subdelay-up/down`, `audiodelay-up/down`), a configurable panel of 5 buttons; from `status` only the `Stream N` types are read (`Track.addStreamType`) to know whether there are subtitles. Issue #4 "change subtitle track" (choosing a specific track/turning it off) has been open since 2014.
- Seek: a `SeekBar` with `max = length` (seconds), sending `seek&val=<sec>` on `onStopTrackingTouch` (and in `onProgressChanged`); ±`seek_time` buttons (a setting, 10 s by default); DPAD ±3 s with Shift; keyboard (space/s/p/n/f/m).
- Volume: `MAX_VOLUME = 512`, a slider in `VolumeFragment`, hardware keys ±20 (`volume&val=%2B20` or locally `mVolumeLevel±20`), mute remembers the last non-zero value.
- Playlist: `current` from `playlist.xml`, search, deletion, file name parsing (series/movie, regex). Issue #3: the wrong track is highlighted — VLC returns a wrong `name` for some .mkv files; README: before 2.1 `current` was set by name rather than by id.
- Other: a notification with buttons, a widget, pause on an incoming call and auto-resume (`receiver/PhoneStateChangedReceiver.java`), `/art` cover art (the author submitted a patch to VLC 2.1.3 for cover art on Windows, ticket #7607), DVD navigation (`nav-*`), one-tap subtitle/audio delay presets (`DelayPresetButton` → `subdelay&val=`), hiding the DVD tab.

### 2.3 VLC-TV-Remote (CloneTV)

- Stack: Java, Android TV (API 24+), Retrofit/OkHttp. MIT. 14★. Last commit 2021-03-07. The repository **does not contain the sources of the ATV app itself** — only APKs in the releases, the remote module for phone/tablet (`source/remote.phone-tablet`), shared constants and a trakt.tv library.
- Architecture: phone → the ATV app's own JSON API (`cmd/GET_STATUS/`, `cmd/{target}/{opt}/`, `RemoteInterface.java`), ATV app → VLC HTTP (constants `DataTagVlcCommand`: `pl_play`, `in_play`, `pl_pause`, `seek`, `volume`, `fullscreen`; `DataTagVlcStatus`: `currentplid`, `apiversion`, `state`...). I.e. "a remote for a remote".
- Features: "automatic find VLC server in local network" (the method is not visible — not verified), an ambilight background, an extended playlist with posters/descriptions from NFO (Kodi/tinyMediaManager) via the Lua add-on `VLC AddOn MediaPlayList` on the PC ("tested VLC 3.0.8, Lua 5.1.5 32-bit"), watch history, Trakt.tv scrobbling, EPG, devices can be on different networks (routing).
- Conclusion: interesting for the idea of a media library with posters on top of VLC and trakt, but the VLC control code is closed.

### 2.4 Closed-source but important competitors

**VLC Remote (Hobbyist Software, iOS/Android, Rob Jonson).** The author is a co-author of `httprequests.lua` and `status.json`/`browse.json` in VLC itself (file headers `vlc/share/lua/http/requests/status.json`: "Authors: Rob Jonson <rob -at- hobbyistsoftware -dot- com>"); in 2013 on vlc-devel he asked to keep the HTTP interface active without a password and to return a clear error, "the HTTP remote interface is used by a lot of people". iOS: 4.8★ (777 ratings), Android: 4.5★, 50K+ installs, updated 2026-08-12. Features (App Store/Google Play, `/vlc-more`): automatic discovery of VLC on the network, browsing files and external drives, playlist, DVD, subtitles/audio tracks/delays, aspect ratio, equalizer, cover art, a picker for fine adjustment of volume/position, a Today widget. VLC 2.0+, no mention of VLC 4 (not verified). Sleep timer/WoL are absent from the description.
Setup Helper (Windows/Mac): enables the Web interface, sets a password (by default `vlcremote` — then "the remote connects automatically without re-entering it", `/VLCSetup-win-manual`), **edits the Windows Firewall rule** ("if you have a third-party firewall, allow VLC yourself"), and while running announces a Bonjour service (iOS finds computers via Bonjour; without the service the app connects by the saved IP). A "traffic cone" indicator = VLC is running. Manual setup guide: Interfaces → Main interfaces → Web, Lua → Lua HTTP password, restart VLC, firewall "VLC checked for both public and private networks".
Troubleshooting (`blog.hobbyistsoftware.com/vlc-remote-troubleshooting/`, `/wrong-wifi/`): update VLC; run the helper ("much more reliable than manual setup"); the phone and the PC on the same network ("phones can join other nearby networks"); the helper is needed every time → no Bonjour service (enable file/printer sharing or save the computer from "Found Computers"); does not connect after a reboot → the IP changed (DHCP reservation); a router guest network, a 5 GHz network with a different name, a Wi-Fi repeater, hotel Wi-Fi, a neighbour's open network — all of these block devices from talking to each other; first of all, reboot the router.

**VLC Mobile Remote — PC & Mac (Adarsh Urs, Android).** 1M+ installs, 4.1★, updated 2026-09-27. No software on the PC ("or alternatively, install our helper app, VMR Connect"); a "Setup Wizard"; auto-connect on the same Wi-Fi; Wear OS; audio track, subtitles and adding `.srt` (VLC ≥ 2.2), speed, crop, aspect, DVD, gestures, browsing all files on the PC, playlist, online links, frame screenshots, "Home location", favourite folders, widget/notification/lock screen, soft pause on a call, hardware volume buttons, **switching audio output devices**, list sorting, several saved hosts, "Close/Quit VLC from the App". With VMR Connect: mouse/keyboard, **Shutdown/Restart/Sleep of the PC**, browser/Netflix control. Disclaimer: "This App do not support VLC for Android app". VideoLAN forum 2022: "connects to linux vlc but does nothing" — a VLC developer suggests checking the web interface in the phone's browser (the standard first diagnostic step).

## 3. Flutter / React Native: RagePeanut/vlc-remote

- Stack: a monorepo — `vlc-remote-app-flutter` (Dart, Dio, MobX), `vlc-remote-app` (React Native, an unfinished port), `vlc-remote-companion` (Electron + React, on the PC), `vlc-remote-api` (TypeScript, TMDb parsing of file names). No LICENSE file; README: "free to do whatever you want ... even if you want to make money". The project is abandoned (2022-01-27), 0★.
- Connection: HTTP JSON (`status.json`, `playlist.json`, `browse.json?uri=`), Basic `":"+password`, `connectTimeout 5000`:
  ```dart
  // vlc-remote-app-flutter/lib/services/vlc.dart
  static const int MAX_VOLUME = 320;
  static const Duration STATUS_REFRESH_INTERVAL = const Duration(milliseconds: 200);
  static const Duration PLAYLIST_REFRESH_INTERVAL = const Duration(milliseconds: 500);
  ...
  Timer.periodic(PLAYLIST_REFRESH_INTERVAL, (timer) => _playlistRequest());
  Timer.periodic(STATUS_REFRESH_INTERVAL, (timer) => _statusRequest());
  ```
  **Status polling 5 times a second + playlist 2 times a second** — aggressive for Wi-Fi/battery. Any error other than a timeout → `_status.setOff(); _files.reset()` (state reset to "VLC is off").
- Discovery: a **QR code** that the companion shows on the PC screen (`vlc-remote-companion/src/screens/HomeScreen.js`: `ip=...\npw=...\nvp=<vlc port>\ncp=<companion port>\nnm=<name>`), parser `Computer.fromQRData`; or manual entry (IP mask `ip_text_input_formatter.dart`).
- Tracks: hotkeys `key&val=audio-track|subtitle-track|video-track` (buttons `AudioTrackButton`, `SubtitleTrackButton`, `VideoTrackButton`).
- Seek/chapters: `seek&val=+N/-N`, `chapter&val=` with a client-side counter (`chapter+1 < chapterCount`, where `chapterCount = information.chapters.length` — a field that does not exist in VLC 4).
- Volume: the phone's hardware buttons via `volume_watcher` → `VLC.volume((320/maxVolume)*vol)` — maximum 320 (125 %).
- Playing a file: if the `uri` is not in the playlist → `in_enqueue`, then `pl_play&id=<found id>` (it searches `lastWhere(uri == file.uri)`), i.e. it **does not create duplicates**.
- Companion: `open_vlc`/`close_vlc` ("power"), `switch_screen` (move the VLC window to another monitor — relevant for a TV), `extra_info` (a metadata stream).
- Notable details: a workaround for `fullscreen` being `bool|int`; per-computer directory history; TMDb posters and title translations.

## 4. Libraries and integrations

### 4.1 node-vlc-client (TypeScript, MIT, 20★, 2022-11)
- `status.json`/`playlist.json`/`browse.json?dir`/`/art?item`, Basic with username:password, any non-200 → `throw new Error("Request error | Code ...")`. No auto-polling and no discovery (`ServerFinder.ts` is an empty stub `find(ip, ipClass=24, port=8080){}`).
- Tracks — the only library that builds a list from `Stream N`:
  ```ts
  // src/Client.ts
  for(let key of Object.keys(stats.information.category)){
      if(key.substring(0,6)==="Stream"){
          let streamIndex = Number.parseInt(key.substring(7));
          ...
          track.streamIndex = streamIndex;
          switch (track.Type) {
              case "Audio": tracks.audio.push(<AudioTrack>track);break;
              case "Video": tracks.video.push(<VideoTrack>track);break;
              case "Subtitle": tracks.subtitle.push(<SubtitleTrack>track);break;
  ...
  public async setAudioTrack(trackId: number){ await this.sendCommand("audio_track",{val: trackId}); }
  ```
  `Types.ts`: `VlcStream { Type, Language?, Codec? }`, `AudioTrack { Channels, Bits_per_sample, Sample_rate }` (VLC replaces spaces in keys with `_`). It cannot determine the currently selected track.
- Volume: `getVolume = volume/512*100`, `setVolume = 512*v/100` — "100 %" = 200 % of the real volume; a comment in the code honestly says "256 is 100% and 512 is 200%".
- `playFile(..., {wait, timeout})` — after `in_play` it polls the status until the file changes (up to 3 s).

### 4.2 aiovlc + Home Assistant `vlc_telnet` (Python, Apache-2.0, actively maintained)
- Telnet 4212, `asyncio.open_connection`, 10 s timeout, an `asyncio.Lock` per command, filtering of telnet IAC bytes. Parsing is positional/regex-based (`model/command.py`): `Status` → `audio_volume = int(float(output[0].split(" ")[3]))`, `state = output[1].split(" ")[2]`; `Info` → `+----[ ... ]` sections; `Password` → "wrong password"/"welcome".
- HA `media_player.py`: `MAX_VOLUME = 500` (yet another scale), `volume_level = audio_volume/500`; state `playing/paused → PLAYING/PAUSED`, otherwise `IDLE`; `media_position_updated_at` is recorded only when `time` changes; the title from `info` (`title`, `now_playing`, otherwise `filename` with `?authSig=` cut off); mute = remember the volume and set 0; when unreachable — reconnect on every `async_update`, `AuthError` → reload the config entry (reauth flow "Please enter the correct password for host"); `play_media` → `add <url>` (for TTS); `config_flow`: host defaults to `localhost`, port 4212, password required, errors `cannot_connect`/`invalid_auth`.
- The `vlc` component (python-vlc, local) is not in the current HA dev tree — only `vlc_telnet` remains.
- HA forum (`community.home-assistant.io/t/remote-vlc/10951`): the telnet integration is "buggy and frustrating" because of the irrecoverably broken socket → home-made HTTP versions:
  - `gambalaya/vlc_remote` (kingo55 → jambalaya): `status.xml`, `volume/256`, mute = `volume&val=0`/`256` (does not restore the previous level), `TIMEOUT 10`; the README shows that the same is achievable with the stock `rest_command` + `rest` sensor + `universal` media player.
  - `jeena/vlc_remote` (`vlc_http.py`): `requests.get(url, auth=("", password), timeout=1)`, a copy of `vlc_telnet` with the backend swapped.

### 4.3 python-vlc-http (MatejMecka, MIT, 0.0.11 / 2023-04, last push 2024-01)
`status.json`/`browse.json`/`playlist.json`, `requests.get(..., auth=(username,password))` without a timeout; `set_volume(volume)`: `str(int(volume * 320))` — a third "scale" (0..1 → 0..320); the host must include the port (otherwise an exception). Methods `video_track(val)`, `subtitle_track(val)` with no way to get the ID.

### 4.4 rest-vlc (Python, MIT, 2022)
`status.xml`, sync/async; `set_volume(volume, percent)` with `volume*2.56` ("0-512 = 0-200%"); sends non-existent parameters `pl_random&state=True`, `pl_repeat&state=` and a `pl_status` command — VLC ignores them (the author did not account for the toggle semantics). An example of how easy it is to misread the README.

### 4.5 vlcrc (evdlabs, Python, MIT, 2024-12)
The RC interface over TCP: for every command `socket.create_connection(...)`, `sendall(cmd+"\n")`, `shutdown(1)`, reading until close, 1 s timeout — **no long-lived connection** (a solution to the aiovlc problem). Supports `adev` (listing/selecting audio devices), playlist, volume (claimed 0..320 — not verified), CLI.

### 4.6 vlc-mcp-server (piebro, Python, MIT, 2025-06)
Launches VLC with `--extraintf=http --http-host=localhost --http-port=8081 --http-password=...`; commands via `status.xml`, reading `status.json`; volume `256 = 100 %`, when a movie starts it forces `volume&val=256`; **sets subtitles with an option at launch**: `in_play&input=...&option=sub-track=<id>`; gets the subtitle list via `mediainfo` on the PC (not from VLC).

## 5. Web and other UX references

- **franciscobmacedo/vlc-remote-control** (Python, MIT, 2023-09, 21★): a Flask page in the style of the Apple TV Remote (touchpad, large buttons), a QR code with the server URL at startup (`run.py`: `socket.gethostbyname(socket.gethostname())` → `qrcode`), **controls by emulating keystrokes** (`keyboard.send(action)`, `src/app.py`), configurable shortcuts; Windows "not tested". It does not know VLC's state.
- **harbour-vlc_remote** (SailfishOS, QML, 2018): `status.xml` via `XMLHttpRequest` with Basic, `checkConnection.js` distinguishes `401`; tracks come from category names: `tempAudioTracks.push(xmlModel2.get(i).category.slice(-1))` (takes the **last character** of the `Stream N` name, breaks for N ≥ 10), then cyclically `passCommands("audio_track&val=" + audioTracks[audioIndex++])`; for subtitles `-1` (off) is added to the list. An illustration that other authors also use the ID from `Stream N`.
- **KDE Connect, the `mpriscontrol` plugin** (desktop plugin README): an asymmetric protocol — the phone sends `requestPlayerList`, receives a list of MPRIS players (identified only by name), sends `{player, action: "Play"|"Next"|...}`, `setVolume` 0..100, `requestNowPlaying` → metadata on track change or on request; on Android — a notification with buttons and lock-screen controls. Seek/cover art/position interpolation are not described in the README (not verified). As a UX reference: the remote immediately shows "what's playing" and can choose the player; our analogue is choosing the VLC instance/PC.
- **The built-in `mobile.html`** — see §1.1; the main things it lacks: track selection, delays, speed, chapters.

## 6. Summary table

| Project | Stack / license | Activity | Transport | PC discovery | Polling | Tracks | Volume (max) | File browser | Special |
|---|---|---|---|---|---|---|---|---|---|
| VlcFreemote | Java / GPL-3 | 2021 | HTTP XML | TCP scan /24 :8080,:22 | 2.5 s only while playing + response to a command | cycle hotkeys | 400 (scale bug) | `browse.xml?dir=`, start `/` (crash on Windows) | bookmarks, "watched", random folder, queue with coalescing |
| android-vlc-remote | Java / GPL-3 | 2014 | HTTP JSON→XML fallback | HTTP sweep /24 with codes 200/401/403 | 1 s, seq numbers, +500 ms after a command | cycle hotkeys, 5 configurable buttons | 512 | Windows paths, drives, Libraries, Home | notification, widget, pause on a call, DVD nav, streaming to the phone |
| VLC-TV-Remote | Java / MIT (ATV part closed) | 2021 | phone→ATV API→VLC HTTP | "auto" (code closed) | — | — | — | NFO media library | trakt.tv, posters, ambilight |
| RagePeanut (Flutter) | Dart+Electron / no license | 2022 | HTTP JSON | QR from the companion | 200 ms (!) + playlist 500 ms | cycle hotkeys | 320 | `browse.json?uri=`, history | companion: launch/close VLC, switch monitor |
| Hobbyist VLC Remote | closed | 2026 | HTTP (+Setup Helper) | Bonjour (helper), scan, IP | — | track list (how — not visible) | — | yes, external drives | helper fixes the firewall, password `vlcremote`, troubleshooting docs |
| VLC Mobile Remote | closed | 2026 | HTTP (+VMR Connect) | auto-discovery on Wi-Fi, wizard | — | list, adding .srt | hardware buttons | yes, Home, favourites | audio devices, screenshot, quit VLC, Shutdown/Sleep of the PC via helper, Wear OS |
| Unified Remote VLC / VLC Web | Lua scripts / server closed | — | keyboard / HTTP XML localhost | server on the PC | 500 ms | — | — | playlist | instruction dialog on error |
| node-vlc-client | TS / MIT | 2022 | HTTP JSON | none | none | `Stream N` → `audio_track&val` | 512 | `browse.json?dir` | playFile with waiting |
| aiovlc + HA vlc_telnet | Python / Apache-2 | 2026 | telnet | manual | HA poll | none | 500 | none | reauth flow, reconnect |
| vlcrc | Python / MIT | 2024 | RC, socket per command | manual | — | — | 320 (claimed) | — | `adev` |
| vlc-mcp-server | Python / MIT | 2025 | HTTP XML/JSON | localhost | — | `sub-track=` as an `in_play` option | 256 = 100 % | its own root | subtitles via mediainfo |
| harbour-vlc_remote | QML | 2018 | HTTP XML | manual | timer | `Stream N`.slice(-1) → `audio_track` | — | yes | 401 → screen |

## 7. Typical user problems

Collected from the issue trackers of the studied repositories, the VideoLAN forum, the Hobbyist Software documentation, Unified Remote and the HA forum.

1. **A password is mandatory since VLC 2.1.0.** The most widespread cause of "cannot connect" after a VLC update: the HTTP interface does not start without a password (`http.lua`: "Password unset, insecure web interface disabled", `/` responds with 403). VideoLAN forum 2013–2014: "the new version requires a password", "VLC 2.1.x is incompatible with the remote — install an older version" (a reply from the support of one of the remotes), the VLC developer's answer — "set a password in the preferences". Unified Remote and Hobbyist hard-coded the default password `vlcremote` into their instructions.
2. **The Web interface is not enabled / "did not get enabled after an update".** Reinstalling VLC can reset `vlcrc` or `Main interfaces`; changes take effect only after a full restart of VLC (all instructions stress this). Hobbyist directly advises "run the helper again".
3. **Windows Firewall / network profile.** On the first launch with Web enabled Windows asks for permission — users click "Cancel", or VLC is allowed only for private networks while the network is marked as "Public". Hobbyist: "VLC checked for both public and private networks"; the helper configures only the built-in firewall, third-party ones (antivirus) must be configured manually. Forum: "turned off the firewall, it didn't help" — i.e. the firewall is not always the cause.
4. **The phone is on a different network**: guest network, 5 GHz with a different SSID and isolation, a Wi-Fi repeater, hotel Wi-Fi, a neighbour's open network, a VPN on the phone (the last one was not found in the documents, not verified). Hobbyist dedicated a separate page `/wrong-wifi/` to this and advises rebooting the router as the first step.
5. **The PC's IP changed after a reboot** (DHCP) — the saved remote "does not see the computer"; Hobbyist's solution is an IP reservation or Bonjour; android-vlc-remote uses reverse DNS for the name and a repeated sweep.
6. **The interface is bound to localhost** (`--http-host=127.0.0.1`) or a non-standard port — the remote on the network does not see VLC; "computer not found" with 8080 open but VLC on another port (samicemalone FAQ). Before 2.1 — the `.hosts` ACL (403 when accessed by IP; the file was overwritten on VLC update).
7. **VLC is not running** — all remotes show this as "no connection"; only Hobbyist distinguishes "computer found" from "VLC running" (traffic cone); VMR/RagePeanut can launch VLC via the companion.
8. **The PC went to sleep** — the remote cannot help without WoL/a companion; no open-source remote implements WoL (VMR Connect can Shutdown/Sleep, but not wake — not verified).
9. **File browsing on Windows**: start path `/` or `~` vs. drives `C:\`, `D:\`; the Windows root = `dir=""`; `..` at drive level; backslashes; `file:///C:/...` vs. `C:\...` — VlcFreemote crashes (#6, #21, #22), fixes in android-vlc-remote (README: "Fix for VLC 2.x not showing the parent (..) entry on Windows"). File names containing a quote do not play (samicemalone #5).
10. **Tracks cannot be selected explicitly** — users ask for subtitle selection/disabling (samicemalone #4, VlcFreemote only cycles); the reason is that `status.json` contains neither IDs nor the current track (see live-probe).
11. **Wrong highlighting of the current playlist item** (samicemalone #3): VLC returned a wrong name/`current` for some .mkv files; before 2.1 `current` was determined by name.
12. **Inconsistent volume scales** (400/320/500/512/256) → "100 % in the remote is too loud/quiet", mute without restore (HA forks).
13. **Stale responses and slider "jitter"**: with polling without sequence numbers the slider jumps back after a seek; without coalescing the slider spams VLC (RagePeanut — 5 requests/s, VlcFreemote — a seek on every movement).
14. **A hung telnet socket** (HA): after a VLC/network restart the connection is not restored without restarting the client.
15. **The remote "connects but does nothing"** (forum, VLC Mobile Remote on Linux 2022): the web interface does not respond even in the phone's browser → the first diagnostic step is to open `http://<ip>:8080` from the phone.
16. **An empty response right after VLC starts** (live-probe) — a retry is needed.
17. **VLC 4**: the web interface is kept, but `apiversion` is 4, `information.chapters/titles` (arrays) and `pl_sd` are removed; the Lua API is rewritten (`vlc.player`), `set_renderer`/`rd.json` are added; string track IDs (not verified). No public statements by Hobbyist/VMR about VLC 4 were found.
18. **Android crash reports** (VlcFreemote #7, #22): `Can't create handler inside thread without Looper` (network callbacks not on the main thread), XML parsing errors for Windows users.
19. **Self-named "VLC Remote"** — trademark conflicts (the samicemalone fork was renamed to "Remote for VLC (Fork)" "to avoid the trademarked "VLC Remote" name"); VLC for Android "Remote access" and remotes for desktop VLC get confused (VMR disclaimer: "do not support VLC for Android app").

## 8. What to take and what to avoid

### Protocol
- **HTTP `status.json` as the main channel** (all working remotes use it); XML is not needed (the fallback to `status.xml` only made sense for VLC 1.x). Handle `fullscreen` as `bool|int`, the absence of `information` in `stopped`, `equalizer: []`.
- **The response to a command = a fresh status** (that is how `status.json?command=` itself works): use it to update the UI immediately instead of a separate request; plus a "clean" `status` ~500 ms after a command (android-vlc-remote, mobile.html) to catch up with asynchronous changes (length/metadata are not filled in immediately).
- **Poll every 1 s in the active state, less often in `stopped`/in the background** (android-vlc-remote 1 s, mobile.html 1 s, Unified Remote 0.5 s); **not** 200 ms (RagePeanut). Interpolate the position between polls locally from `time` + a monotonic clock (as HA does via `media_position_updated_at`) rather than poking VLC more often.
- **Request sequence numbers and discarding stale responses; coalescing "ignorable" commands (seek/volume/status)** — the best ideas of android-vlc-remote (`StatusService`) and VlcFreemote (`RemoteVlc.queueCommand`). One request in flight per type; the slider sends `seek` on release + throttling while dragging.
- **Optimistic updates only for toggle states** (pause/fullscreen/random) with confirmation from the response; for seek — show the target position until the status arrives, with a `seeking` flag (Unified Remote).
- **Idempotent commands**: `pl_forcepause`/`pl_forceresume` instead of `pl_pause` where predictability matters (a double tap must not "un-pause"); `pl_pause&id=` to start from `stopped`.
- **Volume**: internally 0..512, 256 = 0 dB; in the UI — percentages with the 100 % mark at 256 and an explicit amplification zone >100 % (like VLC). Do not repeat 400/320/500. Mute — remember the level (android-vlc-remote), not `0/256` (the HA fork). Hardware buttons → `volume&val=%2B20`/`-20` (android-vlc-remote) or a percentage step.
- **Seek**: absolute in seconds (`seek&val=<sec>`) on slider release; relative jumps `%2B10`/`-10` and a configurable step (presets 5/10/30/60 s); chapters via `chapter&val=`. Percentage seek (`+0.5%`) is a convenient "skip through the movie length" (VlcFreemote), but its precision is 1 % (Unified Remote's remark).
- **Tracks**: build the list from `information.category["Stream N"]` (`Type`, `Language`, `Description`, `Codec`) and send `audio_track|subtitle_track&val=N` (node-vlc-client, harbour; confirmed by live-probe for 3.0.23), `val=-1` for "subtitles off". Keep the hotkeys `key&val=audio-track` as a fallback/"cycle" (the only way used by all Android remotes). HTTP does not return the current track — store the choice on the client and/or get it via RC/our own Lua page (live-probe). Parse the number with a full `parseInt`, not `slice(-1)`.
- **Subtitles from a file**: `addsubtitle&val=<uri>` (VMR: "requires VLC 2.2.0+"), `in_play&option=sub-track=<id>` at launch (vlc-mcp-server).
- **RC/telnet — only via a companion or strictly "socket per command"** (vlcrc): a long-lived socket breaks (HA), one client at a time. Needed for `atrack *`, `adev` (switching HDMI/speakers — an advertised VMR feature), push events, `quit`.
- **Do not rely on `pl_delete`** without verification (README: "completly UNSUPPORTED"), on `pl_sd` (removed in 4.0), on `information.chapters`/`titles` (removed in 4.0) — read `chapters_count` if present, otherwise the array.
- **Diagnostics by codes**: `403` on `/` → "no password set in VLC", `401` → "wrong password", connection refused → "the web interface is off or on another port", timeout → "firewall/different network/PC asleep", empty response → "VLC has just started, retry". Show the user *different* messages (android-vlc-remote distinguishes 200/401/403 already at the scanning stage; VlcFreemote reduces everything to "select a new server" — bad).
- **VLC 4 readiness**: branch on `apiversion` (3 vs 4), do not crash on missing fields; re-check track selection on 4.x (string IDs).

### Discovery and connection
- Combine: (1) a saved IP + host name (reverse DNS, like android-vlc-remote; a `.local`/NetBIOS name — not verified), (2) a parallel HTTP sweep of the `/24` "from own IP outwards" with a ~1 s timeout, saving the response code (android-vlc-remote), rather than a sequential TCP scan at 100 ms each (VlcFreemote, ~25 s per /24), (3) manual entry, (4) a QR code/pairing code from the companion (RagePeanut, VLC for Android Remote access). mDNS/Bonjour is useless without a companion — VLC does not announce anything (Hobbyist announces via the helper).
- A "Test connection" before saving with a human-readable result (android-vlc-remote), a "PC found / VLC running" indicator (Hobbyist traffic cone).
- Do not store the password in plaintext SQLite (VlcFreemote `COLUMN_PASS` with a TODO) — use EncryptedSharedPreferences/Keystore.
- A setup wizard in the app (VMR "Setup Wizard", Hobbyist helper): step by step "enable Web → set a password → restart VLC → firewall (private+public) → check from the phone in a browser", with a "check now" button. Optionally a companion/script on the PC that edits `vlcrc`, the firewall and drops in a Lua page (live-probe showed that `%APPDATA%\vlc\lua\http\requests\` is picked up without administrator rights).

### UX that gets praised
- Auto-connect to the last PC and an instant "what's playing" (all closed-source competitors; KDE Connect).
- File browsing with "Home"/favourite folders (VMR, android-vlc-remote), bookmarks and a "watched" mark (VlcFreemote), "folders first" sorting, remembering the last path per server, not creating playlist duplicates when starting a file (RagePeanut: enqueue only if the `uri` is not there yet).
- Hardware volume buttons, notification/lock screen with buttons, a widget, pause on a call (android-vlc-remote, VMR).
- One-tap subtitle/audio delay presets (android-vlc-remote `DelayPresetButton`), a configurable panel of 5 buttons, aspect/crop, chapters.
- A compact "mini remote" for the phone (VlcFreemote), large buttons and a touchpad in the style of the Apple TV Remote (vlc-remote-control), gestures (VMR).
- For the TV scenario: moving the VLC window to another monitor and launching/closing VLC (RagePeanut companion), Shutdown/Sleep of the PC and switching the audio device (VMR Connect) — all of this requires a companion on the PC.

### What to avoid
- Polling more often than every 1 s and requests without cancelling stale ones (RagePeanut).
- Starting the browser at `/` and a fatal crash on a directory error (VlcFreemote); a text search for `<title>Error loading` instead of codes is acceptable as a heuristic, but the Windows root = `dir=""`/`uri=file:///`.
- A single "cannot connect" message for all classes of errors; looping toasts when there is no network (samicemalone #2).
- Storing the password in plain text; storing an IP without the host name.
- Selecting tracks only with a cyclic hotkey without a list (all Android remotes).
- Depending on fields that do not exist in VLC 4 (`chapters[]`, `titles[]`) and on `pl_sd`.
- Keyboard emulation as the main transport (Unified Remote "VLC", vlc-remote-control): requires window focus and gives no state.
- A long-lived telnet socket from the app without reconnection (HA).
- A custom volume "scale" that does not match VLC's.

## Appendix: links

- VideoLAN wiki: `Documentation:Modules/http_intf` (password since 2.1.0, `.hosts` before 2.1, 401/403), `Control_VLC_from_an_iPhone`, `Control_VLC_from_an_Android_Phone` (list of remotes, 2019).
- vlc-devel 2013-04, "http intf: ensure the password is set" (Rob Jonson).
- Hobbyist Software: `hobbyistsoftware.com/vlc`, `/vlc-more`, `/VLCSetup-win-manual`, `/vlc-versions`, `blog.hobbyistsoftware.com/vlc-remote-troubleshooting/`, `/wrong-wifi/`; App Store id297244048; Google Play `com.hobbyistsoftware.android.vlcremote_us`.
- VLC Mobile Remote: Google Play `adarshurs.android.vlcmobileremote`, `vlcmobileremote.com`; forum.videolan.org p=530687.
- Unified Remote: `help.unifiedremote.com/article/57-configuring-vlc-advanced`, github.com/unifiedremote/Remotes (`Main/VLC`, `Main/VLC Web`).
- Home Assistant: `community.home-assistant.io/t/remote-vlc/10951`, `homeassistant/components/vlc_telnet`, github.com/MartinHjelmare/aiovlc, github.com/gambalaya/vlc_remote, git.jeena.net/jeena/vlc_remote.
- VLC for Android 3.6.0 Remote access: dev.to/npomepuy/vlc-for-android-360-beta-11n4, github.com/videolan/vlc-android `application/remote-access-server`.
- KDE Connect: `lxr.kde.org/source/network/kdeconnect-kde/plugins/mpriscontrol/README`.
- samicemalone FAQ: `samicemalone.co.uk/remote-for-vlc/faq.html` (VLC 2.1+ password, TCP 8080 in the firewall, Home/Libraries).
