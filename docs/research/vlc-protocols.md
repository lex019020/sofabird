# VLC 3.0.23 control protocols (Windows 11) — a primary-source reference

Date: 2026-10-12. Goal: design a mobile remote for VLC on a PC.
Target installation: VLC 3.0.23 Vetinari, `C:\Program Files\VideoLAN\VLC` (verified:
`vlc.exe` FileVersion 3.0.23; the local `lua\http\requests\README.txt` is byte-for-byte identical
to the `3.0.x` branch, only the line endings differ).

All `file:line` references point to files of the `3.0.x` branch of the
`videolan/vlc` repository (downloaded 2026-10-12 from `raw.githubusercontent.com`) unless
`master` (= the future VLC 4) is stated. Local copies: `scratchpad/vlc-src/{3.0.x,master}/...`.
Lua file lines were checked against the branch sources; in the installed VLC the same modules
are compiled (`lua\intf\http.luac`, `lua\intf\modules\httprequests.luac`,
`lua\modules\common.luac`), while the pages `lua\http\requests\*.json|xml` and `custom.lua` are
plain text.

The mark **"not verified"** means: the statement is derived from the code but has not been run against
a live VLC. Several facts were additionally verified by a live run of VLC 3.0.23 on this PC
(`-I dummy --extraintf http --http-host 127.0.0.1`); they are marked **"verified live"**.
The neighbouring file `live-probe.md` contains the results of another live run; on one
point (merging the user Lua folder with the system one) it is **wrong**, see §7.1.

---

## 0. Document map

1. Sources and method
2. HTTP interface (Lua `http`): enabling, server, authentication, commands, `status.json`,
   tracks, `playlist.json`, `browse.json`, `/art`, errors, limitations
3. RC interface (`modules/control/oldrc.c`): enabling on Windows, format, commands, pitfalls
4. Hotkey action names and how to trigger them via HTTP and RC
5. VLC 4 differences (`master` branch) and release status
6. Other control paths on Windows (command line, one-instance, `vlc://`, media keys)
7. Extending the HTTP interface with your own Lua pages (`%APPDATA%\vlc\lua\http`)
8. Conclusions for the remote's design

---

## 1. Sources and method

Key files of the `3.0.x` branch:

| Area | File |
|---|---|
| HTTP interface (script) | `share/lua/intf/http.lua`, `share/lua/intf/modules/httprequests.lua`, `share/lua/modules/common.lua`, `share/lua/http/requests/*.json|xml`, `share/lua/http/requests/README.txt` |
| Lua bindings | `modules/lua/vlc.c`, `modules/lua/intf.c`, `modules/lua/libs/{httpd,variables,misc,input,playlist,volume,configuration,strings,equalizer}.c`, `share/lua/README.txt` |
| Core HTTP server | `src/network/httpd.c`, `include/vlc_httpd.h` |
| RC | `modules/control/oldrc.c` (in 3.0.x the file is called `oldrc.c`, module `oldrc`, shortcut `rc`), `modules/control/intromsg.h` |
| Hotkeys | `include/vlc_actions.h`, `src/misc/actions.c` (in 3.0.x instead of `src/config/keys.c`), `modules/control/hotkeys.c`, `bin/vlc.c` |
| Tracks/ES | `src/input/es_out.c`, `src/input/var.c` |
| Audio | `src/audio_output/output.c`, `include/vlc_aout.h`, `modules/audio_output/{mmdevice,directsound}.c` |
| Windows specifics | `src/win32/specific.c`, `modules/control/win_msg.c`, `modules/access/idummy.c`, `modules/gui/qt/main_interface_win32.cpp`, `modules/control/globalhotkeys/win32.c`, `src/win32/dirs.c` |
| Options | `src/libvlc-module.c`, `modules/gui/qt/qt.cpp` |
| VLC 4 | `master`: `share/lua/intf/modules/httprequests.lua`, `share/lua/intf/cli.lua`, `modules/control/cli/{cli,player,playlist}.c`, `modules/lua/libs/input.c`, `share/lua/README.txt`, `NEWS` |

Paths that give 404 in 3.0.x (mentioned in the task): `modules/control/rc.c`
(→ `oldrc.c`), `src/config/keys.c` (→ `src/misc/actions.c`),
`share/lua/intf/modules/common.lua` (→ `share/lua/modules/common.lua`),
`share/lua/http/.hosts` (there is no ACL file in 3.0.x). On `master` there is no `modules/lua/libs/player.c`
(the player bindings live in `modules/lua/libs/input.c`) and no `modules/control/oldrc.c`/`rc.c`
(→ `modules/control/cli/`).

---

## 2. HTTP interface

### 2.1 Enabling and parameters

Module: Lua interface `http` (`modules/lua/vlc.c:96-104`, shortcuts `luahttp`, `http`).
Module options (`vlc.c:98-100`): `http-password` (password), `http-src` (string, page
directory), `http-index` (bool, build directory listings). Host/port are core options:
`http-host` (string, default NULL = all interfaces) and `http-port` (int, **8080**)
(`src/libvlc-module.c:1798-1799`). There is `https-port` 8443 (`:1801`), but the 3.0.x Lua interface
creates only a plain HTTP host (`http.lua:337` → `vlc.httpd()` →
`vlc_http_HostNew`, `modules/lua/libs/httpd.c:82-86`); HTTPS appeared only in `master`
(`NEWS:271`, option `--http-cert`).

Ways to enable it:

```
vlc.exe --extraintf http --http-host 0.0.0.0 --http-port 8080 --http-password secret
```

or in `%APPDATA%\vlc\vlcrc`:

```
extraintf=http
http-host=0.0.0.0
http-port=8080
http-password=secret
```

("Extra interface modules" = `extraintf`; `src/libvlc.c:301-335` parses `extraintf`
and `control`, separator `:`, each module is brought up as `"<name>,none"`.)
In the GUI: Tools → Preferences → All → Interface → Main interfaces → "Web"
and Lua → Lua HTTP → Password. The password is mandatory (see §2.3).

How `--http-src` reaches the script: `modules/lua/intf.c:67-87` builds
`config={http={dir='<src>',no_index=<bool>}}`; `http.lua:281` takes `config.dir`, otherwise
it looks for an `http` directory via `vlc.config.datadir_list` (see §7).

### 2.2 Server transport properties (`src/network/httpd.c`)

| Property | Fact | Reference |
|---|---|---|
| Binding | `net_ListenTCP(hostname, port)`, hostname from `http-host` (NULL → all addresses, IPv4+IPv6) | `httpd.c:958-960` |
| HTTP versions | HTTP/1.0 and 1.1 are parsed (`:1373-1375`, `HTTP/0.9` — `HTTPD_PROTO_HTTP0`) | `httpd.c:1301-1310, 1373-1381` |
| Response status line | For pages (`httpd_FileNew`) `HTTP/1.1 200 OK` (`i_version=1`, `:313-314`); for "handler" responses (`/art`, empty password) `HTTP/1.0 <code>` (`:466-469`) | `httpd.c:302-350, 448-470` |
| Page headers | `Content-type: <mime>`, `Cache-Control: no-cache`, `Content-Length` | `httpd.c:319-320, 347` |
| Keep-alive | The connection stays open only if the request is HTTP/1.1 **and** the client sent no `Connection` header at all. Any `Connection:` sent (including `keep-alive`) → the server replies `Connection: close` and closes. HTTP/1.0 → always closed | `httpd.c:344-345, 1918-1941`; **verified live** (curl without the header: `Connection #0 left intact`; with `Connection: keep-alive`: response `Connection: close`) |
| Idle timeout | 10 s (`vlc_http_HostNew(..., 10)`), disabled only by the hidden variable `http-no-timeout` | `httpd.c:867-869, 954-956, 1738-1747` |
| Threads | One `httpd_HostThread` thread per host; all callbacks (including Lua page execution) run sequentially under `host->lock`. Parallel requests are serialized | `httpd.c:978, 1984` |
| CORS | No `Access-Control-*` headers anywhere (grep) → a browser client from another origin is impossible without a proxy | — |
| gzip, chunked, range | None | — |
| Request limit | Body > buffer → `413 Request Entity Too Large`; a reasonable URL limit is a few KB (the exact number is **not verified**) | `httpd.c:1519` |
| Latency | On localhost ~20 ms per `status.json` (measured in the neighbouring `live-probe.md`); over Wi-Fi 30-100 ms is expected. There are no push notifications — polling only. The stock web UI polls `status.xml` every **1000 ms** (`share/lua/http/js/controllers.js:87`) and every 500 ms after actions (`:128`) | — |

Conclusion for the client: use HTTP/1.1 **without** a `Connection` header (OkHttp does exactly
that; `HttpURLConnection` sends `Connection: keep-alive` — there will be a reconnect on every
request), keep a single connection, poll `status.json` every 500-1000 ms, do not send
requests in parallel (they are serialized anyway).

### 2.3 Authentication

