# vlc-remote protocol

Protocol version: **1**. This document is the source of truth for the Android app and the companion.

Two levels:

1. **Direct mode (levels 0/1)** — the app talks to the VLC HTTP interface itself. Described in
   [vlc-http.md](vlc-http.md).
2. **Companion mode (level 2)** — the app talks to the companion using NDJSON over
   WebSocket (LAN) or RFCOMM (Bluetooth, milestone M3). Described below and in [messages.schema.json](messages.schema.json).

## Transport

- WebSocket: `ws://<host>:47555/ws`. Text frames, one JSON object per frame. The same NDJSON
  (a trailing `\n`) is allowed and ignored when parsing.
- RFCOMM (Bluetooth Classic): service with UUID **`47fe5bd6-98ad-4d9f-9812-174b3394037d`**, SDP name `VLC Remote`.
  A UTF-8 byte stream, one JSON line per message, `\n` as the separator, at most 1 MiB per line. The same messages,
  handshake and token as over WebSocket. The channel is encrypted by Bluetooth pairing; the token is still required.
- Diagnostics without WebSocket: `GET http://<host>:47555/info` → `{"name","version","protocol":1,"vlc":{...},"wsPort":47555,"needsPairing":bool}` (no authorization, only for display in the list of discovered PCs).

## Discovery

- DNS-SD: service type `_vlcremote._tcp`, name = PC name, port 47555, TXT: `v=1`, `name=<PC name>`, `id=<stable installation GUID>`.
- UDP beacon: the app broadcasts the text `VLCREMOTE_DISCOVER 1` to `255.255.255.255:47556`;
  the companion replies with unicast JSON `{"t":"beacon","name":"...","id":"...","wsPort":47555,"protocol":1,"needsPairing":bool}`.

### Bluetooth

- The companion reports its Bluetooth adapter address in the `bt` field (`"AA:BB:CC:DD:EE:FF"`, upper case, `:`-separated)
  in `/info`, `beacon` and `welcome`. No adapter or Bluetooth turned off — the field is absent.
- A phone that has connected to the PC over LAN remembers `bt` together with the token. If the LAN is unavailable (no Wi-Fi or the PC
  does not respond for ~3 s), and the address is known and the PC is among the phone's paired Bluetooth devices, the phone connects
  over RFCOMM to the service UUID with the same token. When the LAN is available again, it returns to WebSocket (RFCOMM has higher
  latency).
- First connection over Bluetooth only: the user picks the PC from the phone's paired devices
  ("Connect via Bluetooth"), then the usual `hello` → `pairing_required` → code on the PC screen → token.
- Pairing of the Bluetooth devices (phone ↔ laptop) is done by Windows and Android; the companion does not initiate it.

## General message shape

```json
{"t":"<type>", "id":"<string, optional, for matching the response>", ...fields}
```

Responses to requests carry the same `id`. Errors: `{"t":"err","id":"...","code":"<code>","msg":"..."}`.
Codes: `pairing_required`, `unauthorized`, `bad_request`, `vlc_not_running`, `vlc_error`, `unsupported`, `internal`.

## Handshake and pairing

1. Phone → `{"t":"hello","v":1,"device":{"name":"Pixel 8","id":"<uuid>","locale":"ru-RU"},"token":"<token or null>","pairCode":"<6 digits or null>"}`
   `device.locale` (optional) — the phone's UI language (BCP 47); the companion writes `err.msg` in it.
   Without it — in English. The companion's own UI on the PC follows the Windows language.
2. PC → `{"t":"welcome","v":1,"name":"<PC name>","id":"<GUID>","caps":[...],"token":"<new token, only on a successful pairCode>","vlc":{...as in state.vlc}}`
   or an error and the connection is closed:
   - `{"t":"err","code":"pairing_required","msg":"..."}` — the phone is not paired (no token or the token is unknown) and there is no code
     in `hello`. **The companion itself opens a window with the code** on the TV/laptop screen (on top of all windows, with the phone
     name from `device.name`) if there is no active code yet; the phone shows an "Enter the code from the computer screen" field
     and reconnects with `pairCode`.
   - `{"t":"err","code":"unauthorized","msg":"..."}` — the code is wrong or expired (the code window stays/refreshes),
     or the attempts are exhausted.
