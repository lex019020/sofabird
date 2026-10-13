# Live check of the VLC 3.0.23 interfaces (Windows 11)

Date: 2026-10-12. Checked on this PC: VLC 3.0.23 Vetinari, file —
a UHD BDRemux `.mkv` (1 video, 6 audio tracks, 6 subtitle tracks, 40 chapters).
VLC was started as a separate instance without a GUI; the interfaces listened on localhost only:

```
vlc.exe --no-one-instance -I dummy --dummy-quiet ^
  --extraintf http:rc --http-host 127.0.0.1 --http-port 8099 --http-password probe ^
  --rc-host 127.0.0.1:4212 --rc-quiet --vout dummy --aout mmdevice --gain 0 ^
  --start-time 900 "D:\shared\<file>.mkv"
```

The raw responses are in [samples/](samples/). The source of `httprequests.lua` from the 3.0.x branch is
[samples/httprequests-3.0.x.lua](samples/httprequests-3.0.x.lua).

## Main result: track IDs

`status.json` does not return track IDs, while the `audio_track`, `subtitle_track`, `video_track` commands
require an ID (`vlc.var.set(input, "audio-es", val)`). It was verified experimentally that
**the number `N` in the `"Stream N"` key inside `information.category` is exactly that ID**:

| HTTP action | Result according to RC `atrack`/`strack` |
|---|---|
| `command=audio_track&val=2` | the asterisk moved to `2 - MVO Позитив-Мультимедиа … [Russian]` (a Russian dub track name) |
| `command=subtitle_track&val=7` | `7 - Мосфильм / SRT - [Russian]` (a Russian "Mosfilm" subtitle track) selected |
| `command=subtitle_track&val=-1` | subtitles disabled (`-1 - Disable *`) |

Numbering is continuous across all types: `Stream 0` is video, `Stream 1..6` audio, `Stream 7..12`
subtitles. That is, **the list of tracks with IDs and languages can be obtained from plain HTTP** by parsing
`information.category` (keys `Type`, `Language`, `Description`, `Codec`).

What HTTP does not return: **which track is currently selected**. Only RC knows that (`atrack` without
an argument marks the current one with `*`). Options: track the selection on the client side (optimistically),
or get it via the companion / our own Lua page.

## Transport details of the HTTP interface

- A `200` response comes as `HTTP/1.1`, `Content-type: text/plain` (even for JSON),
  `Cache-Control: no-cache`. Keep-alive works: curl reused the connection.
- An authorization error is `HTTP/1.0 401`, `WWW-Authenticate: Basic realm="VLC stream"`.
  The user name is empty, the password is `http-password`. Without a password the interface does not respond (this
  is VLC ≥ 2.1 behaviour, not re-checked separately here).
- `status.json` latency on localhost: a stable ~21 ms (5 measurements). Polling every
  500–1000 ms over Wi-Fi looks realistic.
- An unknown command and an unknown hotkey name (`key=bogus-key`) return `200`
  with no sign of an error. The client cannot tell that a command was not accepted — only from
  a change in state.
- The first request right after VLC started returned an empty response (the interface was still coming up).
  A retry on startup is needed.

## `status.json` fields and their quirks

- `volume`: scale 0..512, `256` = 100 %. `volume&val=256` → `256`; `val=%2B10` → `266`.
  The `val=50%25` form (percent, from the README) **did not work** — the value did not change.
  Not checked why; do not rely on percentages.
- `rate&val=1.5` reads back as `1.5015015602112` (VLC quantizes the rate). Compare
  with a tolerance, not exactly.
- `fullscreen`: during playback `true/false` (boolean), in the `stopped` state `0`
  (number). The parser must accept both types.
- In the `stopped` state `information` and `stats` disappear, `currentplid = -1`,
  `length = 0`, `time = 0`, `position = 0`.
- `time` is whole seconds, `position` a fraction 0..1, `length` whole seconds.
- `audiodelay`/`subtitledelay` are seconds (internally multiplied by 1 000 000).
- `information.chapters` is an array of indexes `[0..39]`, `information.chapter` the current one,
  `titles`/`title` likewise. There are no chapter names or timecodes.
- `information.category.meta` holds the container tags (`title`, `filename`, `artwork_url`,
  MKV statistics). `/art` returned a 1.5 MB `image/jpeg` (cover art from the VLC cache).
- `stats` are demuxer/decoder counters; useless for a remote, can be ignored.
- `equalizer` is an empty array `[]` when the equalizer is off (not an object).
- `seek_sec` is the short-jump size from the VLC settings (`short-jump-size`, 10 here).

## Commands checked over HTTP

| Command | Effect |
|---|---|
| `pl_forcepause` / `pl_forceresume` | `state` → `paused` / `playing`, idempotent |
| `pl_pause` from `stopped` | starts the last item (honouring `--start-time`) |
| `pl_stop` | `state: stopped`, fields are cleared (see above) |
| `seek&val=%2B30` | +30 s |
| `rate&val=1.5`, `rate&val=1` | works, see quantization |
| `fullscreen` | toggle; works even with `--vout dummy` |
| `key&val=subtitle-toggle` etc. | `200`; effect not checked |
| `in_enqueue&input=<uri>&name=…` | item `id=4` added; the `name` parameter was ignored (the title was taken from the metadata) |
| `browse.json?uri=file:///` | list of drives `C:\`, `D:\` with `uri`, `type: dir` |
| `browse.json?uri=file:///D:/shared/` | `..` + files; fields `type` (`dir`/`file`), `path`, `uri`, `name`, `size`, `modification_time` (unix), `mode` |