- Scheme: HTTP Basic. `httpdAuthOk(user, pass, "Authorization")` (`httpd.c:1659-1697`):
  if both user and password of the URL are empty — access is free; otherwise `Basic base64(user:pass)` is required,
  both user and pass are compared with `strcmp`. Lua registers pages with `user=nil` → `""`
  (`http.lua:224`, `httpd.c:1067-1068`), so the login is **empty**: `Authorization: Basic base64(":secret")`.
- Failure → `401` + `WWW-Authenticate: Basic realm="VLC stream"` (`httpd.c:1888-1891`);
  unknown URL → `404` (`:1892`).
- Empty password: `http.lua:338-342` — pages are not registered at all, the only
  handler `/` responds with `Status: 403` (`callback_nopassword`, `:109-114`), other URLs → 404.
  In addition, `modules/lua/libs/httpd.c:199,300,270-279` replace any page output with
  an HTML stub "Password for Web interface has not been set" if the password is empty.
  In the log: "Password unset, insecure web interface disabled" (`http.lua:339`).
- The `.hosts` ACL (present in VLC ≤ 2.0) is **absent** in 3.0.x: the file is 404 in the branch, and `http.lua`
  has no handling for it. The only protection is the password and `http-host`.
- `--http-host` in `lua-config` is ignored with an error (`http.lua:329-333`); set it only
  via `--http-host`/`--http-port`.

### 2.4 General request model

