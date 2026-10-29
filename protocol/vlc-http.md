# The VLC 3.0.x HTTP interface (levels 0/1)

> Since 0.5 the phone no longer talks to this interface: the app's direct mode was removed (the owner's decision,
> 2026-10-29) and the phone works only through the companion. The companion still drives VLC through it, so this
> digest stays for the companion's side (`VlcManager`, the Lua pages).

A short digest for the implementation. Details and links to the sources —
[docs/research/vlc-protocols.md](../docs/research/vlc-protocols.md), live measurements —
[docs/research/live-probe.md](../docs/research/live-probe.md), response samples —
[docs/research/samples/](../docs/research/samples/).

## Connection

- `http://<host>:8080/requests/status.json` (+ `playlist.json`, `browse.json`), Basic authorization,
  **empty user name**, password = VLC's `http-password`. `/art` — artwork of the current item.
- Response `200` as `HTTP/1.1`, `Content-type: text/plain` (it is JSON), `Cache-Control: no-cache`.
  Keep-alive only if the client **does not send a `Connection` header** (OkHttp adds
  `Keep-Alive` — remove it with a network interceptor). Server idle timeout is 10 s.
- `401` + `WWW-Authenticate: Basic realm="VLC stream"` — wrong password (or a sign of VLC when scanning).
  `403` on `/` — no password set in VLC. Connection refused — the interface is off/wrong port.
  Timeout — firewall/different network/PC asleep. Empty response — VLC is just starting (retry).
- Lua errors arrive as `200` with an HTML body containing `<title>Error loading` — treat as an error.
- The server is single-threaded: one request in flight per type (status/playlist/browse), coalesce the rest.
- URL-encode parameters; `+` is **not** decoded as a space (only `%XX`); `+` in `seek`/`volume` → `%2B`.

## `status.json` (apiversion 3)

| Field | Type | Note |
|---|---|---|
| `state` | `"playing"|"paused"|"stopped"` | |
| `time`, `length` | int, seconds | 0 in `stopped` |
| `position` | float 0..1 | |
| `rate` | float | VLC quantizes it (`1.5` → `1.5015`) |
| `volume` | int 0..512 | 256 = 100 %; mute is not reported |
| `fullscreen` | **bool or 0** | `0` when there is no vout (stopped) |
| `currentplid` | int | `-1` in stopped |
| `audiodelay`, `subtitledelay` | float, seconds | |
| `aspectratio` | string | `"default"` or `"16:9"` |
| `random`, `loop`, `repeat` | bool | |
| `apiversion`, `version` | int, string | branch on `apiversion` (4 — VLC 4) |
| `seek_sec` | int | short jump step from VLC settings |
| `equalizer` | `[]` or object | |
| `information` | object | **absent** in `stopped` |
| `information.chapter`, `.title` | int | current ones |
| `information.chapters`, `.titles` | int[] | indices, no names (in VLC 4 — `chapters_count`) |
| `information.category.meta` | object | `title`, `filename`, `artwork_url`, container tags |
| `information.category["Stream N"]` | object | `Type` (`Video|Audio|Subtitle`), `Language`, `Description`, `Codec`, `Original_ID`?, … |

**Track ID** for `audio_track`/`subtitle_track`/`video_track` = `Original_ID` if it is present in
`Stream N`, otherwise `N`. Category key names are localized — recognize them by content
(presence of `Type`), not by the word `Stream`. The current track is **not reported** in `status.json`
(level 1 — `tracks.json`, see below; level 0 — remember the last selection, reset it when `currentplid` changes).

## Commands (`status.json?command=…`)

`pl_play[&id]`, `pl_pause[&id]` (toggle; starts playback from stopped), `pl_forcepause`, `pl_forceresume`,
`pl_stop`, `pl_next`, `pl_previous`, `pl_delete&id` (unreliable), `pl_empty`, `pl_random`, `pl_loop`,
`pl_repeat` (all three — toggle), `fullscreen` (toggle), `volume&val=<0..512|%2BN|-N>` (the percent form
does not work), `seek&val=<sec|%2BN|-N|N%25>`, `rate&val=`, `audiodelay&val=`, `subdelay&val=`,
`aspectratio&val=`, `title&val=`, `chapter&val=`, `audio_track&val=`, `video_track&val=`,
`subtitle_track&val=` (`-1` — turn off), `in_play&input=<uri>[&option=…]`, `in_enqueue&input=<uri>`,
`addsubtitle&val=<uri>`, `key&val=<hotkey without the key- prefix>` (e.g. `nav-up`, `subtitle-toggle`,
`jump-short-forward`; returns no errors), `snapshot`.
Close VLC: `in_play&input=vlc://quit`. The response to any command is a fresh `status.json`.

## `playlist.json`

A tree: root `{"type":"node","id":"0","children":[…]}`, the `Playlist` node (`id` `"1"`) contains
leaves `{"type":"leaf","id":"3","name","uri","duration":int,"current":"current"?}`. `id` values are strings.
Ignore the `Media Library` node.

## `browse.json?uri=<file URI>`

`{"element":[{"type":"dir"|"file"|"unknown","name","path","uri","size","modification_time",…}]}`.
`uri=file:///` → drives (`C:\`, `D:\`). The first element in a folder is `..` (skip it, build "up"
yourself). Windows paths with backslashes in `path`, proper URIs in `uri` — use `uri`.

## Level 1: `tracks.json` and `setvar.json`

If the pages from `companion/lua/` are installed in `%APPDATA%\vlc\lua\http\`:

- `GET /requests/tracks.json` → `{"sofabird":1,"audio":{"current":4,"items":[{"id":1,"name":"… - [Russian]"}]},
  "spu":{…},"video":{…},"chapter":{"current":2,"items":[{"id":0,"name":" Prologue"}]},"title":{…},
  "stereo_mode":{…},"aspect":{…},"crop":{…},"zoom":{…},"deinterlace":{…}}`. Fields are `null` if
  the object does not exist. `404` → level 0. Track names contain the language at the end in square brackets.
- `GET /requests/setvar.json?obj=input&name=audio-es&val=5` → `{"ok":true,"current":5}`.

## Polling

1 s while `playing`, 3 s while `paused`/`stopped`; do not poll in the background without a foreground service.
Interpolate the position locally. After a command, apply the response immediately and request a "clean"
status 500 ms later. Number the requests and discard stale responses.