3. A code lives 5 minutes and 5 attempts; on successful pairing the window closes. The window can also be opened manually from the
   tray menu ("Pair a phone…"). The token is 32 bytes base64url; the PC stores its SHA-256. A repeated `pairing_required`
   from the same phone while the code is active does not create the window again (it only brings it to the front).

Compatibility: old clients that receive `pairing_required` behave as on `unauthorized`.

`caps` — strings: `currentTrack`, `chapterNames`, `audioDevices`, `pushEvents`, `launchVlc`,
`windowControl`, `power`, `systemVolume`, `metadata`, `bluetooth`, `history`, `library`.

## Player commands: `cmd`

`{"t":"cmd","id":"7","name":"<name>","args":{...}}` → response `{"t":"ok","id":"7"}` (or `err`),
and state changes arrive in a separate `state`.

| name | args | VLC equivalent |
|---|---|---|
| `play` | `{"plId": int?}` | `pl_play&id=` / `pl_forceresume` |
| `pause` | — | `pl_forcepause` |
| `togglePause` | — | `pl_pause` |
| `stop` | — | `pl_stop` |
| `next` / `prev` | — | `pl_next` / `pl_previous` |
| `seek` | `{"seconds": int}` absolute or `{"delta": int}` | `seek&val=` |
| `volume` | `{"value": 0..512}` or `{"delta": int}` | `volume&val=` |
| `mute` | `{"on": bool}` | the companion remembers the level, sets 0 / restores it |
| `rate` | `{"value": float}` | `rate&val=` |
| `track` | `{"kind":"audio"|"subtitle"|"video","id": int}` (`-1` = turn off) | `audio_track`/`subtitle_track`/`video_track` |
| `chapter` | `{"index": int}` or `{"delta": ±1}` | `chapter&val=` |
| `title` | `{"index": int}` | `title&val=` |
| `delay` | `{"kind":"audio"|"subtitle","seconds": float}` | `audiodelay`/`subdelay` |
| `fullscreen` | `{"on": bool?}` (no argument — toggle) | `fullscreen` (the companion verifies the state) |
| `aspect` | `{"value": "16:9"|"default"...}` | `aspectratio&val=` |
| `open` | `{"uri": string, "enqueue": bool?, "subtitle": string?, "audioTrack": int?, "subtitleTrack": int?}` | `in_play`/`in_enqueue` (+ `addsubtitle`, track selection after start) |
| `playlistRemove` | `{"plId": int}` | `pl_delete` (unreliable, the companion re-checks) |
| `playlistClear` | — | `pl_empty` |
| `random` / `loop` / `repeat` | `{"on": bool}` | `pl_random`/`pl_loop`/`pl_repeat` (toggle, the companion verifies) |
| `launch` | `{"uri": string?, "display": int?}` | starts `vlc.exe` with the required parameters |
| `quit` | — | RC `quit` or `in_play&input=vlc://quit` |
| `resume` | `{"choice": "resume"\|"restart"}` | answer to the resume prompt (see «Watch history») |
| `dismissNext` | — | hide the «next episode» offer |

## System commands: `sys` (require the corresponding `caps`)

| name | args | cap |
|---|---|---|
| `listDisplays` | — → `{"t":"displays","id":..,"items":[{"index","id","name","kind","primary","current","preferred","bounds"}],"preferredId":string?}` | `windowControl` |
| `moveToDisplay` | `{"displayId": string}` (the old `{"display": int}` is also accepted) | `windowControl` |
| `preferredDisplay` | `{"displayId": string \| null}` | `windowControl` |
| `keepAwake` | `{"on": bool}` | `power` |
| `displayPower` | `{"on": bool}` | `power` |
| `sleep` / `shutdown` | — | `power` |
| `audioDevice` | `{"id": string}` (`""` = default) | `audioDevices` |
| `systemVolume` | `{"value": 0..100}` or `{"delta": int}` or `{"mute": bool}` | `systemVolume` |

