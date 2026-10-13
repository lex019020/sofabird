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
- RFCOMM (Bluetooth Classic): служба с UUID **`47fe5bd6-98ad-4d9f-9812-174b3394037d`**, имя в SDP — `VLC Remote`.
  Поток байт UTF‑8, одна JSON‑строка на сообщение, разделитель `\n`, не больше 1 МиБ на строку. Те же сообщения,
  рукопожатие и токен, что по WebSocket. Канал шифруется сопряжением Bluetooth, токен всё равно обязателен.
- Диагностика без WebSocket: `GET http://<host>:47555/info` → `{"name","version","protocol":1,"vlc":{...},"wsPort":47555,"needsPairing":bool}` (без авторизации, только для отображения в списке найденных ПК).

## Обнаружение

- DNS‑SD: тип службы `_vlcremote._tcp`, имя = имя ПК, порт 47555, TXT: `v=1`, `name=<имя ПК>`, `id=<стабильный GUID установки>`.
- UDP‑маяк: приложение шлёт broadcast на `255.255.255.255:47556` текст `VLCREMOTE_DISCOVER 1`;
  компаньон отвечает unicast JSON `{"t":"beacon","name":"...","id":"...","wsPort":47555,"protocol":1,"needsPairing":bool}`.

### Bluetooth

- Компаньон сообщает адрес своего Bluetooth‑адаптера в поле `bt` (`"AA:BB:CC:DD:EE:FF"`, заглавные, через `:`)
  в `/info`, `beacon` и `welcome`. Нет адаптера или Bluetooth выключен — поля нет.
- Телефон, подключавшийся к ПК по LAN, запоминает `bt` вместе с токеном. Если LAN недоступна (нет Wi‑Fi или ПК не
  отвечает ~3 с), а адрес известен и ПК есть среди сопряжённых Bluetooth‑устройств телефона, телефон подключается
  по RFCOMM к UUID службы с тем же токеном. Когда LAN снова доступна, возвращается на WebSocket (по RFCOMM выше
  задержка).
- Первое подключение только по Bluetooth: пользователь выбирает ПК из сопряжённых устройств телефона
  («Подключиться по Bluetooth»), дальше обычное `hello` → `pairing_required` → код на экране ПК → токен.
- Сопряжение Bluetooth‑устройств (телефон ↔ ноутбук) делают Windows и Android; компаньон его не инициирует.

## Общая форма сообщения

```json
{"t":"<тип>", "id":"<строка, опционально, для сопоставления ответа>", ...поля}
```

Ответы на запросы несут тот же `id`. Ошибки: `{"t":"err","id":"...","code":"<код>","msg":"..."}`.
Коды: `pairing_required`, `unauthorized`, `bad_request`, `vlc_not_running`, `vlc_error`, `unsupported`, `internal`.

## Рукопожатие и сопряжение

1. Телефон → `{"t":"hello","v":1,"device":{"name":"Pixel 8","id":"<uuid>","locale":"ru-RU"},"token":"<токен или null>","pairCode":"<6 цифр или null>"}`
   `device.locale` (необязательно) — язык интерфейса телефона (BCP 47); на нём компаньон пишет `err.msg`.
   Без него — по‑английски. Интерфейс самого компаньона на ПК следует языку Windows.
2. ПК → `{"t":"welcome","v":1,"name":"<имя ПК>","id":"<GUID>","caps":[...],"token":"<новый токен, только при успешном pairCode>","vlc":{...как в state.vlc}}`
   либо ошибка и закрытие соединения:
   - `{"t":"err","code":"pairing_required","msg":"..."}` — телефон не сопряжён (нет токена или токен неизвестен) и кода
     в `hello` нет. **Компаньон сам открывает окно с кодом** на экране ТВ/ноутбука (поверх всех окон, с именем
     телефона из `device.name`), если активного кода ещё нет; телефон показывает поле «Введите код с экрана ПК»
     и переподключается с `pairCode`.
   - `{"t":"err","code":"unauthorized","msg":"..."}` — код неверный или истёк (окно с кодом остаётся/обновляется),
     либо исчерпаны попытки.