## RC interface (`--extraintf rc --rc-host`)

Full list of commands — [samples/rc-help.txt](samples/rc-help.txt). What RC can do and HTTP cannot:

- `atrack`/`strack`/`vtrack` without an argument: **a list of tracks with IDs, names, language and
  the current-track marker `*`**.
- `adev` without an argument: **a list of Windows audio devices** with IDs of the form
  `{0.0.0.00000000}.{GUID}` and the current one marked `*`; `adev <id>` switches. With `--aout dummy`
  the list is empty, with the real `mmdevice` — all devices (speakers, the monitor's HDMI, etc.).
  This is exactly what is needed for switching "laptop speakers ↔ TV HDMI".
- `achan`: Mono/Stereo/Original/Headphones.
- `vratio`, `vcrop`, `vzoom` — set/get the aspect ratio, crop and zoom.
- `quit` — closes VLC cleanly (checked).
- RC sends asynchronous lines `status change: ( play state: 3 )`, `( audio volume: 266 )`,
  `( new input: … )` — these are push events that HTTP does not have. The format is text, parsed with
  regexes: blocks `+----[ Name ]` … `+----[ end of Name ]`, lines `| id - text - [Lang] *`.
- RC accepts one TCP connection at a time (not checked rigorously, but that is usually the case); several
  clients at once are a problem. The companion must hold a single connection and
  multiplex it.

## Conclusions for the architecture

1. **The "no companion" mode (plain HTTP) covers 90 % of the remote**: play/pause/seek/volume,
   chapters, speed, delays, fullscreen, track list and selection (via `Stream N`),
   file browsing and starting playback, playlist, cover art.
2. What is missing without the companion: the currently selected track, the audio device,
   push events, window/monitor control, starting VLC if it is not running, Bluetooth.
3. The companion covers all of this via RC (tracks with `*`, `adev`, events) plus Win32
   (window, monitors, sleep). Alternatively, part of it can be obtained without the companion with our own
   Lua page in the HTTP interface — checked separately (see `vlc-protocols.md`).

## Experiment: our own Lua page in the HTTP interface

A `tracks.json` file (a Lua template `<?vlc … ?>`) was placed in
`%APPDATA%\vlc\lua\http\requests\` — **with no changes to Program Files and no editing of vlcrc**.
VLC 3.0.23 served it at `GET /requests/tracks.json` on a par with the built-in pages.
Result — [samples/tracks-custom-page.json](samples/tracks-custom-page.json).

> **Correction.** The user folder **replaces** the system one rather than supplementing it: `http.lua`
> takes the first existing folder from `datadir_list("http")` and serves only that one
> (see [vlc-protocols.md § 7](vlc-protocols.md)). In this experiment the built-in
> `/requests/status.json` answered `404`, which explains the "failed" `key=quit` below.
> For level 1 the built-in `requests/*.json` must also be copied into the user folder
> (they are ordinary text Lua templates), and VLC must be restarted: URLs are registered at load time.

What `vlc.var.get_list(obj, name)` + `vlc.var.get(obj, name)` give:

| Object / variable | Result |
|---|---|
| `input` / `audio-es`, `spu-es`, `video-es` | a list of `{id, name}` with `-1 - Disable` and **the currently selected ID** (`current`). Names like `Dub Мосфильм / AC3 5.1 @ 448 kbps - [Russian]` ("Mosfilm" dub) |
| `input` / `chapter` | **chapter names** (`" The Shire"`, …) — these are not in `status.json` |
| `input` / `title` | `Segment 0 [2:58:25]` |
| `aout` / `stereo-mode` | Mono / Original / Stereo / Headphones |
| `vout` / `aspect-ratio`, `crop` | lists of allowed values with labels |
| `vout` / `deinterlace` | Off / Automatic / On |
| `aout` / `device` | **empty** — the list of audio devices is not available through a Lua variable (RC uses the C API `aout_DeviceEnum`) |

A second page, `setvar.json?obj=input&name=audio-es&val=5`, switched the track via `vlc.var.set`
and returned `current: 5`. So our own pages can both read and write.

Other:
- `command=key&val=quit` over HTTP did not close VLC — but the request went to `status.json`, which at that
  moment returned `404` (see the correction above), so the hotkey was not tested. A reliable way to close
  VLC over HTTP is `in_play&input=vlc://quit` (a special playlist item, see vlc-protocols § 6);
  RC `quit` also closes it.
- After the experiments `vlcrc` did not change (`mmdevice-audio-device` was not written).

### Implication for the architecture

An intermediate level appears between "plain HTTP" and "full companion":
**"light companion" = a copy of the built-in `lua\http` folder plus our Lua files in
`%APPDATA%\vlc\lua\http\`**, installed by the setup script or the companion (with a VLC restart). This covers the current track, chapter names,
crop/aspect/deinterlace, but **not** audio devices, window/monitor, Bluetooth or starting VLC —
those need a full companion process.