Monitors (`listDisplays`):
- `id` — a **stable** monitor identifier (the monitor device path from `DisplayConfigGetDeviceInfo` /
  `monitorDevicePath`; if absent — the GDI device name `\\.\DISPLAYn`). Does not change when the TV is turned off and
  on, unlike `index`. All new commands work by `id`.
- `name` — a friendly name from EDID (`monitorFriendlyDeviceName`, e.g. "LG TV SSCR2"); if empty — the model/
  "Screen N"; `kind` — `internal` (the laptop's built-in panel) or `external` (HDMI/DP/…); `primary` — the primary one.
- `current` — the VLC window is here now; `preferred` — VLC opens here by default (`preferredId` duplicates it).

Companion behaviour:
- `moveToDisplay` moves the VLC window (leave fullscreen → move → restore fullscreen if it was on) **and
  remembers the monitor as preferred**. If VLC is not running — it only remembers, without an error.
- `preferredDisplay` — only remember (`null` — "do not manage": the companion does not touch the VLC window).
- When a new VLC window appears (started by the companion **or manually**, e.g. by double-clicking a movie),
  the companion moves it to the preferred monitor if that monitor is connected (once per window; if the user
  later drags the window themselves, the companion does not move it back). If `launchFullscreen` is on when the companion starts VLC,
  the window goes fullscreen already on the right monitor.
- The preferred monitor is not connected (TV turned off) — the window is left alone.
- In `state.system`: `display` (index of where VLC is now), `displayId` (id of the monitor where VLC is now),
  `preferredDisplayId`.

## Data requests

- `{"t":"browse","id":"3","uri":"file:///D:/shared/"}` → `{"t":"entries","id":"3","uri":"...","items":[{"name","uri","type":"dir"|"file","size":long?,"modified":unixSec?}]}`.
  `uri` = `"file:///"` → list of drives. The `..` element is not returned (the phone builds "up" itself).
- `{"t":"getState","id":"4"}` → the full `state`.
- `{"t":"getPlaylist","id":"5"}` → `{"t":"playlist","id":"5","items":[{"plId","name","uri","durationSec","current":bool}]}`.
- `{"t":"getTracks","id":"6"}` → `{"t":"tracks","id":"6", ...as in state.tracks}`.
- `{"t":"getAudioDevices","id":"8"}` → `{"t":"audioDevices","id":"8","items":[{"id","name","current":bool}]}`.

## State: `state`

The companion sends `state` on changes (no more than 4 times per second; during playback at least once
per second with an updated `time`). Always the full object (not a diff) — simpler and more robust when packets are lost.

```json
{
  "t": "state",
  "vlc": {"running": true, "status": "running", "version": "3.0.23", "apiVersion": 3, "level": 2},
  "playback": {"state": "playing|paused|stopped", "time": 904, "length": 10705, "position": 0.084, "rate": 1.0, "updatedAt": 1760280000123},
  "item": {"plId": 3, "uri": "file:///D:/shared/x.mkv", "title": "The Lord of the Rings", "artUrl": "http://<host>:47555/art?plId=3"},
  "tracks": {
    "audio":    [{"id": 1, "name": "Dub Мосфильм / AC3 5.1", "lang": "Russian", "codec": "a52"}],
    "subtitle": [{"id": 7, "name": "Мосфильм / SRT", "lang": "Russian", "codec": "subt"}],
    "video":    [{"id": 0, "name": "...", "lang": null, "codec": "hevc"}],
    "currentAudio": 4, "currentSubtitle": -1, "currentVideo": 0
  },
  "chapters": {"index": 2, "count": 40, "names": ["Prologue", "..."]},
  "titles": {"index": 0, "count": 1},
  "volume": {"value": 256, "muted": false},
  "video": {"fullscreen": true, "aspect": "default"},
  "delays": {"audio": 0.0, "subtitle": 0.0},
  "flags": {"random": false, "loop": false, "repeat": false},
  "system": {"volume": 60, "muted": false, "audioDevice": "{0.0.0.00000000}.{guid}", "display": 1, "displayId": "\\?\DISPLAY#GSM5B7F#...", "preferredDisplayId": "...", "keepAwake": true}
}
```

(The track names above are real media metadata, "Mosfilm" in Russian; track names are passed through as-is.)

`vlc.status` — the state of VLC on the PC (added within protocol 1, optional; old companions do not send it):

| status | Meaning | `running` | What the phone shows |
|---|---|---|---|
| `notInstalled` | VLC not found on the PC | false | "VLC was not found on the computer" |
| `notRunning` | no process | false | "VLC is not running" + a "Launch VLC" button (`cmd launch`) |
| `starting` | being started by the companion | false | a "VLC is starting…" indicator |
| `noControl` | the process exists, but the web interface does not respond with the companion's password (VLC was opened manually before the companion configured it) | false | "VLC is open without phone control" + a "Restart with control" button (`cmd launch`) |
| `running` | full control | true | the remote |

`running` still means "can be controlled". The companion tries to keep `noControl` from happening: while VLC is not
running, it writes the web interface and RC with its own password into `%APPDATA%\vlc\vlcrc`, so VLC opened
in any way is controllable right away. `cmd launch` in `noControl` restarts VLC with the same file (the position is lost).

The fields `tracks.current*`, `chapters.names`, `system` may be absent if the level/`caps` do not provide them.
`artUrl` is served by the companion (it proxies VLC's `/art`); authorization is the `Authorization: Bearer <token>` header.

## Watch history and resume (capability `history`)

Product decisions (user, 2026-10-13): ask on the phone whether to resume; with no phone or no answer VLC waits paused;
resume is offered after 5 minutes watched; "finished" = less than 5% left; audio/subtitle choices are remembered per
folder; the next episode is only *offered* on the phone, never auto-played; history is kept for 6 months.

**Storage (companion = source of truth).** The companion records every input VLC plays — launched from a phone or
opened manually — in `history.json` in its data dir (atomic writes, debounced). One entry per file:

| field | meaning |
|---|---|
| `uri` | normalized file URI (key; drive letter lower-case, percent-encoding normalized) |
| `name`, `size` | file name and size — a secondary key, so an entry survives a path change (`D:\shared\x.mkv` ↔ `\\host\shared\x.mkv`) |
| `title` | VLC's title (metadata or file name) |
| `folderUri` | parent folder URI |
| `position`, `length` | seconds; `position` is the resume point (0 when finished) |
| `finished` | `true` once less than 5% was left (or set manually) |
| `lastWatched` | unix ms of the last playback |
| `playCount` | number of opens |

Per-folder track preferences in the same file: `{folderUri: {audio: {lang, name, codec}, subtitle: {lang, name, codec} | "off"}}`.
They are written whenever the user picks a track (from the phone or in VLC) and applied to every new file of that
folder by best match (language → name → codec; subtitles "off" stays off). Track ids are per file and are not stored.

Recording: the position is saved every 10 s while playing and on pause, stop, input change and VLC exit. Entries
older than 183 days (`lastWatched`) are deleted at startup and once a day.

**Resume prompt.** When a new input starts and its entry has `position >= 300` and `finished == false`:
1. the companion pauses VLC right away (before applying the folder track preferences, which happens at the same time);
2. `state.resume = {"uri", "title", "position", "length", "pending": true}` and `event {"name":"resumePrompt", ...same}`;
3. every connected phone shows a modal choice «Continue from 1:23:45» / «From the beginning»; the first answer wins:
   `cmd resume {"choice": "resume"|"restart"}` → the companion seeks (or stays at 0), plays, and clears `state.resume`;
4. no phone / no answer → VLC stays paused indefinitely; a phone connecting later sees `state.resume.pending` and shows
   the choice; if the user presses Play in VLC itself, the prompt is cleared and playback continues from the current
   position (the beginning).
VLC's own "continue playback?" feature is disabled by the companion (`qt-continue=0` in `vlcrc`) to avoid a double
question.

**Next episode.** When an input becomes `finished` (or ends) and its folder contains a next video file (natural sort
order of video files in the same folder) that is not finished, the companion sets `state.next = {"uri", "title"}` and
sends `event {"name":"nextEpisode", ...}`. The phone shows a card «Next: <title>» with «Play» (`cmd open {uri}`) and
«Dismiss» (`cmd dismissNext`). Nothing is played automatically. `state.next` is cleared when another input starts.

**Requests.**
- `{"t":"getHistory","id":"9","filter":"continue"|"all","limit":50,"offset":0}` → `{"t":"history","id":"9","total":N,
  "items":[{"uri","title","name","folderUri","position","length","progress","finished","lastWatched","playCount","artUrl"?}]}`;
  `continue` = not finished and `position >= 300`, plus for each series folder the next unfinished episode after the last
  finished one (marked `"next": true`); sorted by `lastWatched` descending.
- `{"t":"historyUpdate","id":"10","uri":"...","finished":true|false}` (mark watched / unwatched; unwatched resets
  `position` to 0) or `{"t":"historyUpdate","id":"10","uri":"...","remove":true}` → `ok`.
- `{"t":"historyClear","id":"11"}` → `ok`.
- `entries` items of `browse` get optional `progress` (0..1) and `finished` for files that are in the history.
- `state.history = {"revision": n}` changes whenever the history changes, so phones can refresh lists.

## Library (capability `library`)

The movies and series of the **media folders** chosen in the companion's setup window (local disks or network
shares). The companion walks them (at the start, every 30 minutes and when the folders change), recognizes titles
from the file names (release-style names: `The.Matrix.1999.2160p….mkv`, `Show.S01E02….mkv`, `Show/Season 2/E05.mkv`)
and groups them: a series is every episode with the same title, wherever its files are; two files of the same movie
are its versions. Metadata comes from **TMDB** in the PC's Windows language (title, year, overview, poster, backdrop,
rating; episode names and stills), kept by the companion for at most 6 months (TMDB terms), so the phones only show
it. Images are full `https://image.tmdb.org/…` URLs: the phone loads them itself (they are not sent over Bluetooth).
Without a TMDB token or a match the item keeps the title parsed from its file name and has no images.