Pages (local directory `lua\http\requests\`): `status.json`, `status.xml`,
`playlist.json`, `playlist.xml`, `playlist_jstree.xml` (internal, the README asks not to
rely on it), `browse.json`, `browse.xml`, `vlm.xml`, `vlm_cmd.xml`; plus `/art` (not in
`requests/`). All of them are Lua templates `<?vlc ... ?>` (`http.lua:36-37, 65-104`), re-read
when the file mtime changes (`process()`, `:87-104`).

- MIME by extension (`http.lua:40-51`): **`.json` → `text/plain`** (`:42`), `.xml` → `text/xml`.
  The client must not require `application/json`. **Verified live.**
- Query parsing (`parse_url_request`, `http.lua:248-269`): `k=v` pairs split by `&`, decoding
  `vlc.strings.decode_uri` = only `%XX` (`src/text/url.c`, `vlc_uri_decode`). **`+` is not
  turned into a space** — contrary to the README (`requests/README.txt:12`); encode a space
  as `%20`. A repeated key becomes an array (used for `option=`).
- **Every** `requests/*` page first calls `httprequests.processcommands()`
  (`status.json:31`, `playlist.json:31`, `browse.json:32`, `status.xml:33`, `playlist.xml:84`,
  `browse.xml:32`) — i.e. a command can be sent with any of them and the corresponding
  response obtained at once. The command runs **before** the status is built, but many
  actions are asynchronous (playlist/input), so the response may not yet reflect the effect.
- An unknown `command` is silently ignored; the response is the normal status with code 200.

### 2.5 Commands (`httprequests.lua:71-210`)

Parameters are read from `_GET` (`:73-80`): `input`, `command`, `id` (number, default -1),
`val`, `option` (may repeat), `band`, `name`, `duration`.

| `command=` | Parameters | What it does (code) | Notes |
|---|---|---|---|
| `in_play` | `input=<uri or path>`, `option=...`*, `name=`, `duration=` | `vlc.playlist.add({{path=make_uri(input), options, name, duration}})` (`:85-93`) — adds and starts | `make_uri` passes strings with `://` through as is, a Windows path `C:\x.mkv` → `file:///C:/x.mkv` (`modules/lua/libs/strings.c:79-92`, `src/text/url.c` `vlc_path2uri`). Options like `noaudio`, `novideo`, any `:opt=val` without the colon. According to `live-probe.md`, `name` is ignored |
| `in_enqueue` | same | `vlc.playlist.enqueue` (`:96-97`) — add without starting | |
| `addsubtitle` | `val=<path>` | `vlc.input.add_subtitle(val)` (`:94-95`) — a path (not a URI), `input_AddSlave(SLAVE_TYPE_SPU, ..., autoselect=false)` (`libs/input.c:233-260`) | Without a current input → Lua error → error page (§2.13) |
| `pl_play` | `id=` | `id==-1` → `playlist.play()`, otherwise `gotoitem(id)` (`:98-103`) | `id` = playlist item id from `playlist.json` |
| `pl_pause` | `id=` | if `status()=="stopped"` → play/gotoitem, otherwise `playlist.pause()` = **toggle** (`:104-113`, `playlist_TogglePause`) | |
| `pl_forcepause` | — | pause only if `playing` (`:114-117`) — idempotent | |
| `pl_forceresume` | — | toggle only if `paused` (`:118-121`) — idempotent | |
| `pl_stop` | — | `playlist.stop()` | |
| `pl_next` / `pl_previous` | — | `playlist.next()/prev()` | |
| `pl_delete` | `id=` | `playlist.delete(id)` (`:128-129`) | The README calls it "UNSUPPORTED", but the code is there; works for leaf ids |
| `pl_empty` | — | `playlist.clear()` | |
| `pl_sort` | `val=<key>`, `id=0|1` | `playlist.sort(val, id>0)` (`:132-133`) | Keys per `share/lua/README.txt:325-328`: `id, title, title nodes first, artist, genre, random, duration, title numeric, album` (the page README's list "0 Id, 1 Name…" is outdated) |
| `pl_random` | — | toggle random | |
| `pl_loop` | — | toggle loop; if loop got enabled — disables repeat (`:136-140`) | |
| `pl_repeat` | — | toggle repeat; if it got enabled — disables loop (`:141-145`) | |
| `pl_sd` | `val=<SD name>` | toggle service discovery (`:146-151`) | Removed in `master` |
| `fullscreen` | — | toggle, only if there is a vout (`:152-155`) | |
| `snapshot` | — | `common.snapshot()` → `video-snapshot` on the vout (`common.lua:36-40`) | Not in the README |
| `volume` | `val=` | `common.volume(val)` (`:158-159`) — see §2.6 | |
| `seek` | `val=` | `common.seek(val)` — see §2.6 | |
| `key` | `val=<action name>` | `common.hotkey("key-"..val)` (`:162-163`) — see §2.6 and §4 | Not in the README |
| `audiodelay` | `val=<sec, float>` | `var.set(input,"audio-delay", val*1e6)` only when there is an input (`:164-168`) | Negative values allowed |
| `subdelay` | `val=<sec>` | `var.set(input,"spu-delay", val*1e6)` (`:174-178`) | |
| `rate` | `val=<float ≥ 0>` | `var.set(input,"rate", val)` (`:169-173`) | VLC quantizes: `1.5` reads back as `1.5015…` (`live-probe.md`) |
| `aspectratio` | `val=` | `var.set(vout,"aspect-ratio", val)` (`:179-182`) | Strings `16:9`, `4:3`, …; "default" = empty string; invalid → reset |
| `preamp` | `val=<dB>` | `vlc.equalizer.preampset` | |
| `equalizer` | `band=`, `val=<dB>` | `equalizerset(band, val)` | Bands 0..9 |
| `enableeq` | `val=0|1` | `equalizer.enable(val ~= '0')` (`:189-190`) | |
| `setpreset` | `val=<preset id>` | `equalizer.setpreset(val)` | id from `status.json` → `equalizer.presets` |
| `title` | `val=<index>` | `var.set(input,"title", val)` (`:193-194`) | **0-based index** (`src/input/var.c:331`) |
| `chapter` | `val=<index>` | `var.set(input,"chapter", val)` (`:195-196`) | 0-based (`var.c:395`) |
| `audio_track` | `val=<ES id>` | `var.set(input,"audio-es", val)` (`:197-198`) | §2.9 |
| `video_track` | `val=<ES id>` | `var.set(input,"video-es", val)` (`:199-200`) | §2.9 |
| `subtitle_track` | `val=<ES id>` | `var.set(input,"spu-es", val)` (`:201-202`) | `-1` = disable |

Important: `title/chapter/audio_track/video_track/subtitle_track/addsubtitle` **do not check**
whether there is an input; when `stopped`, `vlc.object.input()` = nil → `luaL_checkudata` fails →
an HTML error page instead of JSON (§2.13). `audiodelay/rate/subdelay/seek` are guarded.

### 2.6 Grammars of `volume`, `seek`, `key`

**volume** (`common.lua:203-209` + `libs/volume.c:48-64`):
- `val=N` — absolute, an integer on the **0..512 scale, 256 = 100 %** (`AOUT_VOLUME_DEFAULT 256`,
  `AOUT_VOLUME_MAX 512`, `include/vlc_aout.h:294-295`); `<0` is clamped to 0 (`volume.c:52-53`).
- `val=+N` / `val=-N` (URL: `%2B10`) — relative to the current value.
- `val=N%` from the README (`:126-129`) — **not implemented**: `vlc.volume.set("50%")` →
  `luaL_checkint` cannot parse the string → Lua error → error page, the volume does not
  change (**verified live** by the neighbouring run: the value did not change). Do not use it; convert
  percentages to `round(p*2.56)` on the client.
- The `volup/voldown` step in RC and the `vol-up/down` hotkey: `volume-step` = 12.8 (`libvlc-module.c:154,1499`)
  = 5 % (the exact behaviour of `playlist_VolumeUp` is **not verified**).

**seek** (`common.lua:150-201`):
- `val=N` — absolute, seconds; `+N`/`-N` — relative (`time ± N`).
- `val=N%`, `+N%`, `-N%` — a fraction of the duration (`position`); decimals use a dot (`us_tonumber`).
- Lettered format: `[+|-][<h>H:][<m>M:][<s>[S]]`, letters `h/H`, `m/M/'`, `s/S/"`
  (`parsetime`, `:152-176`). Pitfall: the patterns are searched across the whole string independently, and seconds
  only as the tail `(%d+)[sS"]?$`; the string `1:30` without letters gives **30 s**, while `90` gives 90 s.
  It is safe to send only whole seconds.
- Without a current input — silently nothing (`:181`).

**key** (`httprequests.lua:162-163`, `common.lua:25-33`, `libs/misc.c:149-156`,
`src/misc/actions.c:587-597`):

```lua
function common.hotkey(arg)
    local id = vlc.misc.action_id( arg )           -- vlc_actions_get_id(arg)
    if id ~= nil then
        vlc.var.set( vlc.object.libvlc(), "key-action", id )
```

`vlc_actions_get_id` **requires the `key-` prefix** and looks up the remainder in the
`s_names2actions` table (`actions.c:264-379`, bsearch). HTTP prepends `key-` itself, so
`val` is the bare name: `val=nav-up`, `val=play-pause`, `val=subtitle-track`. Unknown name →
`action_id` returns nil → `false`, the HTTP response is still 200. The action is executed by the
`hotkeys` module (`modules/control/hotkeys.c`), which `vlc.exe` always brings up
(`bin/vlc.c:240`, `libvlc_add_intf(vlc, "hotkeys,none")`). The list of names is in §4.

### 2.7 `status.json` — fields (`httprequests.lua:458-568`, `getstatus(true)`)

`status.json` calls `getstatus(true)` (`status.json:33`) → includes `information` and
`stats`; `status.xml` calls `getstatus(false)` and appends `<information>` itself
(`status.xml:35-80`, a format with `name` attributes). Below is the JSON variant.

| Key | Type | Units / values | Source | Behaviour when `stopped` |
|---|---|---|---|---|
| `apiversion` | int | `3` | `:470` | always |
| `version` | string | e.g. `"3.0.23 Vetinari"` (`VERSION_MESSAGE`) | `:471`, `libs/misc.c:94-98` | always |
| `volume` | int | 0..512, 256 = 100 % (`lroundf(playlist_VolumeGet*256)`). If the aout has never been created yet, `playlist_VolumeGet` returns `-1` (`src/playlist/aout.c`) → `volume = -256` (**not verified**); after stop the aout usually stays alive and the value is kept (in the live `stopped` sample → `266`) | `:472`, `volume.c:58-64` | always |
| `seek_sec` | int | `short-jump-size` (default 10) | `:473`, `libvlc-module.c:2671` | always |
| `time` | int | seconds, `floor(time_us/1e6)` | `:476` | `0` |
| `position` | float | 0..1 | `:477` | `0` |
| `currentplid` | int | playlist item id | `:478`, `libs/playlist.c:331-344` | `-1` |
| `audiodelay` | float | seconds (internally µs/1e6) | `:479` | `0` |
| `rate` | float | 1.0 = normal | `:480` | `1` |
| `subtitledelay` | float | seconds | `:481` | `0` |
| `length` | int | seconds, `floor(item:duration())`; unknown duration → `-1` (duration = -1 µs → `-0.000001` → floor) (the edge case is **not verified**) | `:491-495`, `libs/input.c:347-352` | `0` |
| `fullscreen` | **bool or 0** | with a vout — `true/false` (bool variable), without a vout — the number `0` | `:497-503` | `0` (number) |
| `aspectratio` | string | the vout's `aspect-ratio` value; empty → `"default"` | `:499-500` | **key absent** |
| `audiofilters` | object | `{"filter_0": "<name>", ...}` — the aout's `audio-filter` split by `:`; with no filters `{"filter_0": ""}` | `:505-514` | absent only if there is no aout object; in the live `stopped` sample the key is present (the aout survives stop) |
| `videoeffects` | object | `hue, brightness, contrast, saturation, gamma` — **from the configuration**, not from the live filter | `:516-521` | always |
| `state` | string | `"playing"` / `"paused"` / `"stopped"` / `"unknown"` (`playlist_Status`) | `:523`, `libs/playlist.c:387-407` | `"stopped"` |
| `random`, `loop`, `repeat` | bool | playlist variables | `:524-526` | always |
| `equalizer` | object or `[]` | `{preamp: float, bands: {...}, presets: [...]}`; if the equalizer is off — `equalizerget()` → an empty table, and dkjson prints an empty table as `[]` (**verified live** by the neighbouring run) | `:528-534`, `libs/equalizer.c:123-170` | same as when playing |
| `equalizer.bands` | object | keys like `"band id=\"0\""` → gain dB (`equalizer.c:164-169`) — ugly, but that's how it is | | |
| `equalizer.presets` | object/array | `{"preset id=\"N\"": "Flat", ...}` (`:280-289`) | | |
| `information.category.meta` | object | item metadata: `filename, title, artist, genre, copyright, album, track_number, description, rating, date, setting, url, language, now_playing, publisher, encoded_by, artwork_url, track_id, track_total, director, season, episode, show_name, actors` + arbitrary extra tags of the container (`libs/input.c:108-171`); empty values — the key is absent | `:536-539` | **no `information`** |
| `information.category["Stream N"]` | object | stream fields, see §2.8 | `:541-550` | no |
| `information.chapter`, `information.title` | int | current indices (0-based) | `:560-561` | no |
| `information.chapters`, `information.titles` | array<int> | **indices only** (the first value returned by `get_list`; the names are lost) | `:563-564` | no |
| `stats` | object | `input_item_t.p_stats` counters, keys without underscores: `readpackets, readbytes, inputbitrate, averageinputbitrate, demuxreadpackets, demuxreadbytes, demuxbitrate, averagedemuxbitrate, demuxcorrupted, demuxdiscontinuity, decodedaudio, decodedvideo, displayedpictures, lostpictures, sentpackets, sentbytes, sendbitrate, playedabuffers, lostabuffers` | `:552-558`, `libs/input.c:190-231` | no |

What `status.json` does **not** have: mute (the aout's `mute` variable is not exported), the currently
selected tracks (`audio-es/spu-es/video-es`), chapter/title names, the list of
audio devices, `can-seek/can-pause`, the name/URI of the current item directly (there are
`meta.filename`/`meta.title` and `currentplid` → look it up in `playlist.json`).

Number format: dkjson (`share/lua/modules/dkjson.lua`); floats are printed as is
(`0.41573333740234`), booleans as `true/false`.

### 2.8 `information.category`: `meta` and `Stream N`

Categories are taken from `input_item_t.pp_categories` (`libs/input.c:73-97`). Keys inside
categories get `_` instead of spaces in JSON (`httprequests.lua:545`:
`string.gsub(k2," ","_")`); category names keep their spaces (`"Stream 0"`).
**All category and key names are gettext strings** (`es_out.c` uses `_("...")`),
i.e. with a Russian VLC interface language they will be "Поток 0", "Кодек", "Язык", "Тип" ("Stream 0", "Codec", "Language", "Type").
The remote must either accept localized keys or recommend `--language=en`
(**not verified** on a Russian locale).

`Stream N` keys (English locale; `src/input/es_out.c`, `EsOutUpdateInfo`):

| Key (JSON) | When | Source |
|---|---|---|
| `Original_ID` | only if `i_meta_id != i_id` (see §2.9) | `es_out.c:3148-3150` |
| `Codec` | always; `"<fourcc> (<description>)"` / description | `:3152-3157` |
| `Language` | if known | `:3159-3161` |
| `Description` | if present (track name from the container) | `:3162-3165` |
| `Bitrate` | if > 0, `"N kb/s"` | `:3167-3169` |
| `Type` | `Audio` / `Video` / `Subtitle` | `:3174`, `:3246`, `:3436` |
| audio: `Channels` (from the ES format), `Sample_rate` (`"48000 Hz"`), `Bits_per_sample`, `Decoded_format`, `Decoded_channels` (from the decoder, e.g. `3F2R/LFE`), replay-gain fields | | `:3176-3243`; `Decoded_channels` is visible in the live sample `samples/status-playing.json` |
| video: `Video_resolution` (`WxH`), `Buffer_dimensions`, `Frame_rate` (`"23.976024"`), `Decoded_format`, `Orientation`, `Color_primaries`, `Color_transfer_function`, `Color_space`, `Chroma_location`, `Projection`, `Yaw/Pitch/Roll`, `Field_of_view`, `Max_luminance`, `MaxCLL`, `MaxFALL` | | `:3245-3433` |

The category is named `_("Stream %d")` from `es->i_meta_id` (`es_out.c:1206-1214`).

### 2.9 Tracks: which IDs `audio_track` accepts and where to get the list

1. **What `val` accepts.** `vlc.var.set(input, "audio-es", val)` → `vlclua_var_set`
   (`libs/variables.c:185-198`) → `vlclua_tovalue` by the variable type: `audio-es` is
   `VLC_VAR_INTEGER` (`src/input/var.c:192`), hence `luaL_checkinteger` — the string `"3"`
   is converted to 3, a non-numeric string → Lua error → error page.
2. **What these numbers are.** This is the **ES id** = `es->i_id` (`es_out.c:1559-1565`):

   ```c
   if( es->fmt.i_id < 0 )  es->fmt.i_id = p_sys->i_id;   // the demuxer gave no id → counter
   es->i_id = es->fmt.i_id;
   es->i_meta_id = p_sys->i_id++;                          // always incremented, shared across all types
   ```

   The choices of the `audio-es` variable are built in `EsOutESVarUpdateGeneric` (`:904-978`): first
   `-1` with the text `Disable` is added (`:936-937`), then `(i_id, text)`, where text =
   `"<Description> - [<Language>]"` or `"Track N - [Language]"` / `"Track N"` (`:942-963`).
   Which `fmt.i_id` the demuxer gives depends on the container (MPEG-TS — PID, MP4 — track ID, and
   so on; **not verified** per demuxer). For MKV in the live run the ids matched the shared
   counter (`Stream N`).
3. **Where to get the list over HTTP.** `status.json` has **no** `audio-es` list. But there are
   `Stream N` categories, where `N = i_meta_id`, and the `Original_ID` key, which appears
   **exactly when** `i_id != i_meta_id` (`es_out.c:3148-3150`). Client algorithm:

   ```
   for each category "Stream N" with Type in {Audio, Subtitle, Video}:
       es_id = category.Original_ID ?? N
       label = category.Description or ("Track …"), category.Language, category.Codec
   ```

   This gives the full list of tracks with valid IDs and languages from plain HTTP (for MKV
   confirmed live by the neighbouring run: `audio_track&val=2` selected `Stream 2`).
   Limitations: category/key names are localized (see §2.8); **the currently selected track
   is not available over HTTP** — only `vlc.var.get(input,"audio-es")` knows it (a custom Lua page,
   §7) or RC `atrack`.
4. `title`/`chapter`: the value is a 0-based **index** (`var.c:331, 395`); the choice texts are
   `"Title N [h:mm:ss]"` / name, `"Chapter N"` / name (`var.c:316-328, 340-352`), they do not get into
   `status.json` (only the array of indices).
5. Cyclic selection without an ID: `key&val=audio-track`, `subtitle-track`, `subtitle-revtrack`,
   `subtitle-toggle` (`hotkeys.c:721-801`).

### 2.10 `playlist.json` (`httprequests.lua:301-377`, `libs/playlist.c:221-270`)

The root is a node. Structure:

```json
{ "type":"node", "id":"1", "name":"Undefined", "ro":"rw", "children":[
   { "type":"node", "id":"2", "name":"Playlist", "ro":"ro", "children":[
       { "type":"leaf", "id":"4", "uri":"file:///D:/x.mkv", "name":"x.mkv",
         "ro":"rw", "duration":3600, "current":"current" } ] },
   { "type":"node", "id":"3", "name":"Media Library", "ro":"ro", "children":[] } ] }
```

- `id` is a **string** (`tostring(item.id)`, `:330, 359`), the playlist item id
  (`playlist_item_t.i_id`); this is what `pl_play&id=`, `pl_delete`, `/art?item=` need.
- `name` — `input_item_GetTitleFbName` (the title from metadata, otherwise the file name) (`playlist.c:244-247`).
- `uri` — the item's `psz_uri` (`:248-249`). `duration` — seconds, `floor`, `-1` if unknown.
- `ro` — `"ro"`/`"rw"` by the `PLAYLIST_RO_FLAG` flag; `disabled` items are skipped (`:323`).
- `current: "current"` only on the current leaf (`:349-356`); otherwise the key is absent.
- `?search=<string>` → `vlc.playlist.search` (`:304-311`), a filter by name; an empty string
  resets it.
- The specific node ids (1/2/3) are typical, **not verified**; rely on `name`.

### 2.11 `browse.json` (`httprequests.lua:379-455`)

- Parameters: `uri=file:///D:/dir/` (preferred; `file://~` = home directory) or
  the legacy `dir=D:\dir`. `uri` is normalized (`common.realpath`), a `/` is appended.
  `dir=` with `X:\..` → empty string → list of drives (`:399-409`); with `uri=file:///`, as
  observed by the neighbouring run, the list of drives `C:\`, `D:\` is returned too.
- Result: `{"element":[ {...}, ... ]}`; element fields = `vlc.net.stat` fields (`share/lua/README.txt:222-230`):
  `type` (`"dir"`, `"file"`, `"unknown"` if stat failed), `mode`, `uid`, `gid`, `size`,
  `access_time`, `modification_time`, `creation_time` (unix seconds) + `path` (local path),
  `name`, `uri` (`file:///...`).
- Hidden (`.`) files are skipped; `..` is included (`:428-429`), for an empty directory it is
  added artificially (`:424-426`). Sorted by name (`table.sort`).
- Local FS only (`vlc.net.opendir`); network/SMB directories only if mounted
  as a path (UNC `\\host\share` → `make_uri` gives `file://host/share/...`, `url.c:41-46`;
  **not verified**).

### 2.12 `/art` (`http.lua:155-196, 344`)

- `GET /art` — the current item's cover; `GET /art?item=<plid>` — a playlist item's cover
  (`vlc.playlist.get(num).item`). Requires the same Basic password (`:344`).
- Takes `metas["artwork_url"]` (usually `file:///C:/Users/<u>/AppData/Roaming/vlc/art/...`),
  returns the body with `Content-Type` by extension (`mimes[ext]`, otherwise `application/octet-stream`)
  and `Content-Length`; this is a handler, status line `HTTP/1.0 200`.
- No cover/error → `Status: 404`, body `Error` (`:187-194`).

### 2.13 Errors and edge cases

- A Lua error while generating a page (nil input, non-numeric `val`, `val=50%`) → `pcall` catches it,
  `callback_error` (`http.lua:116-131, 216-221`) returns **HTML** "Error loading …" with
  the error text, status **200**, the `Content-type` of the same page (`text/plain` for `.json`).
  The client must validate the JSON and treat "not JSON" as a command error.
  File callbacks cannot set the status — only handlers can (`httpd.c:453-456`).
- Unknown command, unknown hotkey name — silent, 200.
- When `stopped`: `vlc.object.input()` = nil, `vlc.input.item()` = nil → see the table in §2.7;
  `vlc.object.vout()`/`aout()` may be nil → `fullscreen=0`, no `aspectratio`/`audiofilters`.
- `pl_pause` when `stopped` starts the last active item (`playlist_Play`).
- Startup: right after VLC starts, the interface may respond with an empty body (observation from
  `live-probe.md`) — a retry is needed.
- `in_play&input=vlc://quit` will close VLC (`modules/access/idummy.c:158-166`) — anyone
  who knows the password can do it; see §6.3.

### 2.14 HTTP limitations that cannot be worked around without extensions

No push, no current tracks, no mute, no list/switching of audio devices, no
chapter names, no CORS/HTTPS (3.0.x), a single thread, keep-alive with the `Connection` nuance,
localized `information` keys.

---

## 3. RC interface (`modules/control/oldrc.c`)

### 3.1 Enabling on Windows

- Module `oldrc` ("Remote control interface", `oldrc.c:186-209`), capability `interface` 20,
  **has the shortcut `rc` on Windows** (`:206-208`). The Lua implementation `cli.lua` does **not** register the
  `rc` shortcut on Windows (`modules/lua/vlc.c:113-117`: `#ifndef _WIN32 … "cli","rc"`),
  so `--extraintf rc` on Windows = `oldrc.c`. (The Lua variant is available as
  `--extraintf luacli --lua-config "cli={host='…'}"` — the behaviour is different and not described here.)
- Options (`:191-201`): `rc-show-pos` (print `pos: N%`), `rc-quiet` (**Windows only**:
  do not open a console), `rc-host` (TCP), on non-Windows also `rc-unix`, `rc-fake-tty`.
- Example: `vlc.exe --extraintf rc --rc-host 0.0.0.0:4212 --rc-quiet` or in `vlcrc`:
  `extraintf=rc`, `rc-host=0.0.0.0:4212`, `rc-quiet=1`. The host is parsed by `vlc_UrlParse`
  (`:303-319`); the form without a scheme is allowed (`"//"` is prepended). Combined with HTTP:
  `--extraintf http:rc`.
- **Console window**: `Activate` (`:361-365`) calls `intf_consoleIntroMsg` when
  `rc-quiet=false` **regardless** of `rc-host`; `intromsg.h:22-29` does `AllocConsole()` and
  redirects stdout/stderr/stdin. Without `--rc-quiet` the GUI VLC gets a black console window,
  and RC will read commands from it (`ReadCommand`, `:1864-1874`). With `--rc-quiet` and no
  connected client the thread just sleeps (`:1870-1874`).
- **One connection at a time**: there is a single `i_socket`; `net_Accept` is called only when
  `i_socket == -1` (`:519-524`); a second client hangs in the backlog until the first one does `logout`/disconnects.
- **There is no authentication at all.** Binding to `0.0.0.0` opens full control
  (including `quit`, `add <any URL>`) to the whole network. For the companion — only `127.0.0.1`.
- Command line ≤ 1024 bytes (`MAX_LINE_LENGTH`, `:63`), terminator `\r` or `\n` (`:1899-1900`).
- Encoding: UTF-8 (`net_vaPrintf`/`utf8_vfprintf`, `:148-151`).

### 3.2 Output format and asynchronous lines

- Every response line ends with `\r\n` (`msg_rc`, `:140-153`).
- For "registered" commands (the list in `RegisterCallbacks`, `:412-474`), after execution
  `"<cmd>: returned <N> (<error text>)"` is printed (`:621-632`), e.g. `play: returned 0 (no error)`;
  for commands handled directly in `Run` (`info`, `get_time`, `key`, …) — it is not.
- Asynchronous events are printed from callbacks of other threads with the prefix `status change: `
  (`STATUS_CHANGE`, `:64`); they can interleave at any moment (under `status_lock`, but between
  response lines):
  - `( new input: <uri> )` (`:537`, `:1364`)
  - `( play state: 3 ): Play` / `( pause state: 4 ): Pause` / `( stop state: 0 )` and
    `( <cmd> state: <N> ): <Name>` from `StateChanged` (`:863-896`; names `Initializing, Opening,
    Play, Pause, End, Error`, `:67-74`); in `Run` additionally `( play state: 3 )`,
    `( pause state: 4 )`, `( stop state: 5 )` without a suffix (`:565-581`), `( stop state: 0 )`
    at end/error of the input (`:544-555`)
  - `( audio volume: <0..512> )` (`:849-861`)
  - `( new rate: 1.500 )` (`:897-904`)
  - `( time: <s>s )` after buffering (`:905-920`)
  - `( quit )` when the interface stops (`:778`)
  - `pos: N%` with `rc-show-pos` (`:585-593`)
- Block responses: `+----[ <Name> ]`, lines `| <value> - <text>` (the current one has the suffix ` *`),
  `+----[ end of <Name> ]` (`:1138-1151`, `:1646-1657`). The block name for tracks is the localized
  text of the variable (`VLC_VAR_GETTEXT`, e.g. `Audio Track`), for `adev` — `adev`.
- It can be parsed with regexes, but the protocol is not machine-oriented: there is no "end of response"
  delimiter, and the order of event lines and response lines is not guaranteed. In practice: read line by line, treat
  `status change:` lines as events, attribute the rest to the last command by
  heuristics (the `+----[` … `end of` block).

### 3.3 Commands (`:412-474`, `:621-771`, `:954-1776`)

| Command | Semantics | Reference |
|---|---|---|
| `add <MRL> [ :opt ...]` | `playlist_AddInput(..., play=true)`; MRL + options as ` :name=value` (`parse_MRL`) | `:1307-1322`, `:1922+` |
| `enqueue <MRL>` | the same, without starting | `:1323-1338` |
| `playlist` | the playlist tree: `|  - <name> (<h:mm:ss>)` with indentation | `:1339-1344`, `:1160-1176` |
| `play`, `stop`, `next`, `prev` | obvious; `clear` = stop + clear | `:1200-1212`, `:1298-1306` |
| `goto N` | a **1-based** index in the flat `items` list; `N<=0` → error; `> size` → "Playlist has only N elements" | `:1276-1297` |
| `repeat|loop|random [on|off]` | without an argument — toggle; prints `Setting repeat to true` | `:1213-1275` |
| `sort` | by artist only | `:1346-1352` |
| `status` | replays the `status change` lines: new input, audio volume, state (`stop 5/play 3/pause 4`) | `:1353-1393` |
| `pause` | toggle pause (`playlist_TogglePause`) | `:974-978` |
| `seek X` | `X` seconds (abs) or `X%`; **there are no relative forms** | `:980-993` |
| `fastforward` / `rewind` | doubling the rate (if `can-rate`/`can-rewind`), otherwise jump ±extrashort | `:994-1021` |
| `faster` / `slower` / `normal` | the playlist's `rate-faster`/`rate-slower`, `rate=1` | `:1022-1036` |
| `frame` | next frame | `:1037-1041` |
| `title [X]`, `title_n`, `title_p` | get: `Currently playing title N/M.`; set: index | `:1068-1092` |
| `chapter [X]`, `chapter_n`, `chapter_p` | likewise | `:1042-1067` |
| `atrack [X]`, `vtrack [X]`, `strack [X]` | **without an argument — a list** `| <ES id> - <text> [*]` including `-1 - Disable`; with an argument — `var_SetInteger(input, "audio-es", atoi(X))` (the same ES id as in HTTP) | `:1093-1154` |
| `vratio [X]`, `vcrop [X]`, `vzoom [X]` | get → the list of choices of the vout's `aspect-ratio`/`crop`/`zoom`; set as a string (zoom — float) | `:1503-1625` |
| `snapshot` | `video-snapshot` on the vout | `:1534-1537`, `:1555-1558` |
| `volume [X]` | get → `status change: ( audio volume: N )`; set abs 0..512, `0` additionally enables mute (`playlist_MuteSet(volume==0)`) | `:1425-1467` |
| `volup [N]`, `voldown [N]` | ±N steps (`volume-step`) | `:1469-1500` |
| `adev [id]` | **without an argument — the list of audio devices** `| <id> - <name> [*]` via `aout_DevicesList` + `aout_DeviceGet`; with an argument `aout_DeviceSet(id)` — real switching. For `mmdevice` the id is an endpoint string `{0.0.0.00000000}.{GUID}` (observation from `live-probe.md`), an empty id = default. Requires a live aout (`playlist_GetAout`) — after `stop` there may be "no object" | `:1627-1668`, `src/audio_output/output.c` |
| `achan [N]` | list/set `stereo-mode` (Mono/Stereo/Original/Headphones…) | `:1670-1712` |
| `info` | stream categories as `+----[ <cat> ]` / `| Key: value` (the same data as `information`) | `:659-687` |
| `stats` | text statistics | `:1714-1776` |
| `get_time`, `get_length` | whole seconds; `0` without an input | `:699-712` |
| `is_playing` | `1`/`0` (whether there is an input, not whether it is playing) | `:688-698` |
| `get_title` | item name (`psz_name`) | `:713-723` |
| `key <name>` / `hotkey <name>` | `var_SetInteger(libvlc, "key-action", vlc_actions_get_id(arg))` — **the argument must have the `key-` prefix** (`key key-nav-up`); without the prefix → `ACTIONID_NONE` = 0, silently nothing | `:729-733`, `actions.c:587-597` |
| `f [on|off]` / `fullscreen` | toggle/set `fullscreen` of the playlist and the vout. **Any command starting with `f`/`F`** ends up here (`switch(psz_cmd[0])`) | `:736-758` |
| `intf <module>` | bring up one more interface | `:1416-1423` |
| `logout` | close the TCP session | `:650-658` |
| `quit` | `libvlc_Quit` — close VLC | `:1406-1414` |
| `help`, `longhelp`, `h…`, `H…`, `?` | help | `:724-728`, `:785-844` |
| `shutdown`, `rate X`, `vdeinterlace` | **absent in 3.0.x**: `shutdown`/any command starting with `s` is silently ignored (`:760-763`), `rate` → `Unknown command` | `:769` |
| global libvlc commands | if the name matches a libvlc command variable (`var_Type(libvlc, cmd) & ISCOMMAND`), that is invoked | `:633-649` |

### 3.4 RC pitfalls

- **Almost everything is rejected while paused**: `Input()` (`:966-971`), when `state == PAUSE_S`, for any
  command other than `pause`/`frame` prints "Press pause to continue." and does nothing —
  `seek`, `atrack`, `title`, `faster` do not work while paused. The same goes for `Playlist()` (`next`,
  `prev`, `play`, `goto`, `add`…) — "Type 'pause' to continue." (`:1192-1197`), and for
  `volume`/`volup`/`voldown` (`:1439-1445`, `:1483-1489`). Exceptions: `adev`, `achan`,
  `vratio/vcrop/vzoom/snapshot`, `key`, `info`, `get_*`. For the remote: unpause before `seek`
  or use HTTP.
- `key` without the `key-` prefix is a silent no-op; `help` shows `key [hotkey name]` without
  clarification.
- `volume` requires an input (`VLC_ENOOBJ`, `:1434-1435`).
- `goto` takes an index, not an id; `playlist` prints neither indices nor ids.
- One client; events go only to the connected one.

### 3.5 RC vs HTTP

| Capability | HTTP | RC |
|---|---|---|
| Authentication | Basic | none |
| Multiple clients | yes (sequentially) | one |
| Push events (state, volume, new input, rate) | no | yes (`status change:`) |
| Track list with IDs | indirectly (`Stream N` + `Original_ID`) | directly, with `*` on the current one |
| Current track | no | yes |
| Audio devices: list/switching | no | yes (`adev`) |
| Stereo mode | no | yes (`achan`) |
| Relative seek, `%` seek, h/m/s | yes | only abs seconds and `%` |
| Volume | abs/relative | abs, steps; mute at 0 |
| Works while paused | yes | most commands — no |
| Playlist as data | JSON with ids | text without ids |
| File browsing | `browse.json` | no |
| Cover art | `/art` | no |
| Equalizer, delays, rate=X | yes | no delays/`rate X` (`faster/slower`) |
| Close VLC | via `key&val=quit` (the neighbouring run observed that it did not work) or `in_play&input=vlc://quit` | `quit` |
| Hotkey by name | `key&val=<name>` | `key key-<name>` |
| Machine readability | JSON | text, regexes |

---

## 4. Hotkey action names (`src/misc/actions.c:264-379`, `include/vlc_actions.h`)

The full 3.0.x table (ASCII-sorted in the code). The ones useful for the remote, with semantics from
`modules/control/hotkeys.c`:

| Name (`val=` in HTTP; `key key-<name>` in RC) | Action |
|---|---|
| `play-pause` | toggle pause (`hotkeys.c:544-553`) |
| `play` | play; if rate≠1 — first restore rate=1 (`:555-565`) |
| `pause`, `stop`, `next`, `prev` | obvious |
| `quit` | `libvlc_Quit` (`:357`) |
| `toggle-fullscreen`, `leave-fullscreen` | `:1001-1018` |
| `vol-up`, `vol-down`, `vol-mute` | `volume-step` step; mute = toggle `playlist_MuteGet/Set` (`:487-510`) — **the only way to mute via HTTP** |
| `jump-extrashort`/`jump+extrashort`, `jump-short`/`jump+short`, `jump-medium`/`jump+medium`, `jump-long`/`jump+long` | back/forward by `extrashort/short/medium/long-jump-size` = 3/10/60/300 s by default (`:903-952`, `libvlc-module.c:2669-2675`); `-` back, `+` forward (in a URL encode `+` as `%2B`) |
| `nav-up`, `nav-down`, `nav-left`, `nav-right`, `nav-activate` | DVD/BD menu (`:974-994`) |
| `disc-menu` | disc menu |
| `title-prev`, `title-next`, `chapter-prev`, `chapter-next` | `:954-968` |
| `audio-track` | next audio track in a cycle, OSD "Audio track: …" (`:721-756`) |
| `subtitle-track`, `subtitle-revtrack`, `subtitle-toggle` | cycle subtitles forward/backward; toggle remembers the last one (`spu-choice`) (`:758-850`) |
| `subdelay-up`/`subdelay-down`, `audiodelay-up`/`audiodelay-down` | ±50 ms (`:676-720`); require an active track |
| `subsync-markaudio`, `subsync-marksub`, `subsync-apply`, `subsync-reset` | subtitle synchronization by marks |
| `subpos-up`, `subpos-down` | subtitle position (`:1305-1337`) |
| `subtitle-text-scale-normal/up/down` | subtitle text scale 25..500 %, step 10/25 (`:1339-1357`) |
| `rate-normal`, `faster`, `slower`, `rate-faster-fine`, `rate-slower-fine` | speed (`:422-440`) |
| `frame-next` | frame by frame |
| `position` | show the OSD position (`:1361-1364`) |
| `aspect-ratio`, `crop`, `zoom`, `unzoom`, `zoom-quarter/half/original/double`, `crop-top/…`, `uncrop-…` | video (`:1019-1200`) |
| `deinterlace`, `deinterlace-mode` | |
| `toggle-autoscale`, `incr-scalefactor`, `decr-scalefactor` | video scale in the window (`:1137-1176`) |
| `wallpaper` | "wallpaper" mode (DirectX) |
| `snapshot`, `record` | |
| `random`, `loop` | toggle (`loop` cycles off→all→one, `:376-420`) |
| `clear-playlist` | |
| `audiodevice-cycle` | **exists**: iterates over `aout_DevicesList`, applies `aout_DeviceSet`, OSD "Audio Device: …" (`:514-530`). The only way to change the audio device via HTTP — but only cyclically, without choosing |
| `intf-show` (= `ACTIONID_INTF_TOGGLE_FSC`), `intf-boss`, `intf-popup-menu` | show/hide the FS controller, the "boss key" (hide VLC), the context menu |
| `program-sid-next/prev` | program switching (TS) |
| `viewpoint-fov-in/out`, `viewpoint-roll-clock/anticlock` | 360° |
| `set-bookmark1..10`, `play-bookmark1..10` | bookmarks |

In `master` the enums were renamed (`ACTIONID_FASTER`→`RATE_FASTER`), but the string names
`faster`/`slower` were kept; `frame-previous`, `projection-toggle`,
`subtitle-control-secondary` were added (`master src/misc/actions.c:304-372`).

---

## 5. VLC 4 (`master` branch) — differences and status

**Release status (October 2026).** `master/NEWS:1`: "Changes between 3.0.x and 4.0.0-dev" —
there is no 4.0 release. The current stable version for Windows is **3.0.23** (8 January 2026;
[videolan.org/news](https://www.videolan.org/news/2026-01-08-vlc-3-0-23/),
[linuxiac](https://linuxiac.com/vlc-3-0-23-media-player-released-with-windows-fixes-and-security-updates/)).
According to reports from July 2026, 4.0 beta builds existed only for Apple; for Windows — nightlies
([digitec](https://www.digitec.ch/en/page/open-source-tip-vlc-the-player-for-just-about-anything-43212)).
Old promises of "4.0 this year" ([itsfoss 2021](https://itsfoss.com/news/vlc-4-features/),
[linuxiac](https://linuxiac.com/vlc-4-0-coming-this-year-with-completely-redesigned-ui/)) did not come true.

**HTTP (Lua) in master** — the module remains (`master modules/lua/vlc.c:673-681`, the same options),
the pages are the same, `apiversion` is still 3; differences in `httprequests.lua`:

| What | 3.0.x | master |
|---|---|---|
| Core | `vlc.object.input()` + `vlc.var` | `vlc.player.*` (the new `vlc_player`), `share/lua/README.txt` "Player" |
| `audio_track/video_track/subtitle_track` | `var.set(input,"audio-es",id)` — select | `vlc.player.toggle_audio_track(id)` — **toggle** (`:181-186`): if the track is selected — deselects it, otherwise selects it (`modules/lua/libs/input.c:363-383`); `-1` deselects the category. `id` is the integer `vlc_es_id_GetInputId(es_id)` (`input.c:318`), not a string like `"video/0"` (string ids exist only in the C API `vlc_es_id_GetStrId`) |
| `title/chapter` | index via var | `player.title_goto(i)`/`chapter_goto(i)` (`:177-180`) |
| `audiodelay/subdelay/rate` | var on the input | `player.set_audio_delay(s)` etc. (`:152-162`), without an input check |
| `seek` | var `time/position` | `player.seek_by_*` (`common.lua`), the rest of the grammar is the same |
| `pl_loop/pl_repeat` | mutually exclusive | plain toggle (`:136-139`) |
| `pl_sd` | present | **removed** |
| new | — | `set_renderer&id=`, `unset_renderer` (Chromecast/renderer discovery, `:187-194`), `renderers` in the output (`get_renderers`) |
| `status.information` | `chapters/titles` arrays of indices | `chapters_count`, `titles_count` (`:520-524`) |
| `playlist.json` | node/leaf tree | a **flat array** of leaf objects `{type:"leaf", id, uri, name, duration, current}` without `ro` and without nodes (`:314-338`, `vlc.playlist.list()`) |
| `volume` | 0..512 | the same |
| HTTPS | no | `--http-cert` (`NEWS:271`) |
| `key` | `common.hotkey("key-"..val)` | the same (`:150-151`) |

The master Lua API for custom pages (`share/lua/README.txt`, the Player section): `player.get_audio_tracks()`
→ `{ {id, name, selected}, … }`, `get_spu_tracks`, `get_video_tracks`, `get_time()` (µs),
`get_position()`, `get_rate()`, `get_title_index()`, `get_chapter_index()`, `seek_by_*`,
`get/set_audio_delay` (seconds, float). This solves the "current track" problem out of the box.

**RC in master.** The Lua submodule `cli` was **removed** from `modules/lua/vlc.c`; RC was rewritten in C:
`modules/control/cli/cli.c` with the shortcuts `cli`, `rc`, `oldrc` (`cli.c:1087`), options
`rc-host`, `rc-quiet`, `rc-unix`, `rc-show-pos`, `rc-fake-tty` (`:1070-1082`). Commands
(`cli.c:267-279`, `cli/player.c:1016-1060`, `cli/playlist.c:567-581`): as in 3.0 plus
`rate`, `record`, `fs`, `move` (but without `delete`); `atrack/vtrack/strack N` now take
**a 0-based index in the track list**, `-1` deselects; without an argument they print
`| <index> - <name> [*]` (`player.c:429-497`) — **not the ES id**. `adev` is kept (`player.c:771-793`).
`key`/`hotkey` still require `key-` (`cli.c:253-265`, `actions.c:617`).
Separately there is `share/lua/intf/cli.lua` (for `--extraintf luaintf --lua-intf cli`):
there `hotkey` accepts both spellings (`cli.lua:547-552`), `adev` is commented out (`:604`),
`atrack` prints `vlc.player.get_audio_tracks()` (id = input id) and calls
`toggle_audio_track` (`:519-531`).

Conclusion: a client targeting 3.0.x should build in an abstraction layer over "track selection"
(select vs toggle, id vs index) and over the shape of `playlist.json`.

---

## 6. Other control paths on Windows

### 6.1 Command line and one-instance

- `--one-instance` (`libvlc-module.c:2090`), `--one-instance-when-started-from-file` (on by
  default, `:2095`) + `--started-from-file` (`:2092`, set by the file association), `--playlist-enqueue`
  (`:2098`). Mechanism (`src/win32/specific.c:90-169`): a mutex and the window `"VLC ipc <version>"`
  (`FindWindow`, `:126`), data via `WM_COPYDATA` (`:169`); the receiver `modules/control/win_msg.c:60-115`
  adds each argument as an item (`playlist_AddExt`), **the first one with playback started**, unless
  `playlist-enqueue` (`:101-107`); arguments starting with `:` are attached as item options
  (`:88-93`). I.e. `vlc.exe --one-instance "D:\x.mkv" :start-time=600` from any process on the PC
  will hand the file to the running VLC. Over the network — no (only a local companion).
- The `vlc://` pseudo-URL (`modules/access/idummy.c:148-198`, shortcuts `dummy`, `vlc`): `vlc://quit`
  (close VLC, `:158-166`), `vlc://pause` (an endless placeholder pause, `:168-175`), `vlc://pause:N`
  (a pause of N seconds as a playlist item, `:177-198`), `vlc://nop`. They work as playlist items,
  including via `in_play`/`add`. For the remote, `in_play&input=vlc://quit` is a working "turn off VLC"
  (**not verified** live), but also a hole: anyone with the password can close it.
- Useful launch flags (`libvlc-module.c`, `modules/gui/qt/qt.cpp`):
  `--fullscreen` (`:1557`), `--no-video-title-show` (`:1585`), `--video-title-timeout` (`:1588`),
  `--video-x/--video-y` (`:1620-1622`), `--video-on-top` (`:1578`), `--aspect-ratio` (`:1628`),
  `--start-time/--stop-time/--run-time` (seconds), `--sub-file=<path>` (`:1684`),
  `--audio-track=N`/`--sub-track=N` (**index**, `:1720-1723`), `--audio-track-id`/`--sub-track-id`
  (ES id, `:1738-1741`), `--audio-language=ru,en`/`--sub-language` (`:1726-1730`), `--rate` (`:1770`),
  `--random/--loop/--repeat` (`:2071-2077`), `--play-and-exit/--play-and-stop/--play-and-pause`
  (`:2080-2083`), `--no-auto-preparse` (`:2109`);
  Qt: `--qt-fullscreen-screennumber=N` (`qt.cpp:297`, -1 = current), `--qt-minimal-view` (`:230`),
  `--qt-start-minimized` (`:240`), `--qt-system-tray` (`:233`), `--qt-notification` (`:235`),
  `--qt-continue` (`:261`, "continue where you left off"), `--qt-pause-minimized` (`:242`),
  `--no-qt-privacy-ask` (`:293`), `--qt-disable-volume-keys` (`:274`), `--qt-auto-raise` (`:322`).
- Audio: output modules — `mmdevice` (WASAPI, priority 150, `modules/audio_output/mmdevice.c:1534-1549`),
  `directsound` (`directx`, 100, `directsound.c:73-81`), `waveout`. Choosing the device at startup:
  `--aout=mmdevice --mmdevice-audio-device=<endpoint id>` (`mmdevice.c:1547`) or
  `--directx-audio-device=<id>` (`directsound.c:80`). Endpoint IDs can be obtained only from
  `adev` (RC) / libvlc `libvlc_audio_output_device_enum` / WinAPI `IMMDeviceEnumerator`.
  Options `mmdevice-passthrough`, `mmdevice-volume` (`:1543-1549`).

### 6.2 Media keys, SMTC, D-Bus

- The Qt interface handles `WM_APPCOMMAND` (`main_interface_win32.cpp:254-312`): play/pause,
  play, pause, prev/next, stop, volume up/down/mute (unless `qt-disable-volume-keys`), rewind/ff.
  These are the standard keyboard media keys — they work when the message reaches the VLC window.
  In addition, `vlc.exe` always brings up `globalhotkeys` (`bin/vlc.c:242`) — global
  `RegisterHotKey` for actions that have a "global" key assigned (`globalhotkeys/win32.c:253`).
- **VLC 3.0.x does not register SMTC (System Media Transport Controls)**: in the 3.0.x tree and in `master`
  there is neither `SystemMediaTransportControls` nor any mention in `NEWS`. There is a third-party plugin
  [vlc-win10smtc](https://github.com/spmn/vlc-win10smtc) (MSVC, VLC 3.0.x, last updated
  2021). Consequence: the Windows "now playing" overlay and Bluetooth AVRCP via SMTC cannot be
  obtained from VLC; the companion can, if desired, register SMTC itself and relay commands to VLC.
- D-Bus/MPRIS — only with `HAVE_DBUS` (Linux); not built on Windows (`libvlc-module.c:2101-2102`).

### 6.3 libvlc as a "companion"

`libvlc` (`libvlc_audio_output_device_enum/set`, `libvlc_media_player_*`) controls **its own**
player instance, not a running `vlc.exe`; it cannot attach to the GUI process. A companion on
libvlc = its own player (without VLC's Qt window/menus), not a remote for VLC. Therefore, to control
a running VLC the companion can use: RC on `127.0.0.1`, HTTP, one-instance IPC, custom
Lua pages, WinAPI (window/monitor/sleep), an SMTC bridge.

---

## 7. Extending the HTTP interface with your own Lua pages

### 7.1 Where `http.lua` takes pages from and how to override them without Program Files

`http.lua:271-281`:

```lua
local function find_datadir(name)
    local list = vlc.config.datadir_list(name)
    for _, l in ipairs(list) do
        local s = vlc.net.stat(l)
        if s then return l end          -- the FIRST existing directory
    end
    error("Unable to find the `"..name.."' directory.")
end
http_dir = config.dir or find_datadir("http")
```

`vlc.config.datadir_list("http")` → `vlclua_dir_list` (`modules/lua/vlc.c:214-241`,
`libs/configuration.c:148-166`), order:

1. `config_GetUserDir(VLC_DATA_DIR)\lua\http` = **`%APPDATA%\vlc\lua\http`** (`src/win32/dirs.c:281-300`:
   `CSIDL_APPDATA` + `\vlc`);
2. `config_GetLibDir()\lua\http` = `C:\Program Files\VideoLAN\VLC\lua\http`;
3. `config_GetDataDir()\lua\http` — on Windows it is the same as libdir (`dirs.c:241-244`) and is skipped
   (`vlc.c:228-237`, `both == false`).

Then `load_dir(http_dir)` (`http.lua:294-327, 343`) registers **only the files of the selected
directory** (recursively): text ones (`text/*` per the `mimes` table) as Lua templates, the rest
as raw; `index.html` → `/`; if there is no `index.html` and `no_index=false`, an
auto-listing is added. `custom.lua` is loaded from the same `http_dir` (`:283-293`, its absence is only a warning).

**Consequence: there is no merging.** If `%APPDATA%\vlc\lua\http` exists, the server serves only its
contents; the stock `requests/status.json` and the web UI from Program Files **stop responding (404)**.
**Verified live** (2026-10-12, VLC 3.0.23, `-I dummy --extraintf http --http-host 127.0.0.1`):
with the single file `%APPDATA%\vlc\lua\http\requests\probe.json` → `GET /requests/probe.json`
= 200 `{"probe":true}`, `GET /requests/status.json` = **404 Not found**, `GET /` = 200
(auto-listing). The statement in `live-probe.md` ("the user folder is merged with the
system one") is **wrong** — probably VLC was not restarted between checks there, or only
the new file was checked.

How to drop in your own pages properly without touching Program Files:

1. Create `%APPDATA%\vlc\lua\http\` and **copy into it** from `C:\Program Files\VideoLAN\VLC\lua\http\`
   at least `requests\*.json|xml` (these are plain-text Lua templates, GPL) and, if the stock
   web UI is needed, the whole directory (`index.html`, `js`, `css`, `images`, `dialogs`, `custom.lua`, …).
2. Add your own `requests\tracks.json` etc. Restart VLC (URL registration happens only at startup;
   the contents of already registered files are re-read by mtime, `http.lua:87-104`).
3. An alternative without APPDATA: `--http-src=<directory>` → `config.dir` (`intf.c:67-80`) — the same
   "only this directory" semantics.
4. `require "httprequests"`/`require "common"` work in your own pages: `package.path`
   is built from the location of the loaded `http.luac` (`intf.c:290`, `vlc.c vlclua_add_modules_path`:
   `<script directory>/modules`, `<parent>/modules` and then the `datadir_list("intf")` directories,
   starting with the one where the script was found). For the system `http.luac` these are only
   `C:\Program Files\VideoLAN\VLC\lua\intf\modules` and `...\lua\modules`, i.e. the system
   `.luac` modules stay available, but your own modules in `%APPDATA%\vlc\lua\intf\modules`
   will not be found until `intf\http.lua` itself is overridden (item 5).
5. The user directory is also searched for the interface scripts themselves (`vlclua_find_file("intf","http")`,
   `vlc.c:309-339`, extensions `.luac`, `.lua`, `.vle`): the file `%APPDATA%\vlc\lua\intf\http.lua` completely
   replaces the system `http.luac`. This is the path for deep customization, but it is fragile across VLC updates.
6. In `master` the `http.lua` logic is the same (the diff concerns only `/art`), i.e. the approach is portable.

Permissions: `%APPDATA%` is writable by the user without UAC; the remote app cannot do this itself
(it has no access to the PC's file system), an install script/the companion is needed.

### 7.2 What a custom page can return beyond `status.json` (API 3.0.x)

Available from an interface script (`share/lua/README.txt`): `vlc.object.{input,playlist,aout,vout,libvlc}`,
`vlc.var.{get,set,get_list,inherit,create,trigger_callback,toggle_bool,count_choices}`,
`vlc.playlist.*`, `vlc.input.item()`, `vlc.volume.*`, `vlc.config.*`, `vlc.misc.action_id`,
`vlc.equalizer.*`, `vlc.net.*`, `vlc.io.*`, `vlc.sd.*`, `vlc.vlm()`.

| Task | Call | Result | Status |
|---|---|---|---|
| List of audio/sub/video tracks **with IDs and the current one** | `local ids, names = vlc.var.get_list(input, "audio-es")`; `cur = vlc.var.get(input, "audio-es")` (`variables.c:236-252`, `var_Change GETCHOICES`) | `ids` = `{-1, id1, id2…}`, `names` = `{"Disable", "Description - [Russian]", "Track 2 - [English]"…}` (`es_out.c:936-963`); `cur` — the current ES id | **verified live** by the neighbouring run (`tracks-custom-page.json`) |
| Track selection | `vlc.var.set(input, "audio-es", id)` | the same as HTTP `audio_track` | verified by the neighbouring run |
| Chapters/titles with names | `get_list(input, "chapter")`, `get_list(input, "title")` | indices + `"Chapter N"`/name, `"Title N [h:mm:ss]"` (`var.c:316-352`) | verified by the neighbouring run |
| Mute | `vlc.var.get(aout, "mute")` (bool, `output.c:223`) | the current state | **not verified** |
| Set mute | `vlc.var.set(aout,"mute",true)` — only copies into the `playlist` variable (`output.c:57-63`), does not touch the aout itself; reliable — `common.hotkey("key-vol-mute")` | | not verified |
| Input state | `vlc.var.get(input,"state")` (1..6), `"can-seek"`, `"can-pause"`, `"can-rate"`, `"length"` (µs) | | per `var.c:95-215` |
| Stereo mode | `get_list(aout,"stereo-mode")` / `set` | Mono/Stereo/Original/Headphones | verified by the neighbouring run |
| Aspect/crop/zoom/deinterlace | `get_list(vout, "aspect-ratio"|"crop"|"zoom"|"deinterlace"|"deinterlace-mode")` / `set` | lists of allowed values with labels | verified by the neighbouring run (partially) |
| **Audio devices: list** | `get_list(aout, "device")` | **empty**: `device` is an ordinary string variable without choices (`output.c:225`); the list lives in `owner->dev.list` and is available only via the C API `aout_DevicesList` (`output.c`, no Lua binding) | **verified live** by the neighbouring run (empty) + code |
| Current audio device | `vlc.var.get(aout, "device")` | the current id (a reporting variable, `aout_DeviceNotify`, `output.c:101-104`); empty = default | not verified |
| **Switch the audio device** | `vlc.var.set(aout, "device", id)` | **does not switch**: only `var_CopyDevice` fires → the parent's `audio-device` (`output.c:67-74`); real switching goes through `aout_DeviceSet` → `owner->req.device` → `aout_OutputDeviceSet` (`output.c:833-835, 695-699`), which has no Lua binding | not verified, but impossible per the code |
| Workaround for the device | `vlc.config.set("mmdevice-audio-device", id)` + restart of the aout (stop/play) | the device is applied on the next aout creation (`mmdevice.c:1351`); `config.set` does not write `vlcrc` to disk | not verified |
| Cycle devices | `common.hotkey("key-audiodevice-cycle")` | cyclically, OSD | — |
| Push/long polling | a blocking page with `vlc.misc.mwait` | **impossible**: the host is single-threaded (§2.2) — it would block all requests | — |
| Volume | `vlc.volume.get/set/up/down` | 0..512 | — |
| Current item | `vlc.playlist.current()`, `vlc.input.item():uri()/name()/metas()` | | — |

Example of a minimal `requests/tracks.json` page:

```lua
<?vlc
local httprequests = require "httprequests"
httprequests.processcommands()          -- so that commands work from this page too
local out = { audio = {}, spu = {}, video = {} }
local input = vlc.object.input()
if input then
  for key, var in pairs({audio="audio-es", spu="spu-es", video="video-es"}) do
    local ids, names = vlc.var.get_list(input, var)
    local cur = vlc.var.get(input, var)
    local list = { _array = {} }
    for i, id in ipairs(ids) do
      table.insert(list._array, { id = id, name = names[i], selected = (id == cur) })
    end
    out[key] = list
  end
end
httprequests.printTableAsJson(out)
?>
```

(`_array` is a `printTableAsJson`/`removeArrayIndicators` convention, `httprequests.lua:228-252`,
so that empty tables are printed as arrays.)

### 7.3 Mapping `key` to a hotkey: summary

| Channel | What to pass | Code |
|---|---|---|
| HTTP 3.0.x / master | `command=key&val=nav-up` (without `key-`) | `httprequests.lua:162-163` / master `:150-151` → `common.hotkey("key-"..val)` → `vlc.misc.action_id` → `vlc_actions_get_id` (requires `key-`) |
| RC 3.0.x (`oldrc.c`) | `key key-nav-up` (with `key-`) | `oldrc.c:729-733` |
| RC master (`cli.c`) | `key key-nav-up` | `cli.c:253-265` |
| `cli.lua` (luaintf) | `hotkey nav-up` or `hotkey key-nav-up` | `cli.lua:547-552` |
| Custom Lua page | `common.hotkey("key-nav-up")` | `common.lua:25-33` |

---

## 8. Conclusions for the remote's design

1. **The base transport is HTTP (`status.json`/`playlist.json`/`browse.json`)**. It covers
   play/pause/stop/seek/rate/volume/fullscreen/chapters/delays/equalizer/playlist/FS browsing/cover art
   and even track selection (IDs are reconstructed from `Stream N` + `Original_ID`). Client: HTTP/1.1 without
   a `Connection` header, a single connection, polling every 500-1000 ms, JSON validation (errors arrive
   as HTML with status 200, MIME `text/plain`), tolerance for `fullscreen` being bool/0, for the absence of
   `information` when `stopped`, for localized `information` keys.
2. **Via `key`** you get mute (`vol-mute`), track cycling, menu navigation, jumps by
   fixed intervals, `audiodevice-cycle` (the only "audio switch" in plain HTTP).
   Do not rely on `volume&val=N%` or on `+` as a space.
3. **What HTTP does not provide**: the current track, chapter names, the mute state, the list/selection of the
   audio device, push events, multiple interfaces — this is exactly the companion's area.
4. **A "light companion" = custom Lua pages in `%APPDATA%\vlc\lua\http`** (with a copy of the stock
   `requests/*`): gives the current tracks with names, chapters with names, mute, aspect/crop/
   deinterlace lists, the input state. It does not give: the list/switching of audio devices (no Lua binding for
   `aout_DevicesList/DeviceSet`), push. It requires installing files on the PC and restarting VLC;
   remember that the folder **replaces** rather than supplements the system one.
5. **A full companion (a process on the PC)**: holds RC on `127.0.0.1:4212` (the only client,
   `--rc-quiet`), receives `status change:` pushes, the `atrack/strack/adev` lists with `*`, switches the
   device with `adev <id>`; works around "nothing can be done while paused" (RC) by using
   HTTP for seek/next; adds WinAPI (window, monitor via `--qt-fullscreen-screennumber` only at startup,
   sleep/wake), launching VLC with the needed flags (`--extraintf http:rc …`), one-instance IPC,
   optionally an SMTC/AVRCP bridge, its own WebSocket for the phone. Do not build the companion on
   libvlc — it does not control a running `vlc.exe`.
6. **Security**: HTTP only with a password (Basic, empty login); RC has no authentication — localhost
   only; `in_play&input=vlc://quit`/`add` of arbitrary URLs are available to anyone with the password.
7. **VLC 4**: the pages are the same, but `audio_track` becomes a toggle, `playlist.json` becomes flat, RC uses
   indices instead of ES ids; abstract "track selection" and "playlist structure" behind an interface,
   determine the version from `status.version`.
