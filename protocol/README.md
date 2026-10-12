# Протокол vlc-remote

Версия протокола: **1**. Этот документ — источник истины для Android‑приложения и компаньона.

Два уровня:

1. **Прямой режим (уровни 0/1)** — приложение само ходит в HTTP‑интерфейс VLC. Описание в
   [vlc-http.md](vlc-http.md).
2. **Режим компаньона (уровень 2)** — приложение разговаривает с компаньоном по NDJSON поверх
   WebSocket (LAN) или RFCOMM (Bluetooth, этап M3). Описание ниже и в [messages.schema.json](messages.schema.json).

## Транспорт

- WebSocket: `ws://<host>:47555/ws`. Текстовые фреймы, один JSON‑объект на фрейм. Тот же NDJSON
  (`\n` в конце) допустим и игнорируется при разборе.
- RFCOMM (M3): поток байт, UTF‑8, одна JSON‑строка на сообщение, разделитель `\n`.
- Диагностика без WebSocket: `GET http://<host>:47555/info` → `{"name","version","protocol":1,"vlc":{...},"wsPort":47555,"needsPairing":bool}` (без авторизации, только для отображения в списке найденных ПК).

## Обнаружение

- DNS‑SD: тип службы `_vlcremote._tcp`, имя = имя ПК, порт 47555, TXT: `v=1`, `name=<имя ПК>`, `id=<стабильный GUID установки>`.
- UDP‑маяк: приложение шлёт broadcast на `255.255.255.255:47556` текст `VLCREMOTE_DISCOVER 1`;
  компаньон отвечает unicast JSON `{"t":"beacon","name":"...","id":"...","wsPort":47555,"protocol":1,"needsPairing":bool}`.

## Общая форма сообщения

```json
{"t":"<тип>", "id":"<строка, опционально, для сопоставления ответа>", ...поля}
```

Ответы на запросы несут тот же `id`. Ошибки: `{"t":"err","id":"...","code":"<код>","msg":"..."}`.
Коды: `unauthorized`, `bad_request`, `vlc_not_running`, `vlc_error`, `unsupported`, `internal`.

## Рукопожатие и сопряжение

1. Телефон → `{"t":"hello","v":1,"device":{"name":"Pixel 8","id":"<uuid>"},"token":"<токен или null>","pairCode":"<6 цифр или null>"}`
2. ПК → `{"t":"welcome","v":1,"name":"<имя ПК>","id":"<GUID>","caps":[...],"token":"<новый токен, только при успешном pairCode>","vlc":{"running":true,"version":"3.0.23","apiVersion":3,"level":2}}`
   либо `{"t":"err","code":"unauthorized","msg":"pairing required"}` и закрытие соединения.
3. Код сопряжения компаньон показывает в окне на экране ТВ/ноутбука по нажатию в трее
   («Сопряжение телефона») и принимает его 5 минут. Токен — 32 байта base64url, ПК хранит SHA‑256.

`caps` — строки: `currentTrack`, `chapterNames`, `audioDevices`, `pushEvents`, `launchVlc`,
`windowControl`, `power`, `systemVolume`, `metadata`, `bluetooth`.

## Команды плеера: `cmd`

`{"t":"cmd","id":"7","name":"<имя>","args":{...}}` → ответ `{"t":"ok","id":"7"}` (или `err`),
а изменения состояния приходят отдельным `state`.

