# Прямой режим: HTTP‑интерфейс VLC 3.0.x (уровни 0/1)

Краткая выжимка для реализации. Подробности и ссылки на исходники —
[docs/research/vlc-protocols.md](../docs/research/vlc-protocols.md), живые замеры —
[docs/research/live-probe.md](../docs/research/live-probe.md), образцы ответов —
[docs/research/samples/](../docs/research/samples/).

## Соединение

- `http://<host>:8080/requests/status.json` (+ `playlist.json`, `browse.json`), Basic‑авторизация,
  **логин пустой**, пароль = `http-password` VLC. `/art` — обложка текущего элемента.
- Ответ `200` как `HTTP/1.1`, `Content-type: text/plain` (это JSON), `Cache-Control: no-cache`.
  Keep‑alive только если клиент **не шлёт заголовок `Connection`** (OkHttp добавляет
  `Keep-Alive` — убрать сетевым интерцептором). Idle‑таймаут сервера 10 с.
- `401` + `WWW-Authenticate: Basic realm="VLC stream"` — неверный пароль (или признак VLC при скане).
  `403` на `/` — пароль в VLC не задан. Connection refused — интерфейс выключен/порт.
  Таймаут — брандмауэр/другая сеть/ПК спит. Пустой ответ — VLC только стартует (повторить).
- Ошибки Lua приходят как `200` с HTML‑телом, содержащим `<title>Error loading` — считать ошибкой.
- Сервер однопоточный: один запрос в полёте на тип (status/playlist/browse), остальные коалесцировать.
- Параметры URL‑кодировать; `+` **не** декодируется как пробел (только `%XX`); `+` в `seek`/`volume` → `%2B`.

## `status.json` (apiversion 3)

| Поле | Тип | Примечание |
|---|---|---|
| `state` | `"playing"|"paused"|"stopped"` | |
| `time`, `length` | int, секунды | в `stopped` — 0 |
| `position` | float 0..1 | |
| `rate` | float | VLC квантует (`1.5` → `1.5015`) |
| `volume` | int 0..512 | 256 = 100 %; mute не отдаётся |
| `fullscreen` | **bool или 0** | `0` когда нет vout (stopped) |
| `currentplid` | int | `-1` в stopped |
| `audiodelay`, `subtitledelay` | float, секунды | |
| `aspectratio` | string | `"default"` или `"16:9"` |
| `random`, `loop`, `repeat` | bool | |
| `apiversion`, `version` | int, string | ветвиться по `apiversion` (4 — VLC 4) |
| `seek_sec` | int | шаг короткого прыжка из настроек VLC |
| `equalizer` | `[]` или объект | |
| `information` | объект | **отсутствует** в `stopped` |
| `information.chapter`, `.title` | int | текущие |
| `information.chapters`, `.titles` | int[] | индексы, без названий (в VLC 4 — `chapters_count`) |
| `information.category.meta` | объект | `title`, `filename`, `artwork_url`, теги контейнера |
| `information.category["Stream N"]` | объект | `Type` (`Video|Audio|Subtitle`), `Language`, `Description`, `Codec`, `Original_ID`?, … |

**ID дорожки** для `audio_track`/`subtitle_track`/`video_track` = `Original_ID`, если он есть в
`Stream N`, иначе `N`. Названия ключей категорий локализованы — распознавать по содержимому
(наличие `Type`), не по слову `Stream`. Текущая дорожка в `status.json` **не отдаётся**
(уровень 1 — `tracks.json`, см. ниже; уровень 0 — помнить последний выбор, сбрасывать при смене `currentplid`).

## Команды (`status.json?command=…`)

`pl_play[&id]`, `pl_pause[&id]` (toggle; из stopped запускает), `pl_forcepause`, `pl_forceresume`,
`pl_stop`, `pl_next`, `pl_previous`, `pl_delete&id` (ненадёжно), `pl_empty`, `pl_random`, `pl_loop`,
`pl_repeat` (все три — toggle), `fullscreen` (toggle), `volume&val=<0..512|%2BN|-N>` (процентная форма
не работает), `seek&val=<sec|%2BN|-N|N%25>`, `rate&val=`, `audiodelay&val=`, `subdelay&val=`,
`aspectratio&val=`, `title&val=`, `chapter&val=`, `audio_track&val=`, `video_track&val=`,
`subtitle_track&val=` (`-1` — выключить), `in_play&input=<uri>[&option=…]`, `in_enqueue&input=<uri>`,
`addsubtitle&val=<uri>`, `key&val=<hotkey без префикса key->` (например `nav-up`, `subtitle-toggle`,
`jump-short-forward`; ошибок не возвращает), `snapshot`.
Закрыть VLC: `in_play&input=vlc://quit`. Ответ на любую команду — свежий `status.json`.

## `playlist.json`

Дерево: корень `{"type":"node","id":"0","children":[…]}`, узел `Playlist` (`id` `"1"`) содержит
листья `{"type":"leaf","id":"3","name","uri","duration":int,"current":"current"?}`. `id` — строки.
Узел `Media Library` игнорировать.

## `browse.json?uri=<file URI>`

`{"element":[{"type":"dir"|"file"|"unknown","name","path","uri","size","modification_time",…}]}`.
`uri=file:///` → диски (`C:\`, `D:\`). Первый элемент в папке — `..` (пропускать, строить «вверх»
самостоятельно). Пути Windows с обратными слэшами в `path`, нормальные URI в `uri` — использовать `uri`.

## Уровень 1: `tracks.json` и `setvar.json`

Если в `%APPDATA%\vlc\lua\http\` установлены страницы из `companion/lua/`:

- `GET /requests/tracks.json` → `{"vlcremote":1,"audio":{"current":4,"items":[{"id":1,"name":"… - [Russian]"}]},
  "spu":{…},"video":{…},"chapter":{"current":2,"items":[{"id":0,"name":" Prologue"}]},"title":{…},
  "stereo_mode":{…},"aspect":{…},"crop":{…},"zoom":{…},"deinterlace":{…}}`. Поля `null`, если
  объекта нет. `404` → уровень 0. Имена дорожек содержат язык в конце в квадратных скобках.
- `GET /requests/setvar.json?obj=input&name=audio-es&val=5` → `{"ok":true,"current":5}`.

## Опрос

1 с при `playing`, 3 с при `paused`/`stopped`, не опрашивать в фоне без foreground‑сервиса.
Позицию интерполировать локально. После команды применить ответ сразу и запросить «чистый»
статус через 500 мс. Порядковые номера запросов, устаревшие ответы отбрасывать.