- `{"t":"getLibrary","id":"5"}` → `{"t":"library","id":"5","revision":12,"scanning":false,"movies":[…],"shows":[…]}`.
  A movie: `{"key","title","year"?,"overview"?,"poster"?,"backdrop"?,"rating"?,"files":[file…]}` (several files =
  versions, the biggest first). A series: the same fields without `files`, plus `"episodes": n, "unwatched": n`.
  A file: `{"uri","name","size","progress"?,"finished"?}` (`progress`/`finished` from the watch history).
- `{"t":"getShow","id":"6","key":"<series key>"}` → `{"t":"show","id":"6","show":{series},"episodes":[{"season",
  "episode","title"?,"overview"?,"still"?,"file":{file}}…]}` by season and episode; an unknown key → `bad_request`.
  The episodes are a separate request so that `library` stays small enough for one Bluetooth line (≤ 1 MiB).
- `state.library = {"revision": n, "scanning": bool}`: the revision changes when files appear or disappear and when
  metadata arrives (at most every 3 s while posters are being fetched); phones re-read the library when it changes.
- Playing goes through the usual commands: `cmd open {"uri"}` (a movie's first file, an episode).

## Events: `event`

`{"t":"event","name":"vlcStarted"|"vlcExited"|"inputChanged"|"endReached"|"paired"|"resumePrompt"|"nextEpisode", ...}`.

## Keep-alive

`{"t":"ping"}` ↔ `{"t":"pong"}`; the phone sends it every 30 s over WS and every 15 s over RFCOMM. No `pong`
within 10 s → reconnect.