| name | args | Соответствие в VLC |
|---|---|---|
| `play` | `{"plId": int?}` | `pl_play&id=` / `pl_forceresume` |
| `pause` | — | `pl_forcepause` |
| `togglePause` | — | `pl_pause` |
| `stop` | — | `pl_stop` |
| `next` / `prev` | — | `pl_next` / `pl_previous` |
| `seek` | `{"seconds": int}` абсолют или `{"delta": int}` | `seek&val=` |
| `volume` | `{"value": 0..512}` или `{"delta": int}` | `volume&val=` |
| `mute` | `{"on": bool}` | компаньон запоминает уровень, ставит 0 / возвращает |
| `rate` | `{"value": float}` | `rate&val=` |
| `track` | `{"kind":"audio"|"subtitle"|"video","id": int}` (`-1` = выключить) | `audio_track`/`subtitle_track`/`video_track` |
| `chapter` | `{"index": int}` или `{"delta": ±1}` | `chapter&val=` |
| `title` | `{"index": int}` | `title&val=` |
| `delay` | `{"kind":"audio"|"subtitle","seconds": float}` | `audiodelay`/`subdelay` |
| `fullscreen` | `{"on": bool?}` (без аргумента — toggle) | `fullscreen` (компаньон сверяет состояние) |
| `aspect` | `{"value": "16:9"|"default"...}` | `aspectratio&val=` |
| `open` | `{"uri": string, "enqueue": bool?, "subtitle": string?, "audioTrack": int?, "subtitleTrack": int?}` | `in_play`/`in_enqueue` (+ `addsubtitle`, выбор дорожек после старта) |
| `playlistRemove` | `{"plId": int}` | `pl_delete` (ненадёжно, компаньон перепроверяет) |
| `playlistClear` | — | `pl_empty` |
| `random` / `loop` / `repeat` | `{"on": bool}` | `pl_random`/`pl_loop`/`pl_repeat` (toggle, компаньон сверяет) |
| `launch` | `{"uri": string?, "display": int?}` | запуск `vlc.exe` с нужными параметрами |
| `quit` | — | RC `quit` или `in_play&input=vlc://quit` |

## Системные команды: `sys` (нужны соответствующие `caps`)

| name | args | cap |
|---|---|---|
| `moveToDisplay` | `{"display": int}` | `windowControl` |
| `listDisplays` | — → ответ `{"t":"displays","id":..,"items":[{"index","name","primary","bounds"}]}` | `windowControl` |
| `keepAwake` | `{"on": bool}` | `power` |
| `displayPower` | `{"on": bool}` | `power` |
| `sleep` / `shutdown` | — | `power` |
| `audioDevice` | `{"id": string}` (`""` = по умолчанию) | `audioDevices` |
| `systemVolume` | `{"value": 0..100}` или `{"delta": int}` или `{"mute": bool}` | `systemVolume` |

## Запросы данных

- `{"t":"browse","id":"3","uri":"file:///D:/shared/"}` → `{"t":"entries","id":"3","uri":"...","items":[{"name","uri","type":"dir"|"file","size":long?,"modified":unixSec?}]}`.
  `uri` = `"file:///"` → список дисков. Элемент `..` не возвращается (телефон сам строит «вверх»).
- `{"t":"getState","id":"4"}` → полный `state`.
- `{"t":"getPlaylist","id":"5"}` → `{"t":"playlist","id":"5","items":[{"plId","name","uri","durationSec","current":bool}]}`.
- `{"t":"getTracks","id":"6"}` → `{"t":"tracks","id":"6", ...как в state.tracks}`.
- `{"t":"getAudioDevices","id":"8"}` → `{"t":"audioDevices","id":"8","items":[{"id","name","current":bool}]}`.

## Состояние: `state`

Компаньон шлёт `state` при изменениях (не чаще 4 раз в секунду; при воспроизведении минимум раз
в секунду с обновлённым `time`). Всегда полный объект (не diff) — проще и надёжнее при потере пакетов.

```json
{
  "t": "state",
  "vlc": {"running": true, "version": "3.0.23", "apiVersion": 3, "level": 2},
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
  "system": {"volume": 60, "muted": false, "audioDevice": "{0.0.0.00000000}.{guid}", "display": 1, "keepAwake": true}
}
```

Поля `tracks.current*`, `chapters.names`, `system` могут отсутствовать, если уровень/`caps` их не дают.
`artUrl` отдаёт компаньон (проксирует `/art` VLC), авторизация — заголовок `Authorization: Bearer <token>`.

## События: `event`

`{"t":"event","name":"vlcStarted"|"vlcExited"|"inputChanged"|"endReached"|"paired", ...}`.

## Keep‑alive

`{"t":"ping"}` ↔ `{"t":"pong"}`; телефон шлёт каждые 30 с по WS (15 с по RFCOMM). Нет `pong`
за 10 с → переподключение.