3. Код живёт 5 минут и 5 попыток; при успешном сопряжении окно закрывается. Окно можно открыть и вручную из меню
   в трее («Сопряжение телефона…»). Токен — 32 байта base64url, ПК хранит SHA‑256. Повторное `pairing_required`
   от того же телефона, пока код активен, окно заново не создаёт (только поднимает поверх).

Совместимость: старые клиенты, получив `pairing_required`, ведут себя как при `unauthorized`.

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
| `listDisplays` | — → `{"t":"displays","id":..,"items":[{"index","id","name","kind","primary","current","preferred","bounds"}],"preferredId":string?}` | `windowControl` |
| `moveToDisplay` | `{"displayId": string}` (старое `{"display": int}` тоже принимается) | `windowControl` |
| `preferredDisplay` | `{"displayId": string \| null}` | `windowControl` |

Мониторы (`listDisplays`):
- `id` — **устойчивый** идентификатор монитора (путь устройства монитора из `DisplayConfigGetDeviceInfo` /
  `monitorDevicePath`; если его нет — имя GDI‑устройства `\\.\DISPLAYn`). Не меняется, когда ТВ выключают и
  включают, в отличие от `index`. Все новые команды работают по `id`.
- `name` — понятное имя из EDID (`monitorFriendlyDeviceName`, например «LG TV SSCR2»), если пусто — модель/
  «Экран N»; `kind` — `internal` (встроенная панель ноутбука) или `external` (HDMI/DP/…); `primary` — основной.
- `current` — сейчас здесь окно VLC; `preferred` — здесь VLC открывается по умолчанию (`preferredId` дублирует).

Поведение компаньона:
- `moveToDisplay` переносит окно VLC (снять полный экран → переместить → вернуть полный экран, если был) **и
  запоминает монитор как предпочтительный**. Если VLC не запущен — только запоминает, без ошибки.
- `preferredDisplay` — только запомнить (`null` — «не управлять»: компаньон не трогает окно VLC).
- Когда появляется новое окно VLC (запущенное компаньоном **или вручную**, например двойным кликом по фильму),
  компаньон переносит его на предпочтительный монитор, если он подключён (один раз на окно; если пользователь
  потом сам перетащит окно — компаньон его не возвращает). Если при запуске компаньоном включён `launchFullscreen`,
  окно разворачивается на весь экран уже на нужном мониторе.
- Предпочтительный монитор не подключён (ТВ выключен) — окно не трогается.
- В `state.system`: `display` (индекс, где сейчас VLC), `displayId` (id монитора, где сейчас VLC),
  `preferredDisplayId`.
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

`vlc.status` — состояние VLC на ПК (появилось в протоколе 1, необязательное; старые компаньоны его не шлют):

| status | Значение | `running` | Что показывает телефон |
|---|---|---|---|
| `notInstalled` | VLC не найден на ПК | false | «VLC не установлен на ПК» |
| `notRunning` | процесса нет | false | «VLC не запущен» + кнопка «Запустить» (`cmd launch`) |
| `starting` | запускается компаньоном | false | индикатор «VLC запускается…» |
| `noControl` | процесс есть, но веб‑интерфейс не отвечает с паролем компаньона (VLC открыт вручную до того, как компаньон его настроил) | false | «VLC открыт без управления» + кнопка «Перезапустить с управлением» (`cmd launch`) |
| `running` | полный контроль | true | пульт |

`running` по‑прежнему означает «можно управлять». Компаньон старается, чтобы `noControl` не возникал: пока VLC не
запущен, он прописывает в `%APPDATA%\vlc\vlcrc` веб‑интерфейс и RC со своим паролем, так что VLC, открытый
как угодно, сразу управляем. `cmd launch` при `noControl` перезапускает VLC с тем же файлом (позиция теряется).

Поля `tracks.current*`, `chapters.names`, `system` могут отсутствовать, если уровень/`caps` их не дают.
`artUrl` отдаёт компаньон (проксирует `/art` VLC), авторизация — заголовок `Authorization: Bearer <token>`.

## События: `event`

`{"t":"event","name":"vlcStarted"|"vlcExited"|"inputChanged"|"endReached"|"paired", ...}`.

## Keep‑alive

`{"t":"ping"}` ↔ `{"t":"pong"}`; телефон шлёт каждые 30 с по WS и каждые 15 с по RFCOMM. Нет `pong`
за 10 с → переподключение.
