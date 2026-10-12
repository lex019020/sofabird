# Prior art: существующие пульты и библиотеки для управления VLC

Дата: 2026-10-12. Цель — собрать опыт существующих open-source (и нескольких закрытых) решений, чтобы спроектировать современный Android-пульт для VLC на Windows-ПК (домашний кинотеатр: ПК подключён к телевизору).

Связанные документы: [live-probe.md](live-probe.md) (живая проверка HTTP/RC-интерфейсов VLC 3.0.23 на этом ПК; ниже на него есть ссылки там, где он подтверждает или опровергает предположения из чужого кода).

## 0. Метод и источники

Репозитории клонированы `git clone --depth 1` в scratch-каталог
`...\scratchpad\prior-art\<repo>` (из-за лимита длины пути Windows клонирование шло через короткий staging-каталог, затем `mv`; читать удобно через junction `C:\Users\Alexey\AppData\Local\Temp\pa\<repo>`). Код только читался (`cat`/`grep`/`sed`/`find`), ничего из клонов не запускалось. Даты «последний коммит» — из `git log -1` клона; звёзды/лицензия — из GitHub API на дату документа.

| Клон (подкаталог) | Репозиторий | Последний коммит |
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
| `_fetched/` | отдельные файлы: `httprequests.lua`, `common.lua`, `http.lua`, `requests/README.txt` ветки 3.0.x; `python_vlc_http/vlc.py`; скрипты Unified Remote `Main/VLC/remote_win.lua`, `Main/VLC Web/remote.lua`; HTML страниц Play Store | — |

Что не удалось проверить (помечено ниже как «не проверено»):
- `wiki.hobbyistsoftware.com` не резолвится (страница «VLC connection troubleshooting» недоступна; web.archive.org из этой среды не читается). Использованы `hobbyistsoftware.com/vlc`, `/vlc-more`, `/VLCSetup-win-manual`, `/vlc-versions`, `blog.hobbyistsoftware.com/vlc-remote-troubleshooting/`, `/wrong-wifi/`.
- Исходники Android-части KDE Connect (`MprisPlugin.kt`) не скачались (invent.kde.org отдаёт 403, зеркало на GitHub — 404); использован README desktop-плагина `mpriscontrol`.
- `code.videolan.org` закрыт Anubis-защитой — issue-трекер VLC не читался.
- Отзывы Play Store не парсились (только описание, рейтинг, число установок, дата обновления).
- Оригинальный репозиторий Peter Baldwin (`code.google.com/p/android-vlc-remote`) мёртв; изучен форк samicemalone.
- «Remote for VLC» от MetaTiger — такой проект не найден ни в GitHub, ни в сторах; вероятно, ошибочное название.

## 1. Карта протоколов VLC (контекст для сравнения)

### 1.1 HTTP-интерфейс (Lua `http`, порт 8080)

Источник: `vlc/share/lua/intf/http.lua`, `vlc/share/lua/intf/modules/httprequests.lua`, `vlc/share/lua/http/requests/README.txt` (master) и те же файлы ветки 3.0.x (`_fetched/`).

- Эндпоинты: `/requests/status.{xml,json}` (статус + команды через `?command=`), `/requests/playlist.{xml,json}`, `/requests/browse.{xml,json}?uri=<file-uri>` (или устаревший `?dir=`), `/art` и `/art?item=<id>` (обложка), `/requests/vlm*.xml`, в master ещё `rd.{xml,json}` (renderer discovery — Chromecast). Web-UI: `index.html`, `mobile.html`, `mobile_browse.html`, `mobile_equalizer.html`, `mobile_view.html`.
- Аутентификация: HTTP Basic, логин пустой, пароль `--http-password`. Если пароль не задан, Lua-интерфейс вообще не поднимает страницы:
  ```lua
  -- vlc/share/lua/intf/http.lua
  password = vlc.var.inherit(nil,"http-password")
  h = vlc.httpd()
  if password == "" then
      vlc.msg.err("Password unset, insecure web interface disabled")
      vlc.msg.info("Set --http-password on the command line if you want to enable the web interface.")
      p = h:handler("/",nil,nil,callback_nopassword,nil)   -- отвечает "Status: 403"
  ```
  Следствие для диагностики: **403 на `/` = пароль не задан в VLC**, **401 = пароль задан, но не совпал**. Это же (с 2.1.0) описано в wiki `Documentation:Modules/http_intf`: до 2.1 был ACL-файл `.hosts` (403 при доступе не с localhost), с 2.1 — только пароль.
- `--http-host` (по умолчанию все интерфейсы) и `--http-port` — опции ядра; `config.host` в Lua игнорируется с сообщением «Pass --http-host=IP ... on the command line instead». Т.е. интерфейс *можно* привязать к `127.0.0.1` (так делают Unified Remote «VLC Web» и vlc-mcp-server, у которых клиент живёт на том же ПК), и тогда телефон его не увидит.
- Ошибки команд не сигнализируются: неизвестная команда/хоткей → `200` (см. live-probe). Ошибка генерации страницы — HTML `<title>Error loading /requests/...</title>` с кодом 200 (VlcFreemote это распознаёт текстовым поиском, см. §2.1).
- Семантика ключевых команд (`httprequests.lua`, одинаково в 3.0.x и master):
  - `pl_pause` без `id`: если `stopped` — `vlc.playlist.play()`, иначе toggle pause; `pl_forcepause`/`pl_forceresume` идемпотентны; `pl_play&id=` — `gotoitem(id)`.
  - `seek&val=` — `common.seek`: суффикс `%` → относительный/абсолютный seek по позиции (`+10%`, `50%`), иначе время через `common.parsetime` (`+1H:2M`, `-10`, `1000`). URL-кодировать `+` (`%2B`) и `%` (`%25`). В live-probe `val=50%25` по волюму не сработал, по seek процентные формы используют VlcFreemote и Unified Remote.
  - `volume&val=` — `common.volume`: `+N`/`-N` относительное, иначе `vlc.volume.set(tostring(value))`; шкала 0..512, 256 = 100 % (подтверждено live-probe). Форма `N%` упомянута в README, но в `common.volume` ветки 3.0.x и master обрабатывается только знак `+`/`-` — процент передаётся как есть в `vlc.volume.set` (поведение не проверено, лучше не использовать).
  - `key&val=<hotkey>` — `common.hotkey("key-"..val)` → `vlc.misc.action_id`; хоткеи `audio-track`, `subtitle-track`, `subtitle-toggle`, `aspect-ratio`, `crop`, `chapter-prev/next`, `nav-*`, `subdelay-up/down`, `audiodelay-up/down`, `toggle-fullscreen` и т.д. Именно так все старые Android-пульты «переключают дорожки» (циклически).
  - `audio_track`/`subtitle_track`/`video_track&val=<id>` — в 3.0.x `vlc.var.set(vlc.object.input(),"audio-es",val)`; в master `vlc.player.toggle_audio_track(val)`. `status.json` ID дорожек **не отдаёт**, только категории `"Stream N"`; что `N` == ES-id в VLC 3.0.23 — подтверждено экспериментально в live-probe. В VLC 4 идентификаторы дорожек в `libvlc_player` строковые (`"audio/1"`), поэтому числовой `val` там, вероятно, перестанет работать — не проверено.
  - `in_play&input=<uri>&option=<opt>` принимает item-опции (`noaudio`, `novideo`, в vlc-mcp-server — `sub-track=<id>`); `in_enqueue`; `addsubtitle&val=<uri>`; `aspectratio&val=16:9`; `rate&val=`; `audiodelay`/`subdelay&val=<сек>`; `chapter&val=`, `title&val=`; `snapshot` (скриншот сохраняется на ПК, клиенту не отдаётся); эквалайзер `enableeq`, `preamp`, `equalizer&band=`, `setpreset`; `pl_sort`, `pl_random/loop/repeat` (только toggle, параметра состояния нет); `fullscreen` (toggle, только при наличии vout).
  - README прямо предупреждает: «`pl_delete` is completly UNSUPPORTED» (и тем не менее все пульты им пользуются).
- `browse`: `dir=""` на Windows возвращает список дисков; `dir="X:\.."` специально мапится в `""`; `uri=file:///` даёт то же (live-probe); `~` → домашняя папка. Элементы: `type` (`dir`/`file`/`unknown`), `path`, `uri`, `name`, `size`, `modification_time`, `mode`, `..`.
- **Различия apiversion 3 (VLC 3.0.x) и 4 (master/4.0-dev)**, по diff `getstatus`:
  - `information.chapters`/`titles` (массивы индексов) заменены на `chapters_count`/`titles_count` — клиенты, типизировавшие `chapters: number[]` (node-vlc-client `src/Types.ts`), на VLC 4 сломаются.
  - убран `pl_sd` (services discovery); добавлены `set_renderer`/`unset_renderer` и `rd.json` (Chromecast из HTTP).
  - `fullscreen` без vout — число `0`, с vout — boolean (и в 3.0.x, и в master; RagePeanut уже обходит это: «fullscreen param is not instantly initialized»).
  - В NEWS master: «Support HTTPS in the Lua HTTP interface when --http-cert is set»; web-интерфейс (`share/lua/http`) в 4.0-dev сохранён, но Qt-GUI и Lua API переписаны (`vlc.player.*` вместо `vlc.input`).
- Встроенный `mobile.html` (jQuery UI): play/pause/stop/prev/next, перемотка ±, fullscreen, слайдер позиции, слайдер громкости (`value = volume/5.12`, подпись `volume/2.56 %`, т.е. максимум слайдера = 512), обложка, «Open» (обзор файлов), эквалайзер, Stream Output. Опрос `updateStatus` раз в 1000 мс (500 мс после команды). **Выбора аудио/субтитров в web-UI нет** (grep по `audio_track|subtitle_track` в `http/` пуст).

### 1.2 RC/telnet (Lua `cli`, порт 4212)

Источник: `vlc/share/lua/intf/cli.lua`, `aiovlc/src/aiovlc/model/command.py`, `vlcrc/src/vlcrc/vlc_remote_control.py`, live-probe.

- Текстовый протокол; с паролем (`--telnet-password`) — приглашение `Password:` и ответ `Welcome`/`Wrong password` (aiovlc `Password.parse_output` ищет эти подстроки).
- Команды: `status` → три строки `( new input: <uri> )`, `( audio volume: N )`, `( state X )` (aiovlc парсит их позиционно через `split(" ")`); `get_time`, `get_length`, `info` (блоки `+----[ Category ]` / `| key: value`), `atrack|vtrack|strack [id]` — **список дорожек с ID, названием, языком и `*` у текущей**, `adev` — аудиоустройства, `volume`, `seek`, `add/enqueue/clear/next/prev/random/repeat/loop`, `quit`:
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
- Push-события (`status change: ( play state: 3 )` и т.п.) — есть только здесь.
- Минусы, которые видны по чужому коду: одно долгоживущее TCP-соединение (HA-форум: «unrecoverable broken socket» после рестарта VLC/сети, пришлось перезапускать HA), vlcrc обходит это, открывая **новый сокет на каждую команду** и делая `shutdown(1)`; на Windows интерфейс называется `rc` (`--extraintf rc --rc-host=HOST:PORT`), на Linux `oldrc --rc-fake-tty` (README vlcrc).

### 1.3 VLC for Android «Remote access» — почему не подходит

VLC for Android 3.6.0 (январь 2025) добавил «Remote access»: внутри *Android-приложения* поднимается Ktor/Netty-сервер (`application/remote-access-server/build.gradle`: `ktor-server-netty`, `-websockets`, `-sessions`, `-auth`, `-cors`, `ktor-network-tls-certificates`, bouncycastle), выдаётся URL + одноразовый код, по HTTPS с self-signed сертификатом браузер с другого устройства управляет **этим телефоном/планшетом**: просмотр медиатеки, запуск воспроизведения на устройстве, обмен файлами. Это серверная часть библиотеки `org.videolan.android:remote-access`, которой нет в desktop VLC; к управлению VLC на Windows она отношения не имеет. Полезна только как UX-референс: пейринг по OTP, страница «как принять сертификат». Страница docs.videolan.me `.../more/remoteaccess/index.html` на дату документа содержит лишь заголовки («Availability», «How does it work?») без текста.

### 1.4 Unified Remote (закрытый сервер, открытые скрипты пультов)

Репозиторий `unifiedremote/Remotes` (лицензия не проверена):
- `Main/VLC/remote_win.lua` — **эмуляция клавиатуры**: `win.switchtowait("vlc.exe")` затем `keyboard.stroke("space")`; статус = заголовок окна `vlc.exe`, опрос `timer.interval(actions.update, 500)`; умеет запустить VLC (`os.start("C:\\Program Files\\VideoLAN\\VLC\\vlc.exe")`). Требует фокуса окна и не знает состояния.
- `Main/VLC Web/remote.lua` — HTTP: `GET /requests/status.xml?command=...`, Basic `":"..password`, `settings.prop`: `host: localhost`, `port: 8080`, `password: vlcremote`. Опрос 500 мс (`libs.timer.timeout(update_status, 500)`), `/art` при смене заголовка, библиотека из `playlist.xml`, seek процентами с комментарием «Seeking precision is only 1%», флаг `seeking` на время перетаскивания. При неудаче показывает диалог-инструкцию: включить Web в Main interfaces, «VLC 2.1+ you MUST specify a password ... 'vlcremote'», перезапустить VLC.

## 2. Android-приложения

### 2.1 VlcFreemote (nicolasbrailo/VlcFreemote)

- Стек: Java, AndroidX, `minSdk 15`, `targetSdk 30`, без сторонних библиотек (`HttpURLConnection` + свой XML-парсер `XmlMogrifier`). Лицензия GPL-3.0-or-later. 23★, 17 открытых issue. Последний коммит 2021-08-21; F-Droid 1.15 (январь 2021). Не в Google Play («лень и жадность», README).
- Подключение: только HTTP, причём **XML** (`requests/status.xml`, `browse.xml?dir=`, `playlist.xml`), каждая команда — `status.xml?command=...` и ответ парсится как статус.
- Обнаружение ПК: TCP-скан /24 по 8080 и 22 (`net_utils/ServerScanner.java`): последовательно 1..254 для каждого IPv4-интерфейса, таймаут 100 мс, IPv6 отбрасывается; найденный SSH-порт сохраняется «на будущее» (автозапуск VLC по SSH так и не реализован — TODO в README). Ручной ввод IP:порт; сервера запоминаются в SQLite (`local_settings/RememberedServers.java`: `ip, port, display_name, pass, last_used, last_path`; пароль в открытом виде, `// TODO: Is it worth encrypting this?`).
- Опрос/согласование: статус приходит в ответе на любую команду; периодический опрос только когда `isPlaying()` — цепочка `postDelayed(..., PERIODIC_VLC_STATUS_UPDATE_DELAY = 2500)` (`MainActivity.java:36`). Команды сериализуются: одна в полёте, остальные в `PriorityQueue` по приоритету; **низкоприоритетные (`CanIgnore`: статус, громкость, seek) не копятся — хранится только последняя** (`vlc_connector/RemoteVlc.java`):
  ```java
  private void queueCommand(final VlcCommand cmd) {
      if (cmd.getPriority() == VlcCommand.Priority.CanIgnore) {
          lowPriorityCommand = cmd;      // слайдер "спамит" — выполнится только последнее
      } else {
          pendingCommands.add(cmd);
      }
  }
  ```
  Оптимистичных обновлений нет: UI обновляется из ответа.
- Аутентификация/ошибки: `Authorization: Basic base64(":"+pass)`; таймауты 10 с; `401` → `onAuthError()` → переход на экран серверов + toast «The supplied password is not valid»; `IOException` → toast «Could not connect to VLC at %s. Please select a new server.»; любой не-200/ошибка парсинга статуса → `onVlcStatusFetchError()` → тоже «переподключитесь». Диалог пароля показывается при выборе сервера (с подстановкой сохранённого).
- Обзор файлов: стартовый путь `"/"` (`model/VlcPath.java: VLC_DEFAULT_START_PATH = "/"`), ошибка → повтор с `"/"`, затем фатальная `VlcPath_ApplicationError` («the default VlcPath (/) is not valid on your setup»). **На Windows это краш**: issue #21 — стек `VlcPath$2.onContentError ← Cmd_DirList$1.onHttpNotOkResponse ← ...onResponse`, т.к. `browse.xml?dir=/` отдаёт HTML-ошибку; issue #6 «Can't get the directory listing for Win 7»; #22 — краш при подключении к 3.0.23 (автор: «похоже, только у Windows-пользователей»). Детект ошибки — по тексту:
  ```java
  // vlc_connector/Cmd_DirList.java
  if (result.contains("<title>Error loading /requests/browse.xml</title>")) { onHttpNotOkResponse(); return; }
  ...
  case "uri": object.path = value.substring("file://".length()); break;
  ```
  Фичи обзора: закладки на папки (per-server), «уже проигрывалось» (история 1000 файлов, подсветка и ручное переключение), случайный вложенный каталог, запоминание последнего пути per-server.
- Дорожки: только циклические хоткеи, без списка:
  ```java
  // vlc_connector/Cmd_CycleAudioTrack.java
  public String getCommandPath() { return "requests/status.xml?command=key&val=audio-track"; }
  // vlc_connector/Cmd_CycleSubtitle.java
  public String getCommandPath() { return "requests/status.xml?command=key&val=subtitle-track"; }
  ```
- Seek: `SeekBar` 0..100 % → на каждом `onProgressChanged(fromUser)` шлёт `seek&val=<pct>%25` (без debounce — спасает только коалесценция очереди); кнопки «automagic movie-skip» ±0.5 % длины (`Cmd_JumpRelativePercent(±0.5f)` → `seek&val=+0.50%25`).
- Громкость: слайдер 0..100 с пересчётом `4*percent` / `vlc/4` (`model/VlcStatus.java`) — т.е. «100 %» в приложении = 400 = 156 % реальной громкости, а 256 (0 dB) оказывается на 64 %. Пример неправильной шкалы.
- Плейлист: `playlist.xml` → `leaf` (`id, uri, name, duration`), `pl_play&id`, `pl_delete`, `pl_empty`, `in_enqueue`. Известная дыра: после `in_enqueue` в состоянии `stopped` воспроизведение не стартует (TODO в `MainActivity.onAddToPlaylistRequested`: VLC «помнит» последний элемент, `pl_pause` его и запустит).
- Прочее: виджет на рабочий стол, компактный лэйаут, голландская локализация. Нет: уведомления, аппаратные кнопки громкости (issue #5 FR), таймер сна, WoL.

### 2.2 android-vlc-remote («Remote for VLC», Peter Baldwin; форк samicemalone)

- Стек: Java, проект эпохи Eclipse (без Gradle), `minSdk 14`, `targetSdk 19`, версия 0.7.5 (versionCode 66), Apache HttpClient-классы для Basic. GPL-3.0. 46★, 30 форков, 10 issue. Последний коммит 2014-07-08. Тестировалось с VLC 1.1–2.2 nightlies. Оригинал (`org.peterbaldwin.client.android.vlcremote`) был на Google Code — мёртв.
- Подключение: HTTP; **сначала `status.json`, при 404 падает на `status.xml`** (`service/StatusService.java: USE_XML_STATUS`, сброс при смене сервера) — поддержка VLC 1.x без JSON; `playlist.xml?search=`, `browse.xml?dir=`, `/art`.
- Обнаружение: `sweep/PortSweeper.java` — HTTP `GET /requests/status.xml` к хостам /24 в порядке «ближайшие к своему IP первыми» (`delta` 1..127 в обе стороны), N потоков, таймаут 1 с; **сохраняет код ответа (200/401/403) и показывает сервер с иконкой «нужен пароль»/«запрещён»**; обратный DNS-lookup для имени (`address.getHostName()`); реагирует на смену Wi-Fi. Есть «Test connection» перед добавлением (`net/ServerConnectionTest.java`: 1 с, toast по 200/401/403). Сервера с никнеймами, без показа пароля в списке.
- Опрос/согласование: `fragment/StatusFragment.java` — таймер 1 с → `status().programmatic().get()`; все запросы идут через `StatusService` на отдельных `HandlerThread` (status / command / art / remoteviews), **порядковые номера отбрасывают устаревшие ответы**, при поступлении команды отложенные статус-запросы снимаются, для абсолютных `seek`/`volume` снимаются и более ранние команды того же типа; после команды через 500 мс делается «чистый» `status` (`sendMessageDelayed(..., 500)`); `loop`/`repeat` после toggle выдерживают `DELAY=500` через `AlarmManager`. Оптимистичных апдейтов нет (кроме локального кеша громкости для клавиш).
- Ошибки: `ACTION_ERROR` broadcast, после ошибки при следующем удачном статусе перезагружается вкладка обзора; известная проблема #2 — зацикленный toast «No files were found» каждые пару секунд при недоступности сервера.
- Обзор файлов: `net/xml/XmlDirectoryContentHandler.java` определяет Windows по первому `path`, не начинающемуся с `/`, переводит `/`→`\`, корень Windows = `""` (список дисков), прячет `..` в корне, нормализует `a/../b`; «Libraries» — виртуальные папки из нескольких каталогов (аналог библиотек Windows), «Home» и «Set as Home», сортировка «папки первыми», MRL через `Uri.fromFile`, картинки через `fake://`; стриминг на телефон через `:sout=#transcode{...}:rtp{sdp=rtsp://0.0.0.0:5554/...}`; `pl_sd` для services discovery.
- Дорожки: хоткеи через `command=key&val=` (`model/Hotkeys.java`: `audio-track`, `subtitle-track`, `crop`, `aspect-ratio`, `toggle-fullscreen`, `chapter-prev/next`, `subdelay-up/down`, `audiodelay-up/down`), настраиваемая панель из 5 кнопок; из `status` читаются только типы `Stream N` (`Track.addStreamType`), чтобы знать, есть ли субтитры. Issue #4 «change subtitle track» (выбор конкретной дорожки/выключение) — открыт с 2014.
- Seek: `SeekBar` с `max = length` (секунды), отправка `seek&val=<sec>` на `onStopTrackingTouch` (и в `onProgressChanged`); кнопки ±`seek_time` (настройка, по умолчанию 10 с); DPAD ±3 с с Shift; клавиатура (space/s/p/n/f/m).
- Громкость: `MAX_VOLUME = 512`, слайдер в `VolumeFragment`, аппаратные клавиши ±20 (`volume&val=%2B20` или локально `mVolumeLevel±20`), mute помнит последнее ненулевое значение.
- Плейлист: `current` из `playlist.xml`, поиск, удаление, парсинг имени файла (сериал/фильм, regex). Issue #3: подсветка не той дорожки — VLC отдаёт неверный `name` для некоторых .mkv; README: до 2.1 `current` ставился по имени, а не по id.
- Прочее: уведомление с кнопками, виджет, пауза при входящем звонке и авто-возобновление (`receiver/PhoneStateChangedReceiver.java`), обложка `/art` (автор отправил патч в VLC 2.1.3 для обложек на Windows, ticket #7607), DVD-навигация (`nav-*`), пресеты задержки субтитров/звука одной кнопкой (`DelayPresetButton` → `subdelay&val=`), скрытие вкладки DVD.

### 2.3 VLC-TV-Remote (CloneTV)

- Стек: Java, Android TV (API 24+), Retrofit/OkHttp. MIT. 14★. Последний коммит 2021-03-07. В репозитории **нет исходников самого ATV-приложения** — только APK в релизах, модуль пульта для телефона/планшета (`source/remote.phone-tablet`), общие константы и библиотека trakt.tv.
- Архитектура: телефон → собственный JSON-API ATV-приложения (`cmd/GET_STATUS/`, `cmd/{target}/{opt}/`, `RemoteInterface.java`), ATV-приложение → HTTP VLC (константы `DataTagVlcCommand`: `pl_play`, `in_play`, `pl_pause`, `seek`, `volume`, `fullscreen`; `DataTagVlcStatus`: `currentplid`, `apiversion`, `state`...). Т.е. «пульт для пульта».
- Фичи: «automatic find VLC server in local network» (способ не виден — не проверено), ambilight-фон, расширенный плейлист с постерами/описаниями из NFO (Kodi/tinyMediaManager) через Lua-аддон `VLC AddOn MediaPlayList` на ПК («tested VLC 3.0.8, Lua 5.1.5 32-bit»), история просмотров, Trakt.tv-скробблинг, EPG, устройства могут быть в разных сетях (маршрутизация).
- Вывод: интересен идеей медиатеки с постерами поверх VLC и trakt, но код управления VLC закрыт.

### 2.4 Закрытые, но важные конкуренты

**VLC Remote (Hobbyist Software, iOS/Android, Rob Jonson).** Автор — соавтор `httprequests.lua` и `status.json`/`browse.json` в самом VLC (шапки файлов `vlc/share/lua/http/requests/status.json`: «Authors: Rob Jonson <rob -at- hobbyistsoftware -dot- com>»), в 2013 на vlc-devel просил сохранять HTTP-интерфейс активным без пароля и возвращать внятную ошибку, «the HTTP remote interface is used by a lot of people». iOS: 4.8★ (777 оценок), Android: 4.5★, 50K+ установок, обновлено 2026-08-12. Фичи (App Store/Google Play, `/vlc-more`): автопоиск VLC в сети, обзор файлов и внешних дисков, плейлист, DVD, субтитры/аудиодорожки/задержки, aspect ratio, эквалайзер, обложки, пикер для точной подстройки громкости/позиции, виджет Today. VLC 2.0+, упоминаний VLC 4 нет (не проверено). Таймер сна/WoL в описании отсутствуют.
Setup Helper (Windows/Mac): включает Web-интерфейс, ставит пароль (по умолчанию `vlcremote` — тогда «the remote connects automatically without re-entering it», `/VLCSetup-win-manual`), **правит правило Windows Firewall** («если сторонний фаервол — разрешите VLC сами»), пока запущен — анонсирует Bonjour-сервис (iOS ищет компьютеры через Bonjour; без сервиса приложение подключается по сохранённому IP). Индикатор «traffic cone» = VLC запущен. Руководство по ручной настройке: Interfaces → Main interfaces → Web, Lua → Lua HTTP password, перезапуск VLC, фаервол «VLC checked for both public and private networks».
Troubleshooting (`blog.hobbyistsoftware.com/vlc-remote-troubleshooting/`, `/wrong-wifi/`): обновите VLC; запустите helper («much more reliable than manual setup»); телефон и ПК в одной сети («phones can join other nearby networks»); helper нужен каждый раз → нет Bonjour-сервиса (включите общий доступ к файлам/принтерам или сохраните компьютер из «Found Computers»); после перезагрузки не подключается → сменился IP (DHCP-резервация); гостевая сеть роутера, 5 GHz-сеть с другим именем, Wi-Fi-репитер, отельный Wi-Fi, соседская открытая сеть — всё блокирует связь устройств между собой; первым делом перезагрузить роутер.

**VLC Mobile Remote — PC & Mac (Adarsh Urs, Android).** 1M+ установок, 4.1★, обновлено 2026-09-27. Без ПО на ПК («or alternatively, install our helper app, VMR Connect»); «Setup Wizard»; автоподключение в той же Wi-Fi; Wear OS; аудиодорожка, субтитры и добавление `.srt` (VLC ≥ 2.2), скорость, crop, aspect, DVD, жесты, обзор всех файлов ПК, плейлист, online-ссылки, скриншоты кадра, «Home location», избранные папки, виджет/уведомление/экран блокировки, мягкая пауза при звонке, аппаратные кнопки громкости, **переключение аудиоустройств вывода**, сортировка списка, несколько сохранённых хостов, «Close/Quit VLC from the App». С VMR Connect: мышь/клавиатура, **Shutdown/Restart/Sleep ПК**, управление браузером/Netflix. Дисклеймер: «This App do not support VLC for Android app». Форум VideoLAN 2022: «connects to linux vlc but does nothing» — разработчик VLC предлагает проверить web-интерфейс в браузере телефона (стандартный первый шаг диагностики).

## 3. Flutter / React Native: RagePeanut/vlc-remote

- Стек: монорепо — `vlc-remote-app-flutter` (Dart, Dio, MobX), `vlc-remote-app` (React Native, незаконченный порт), `vlc-remote-companion` (Electron + React, на ПК), `vlc-remote-api` (TypeScript, TMDb-парсинг имён файлов). Файла LICENSE нет; README: «free to do whatever you want ... even if you want to make money». Проект заброшен (2022-01-27), 0★.
- Подключение: HTTP JSON (`status.json`, `playlist.json`, `browse.json?uri=`), Basic `":"+password`, `connectTimeout 5000`:
  ```dart
  // vlc-remote-app-flutter/lib/services/vlc.dart
  static const int MAX_VOLUME = 320;
  static const Duration STATUS_REFRESH_INTERVAL = const Duration(milliseconds: 200);
  static const Duration PLAYLIST_REFRESH_INTERVAL = const Duration(milliseconds: 500);
  ...
  Timer.periodic(PLAYLIST_REFRESH_INTERVAL, (timer) => _playlistRequest());
  Timer.periodic(STATUS_REFRESH_INTERVAL, (timer) => _statusRequest());
  ```
  **Опрос статуса 5 раз в секунду + плейлист 2 раза в секунду** — агрессивно для Wi-Fi/батареи. Любая ошибка кроме таймаута → `_status.setOff(); _files.reset()` (сброс состояния «VLC выключен»).
- Обнаружение: **QR-код**, который компаньон показывает на экране ПК (`vlc-remote-companion/src/screens/HomeScreen.js`: `ip=...\npw=...\nvp=<vlc port>\ncp=<companion port>\nnm=<name>`), парсер `Computer.fromQRData`; либо ручной ввод (IP-маска `ip_text_input_formatter.dart`).
- Дорожки: хоткеи `key&val=audio-track|subtitle-track|video-track` (кнопки `AudioTrackButton`, `SubtitleTrackButton`, `VideoTrackButton`).
- Seek/главы: `seek&val=+N/-N`, `chapter&val=` с клиентским счётчиком (`chapter+1 < chapterCount`, где `chapterCount = information.chapters.length` — поле, которого нет в VLC 4).
- Громкость: аппаратные кнопки телефона через `volume_watcher` → `VLC.volume((320/maxVolume)*vol)` — максимум 320 (125 %).
- Воспроизведение файла: если `uri` нет в плейлисте → `in_enqueue`, затем `pl_play&id=<найденный id>` (ищет `lastWhere(uri == file.uri)`), т.е. **дубликаты не плодит**.
- Компаньон: `open_vlc`/`close_vlc` («power»), `switch_screen` (перенести окно VLC на другой монитор — актуально для ТВ), `extra_info` (стрим метаданных).
- Особенности: обход `fullscreen` типа `bool|int`; история каталогов per-computer; TMDb-постеры и переводы названий.

## 4. Библиотеки и интеграции

### 4.1 node-vlc-client (TypeScript, MIT, 20★, 2022-11)
- `status.json`/`playlist.json`/`browse.json?dir`/`/art?item`, Basic с username:password, любой не-200 → `throw new Error("Request error | Code ...")`. Автопуллинга и discovery нет (`ServerFinder.ts` — пустая заглушка `find(ip, ipClass=24, port=8080){}`).
- Дорожки — единственная библиотека, которая строит список из `Stream N`:
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
  `Types.ts`: `VlcStream { Type, Language?, Codec? }`, `AudioTrack { Channels, Bits_per_sample, Sample_rate }` (пробелы в ключах VLC заменяет на `_`). Текущую выбранную дорожку определить не может.
- Громкость: `getVolume = volume/512*100`, `setVolume = 512*v/100` — «100 %» = 200 % реальной; комментарий в коде честно говорит «256 is 100% and 512 is 200%».
- `playFile(..., {wait, timeout})` — после `in_play` опрашивает статус до смены файла (до 3 с).

### 4.2 aiovlc + Home Assistant `vlc_telnet` (Python, Apache-2.0, активно поддерживается)
- Telnet 4212, `asyncio.open_connection`, таймаут 10 с, `asyncio.Lock` на команду, фильтрация telnet IAC-байтов. Парсинг — позиционный/регулярками (`model/command.py`): `Status` → `audio_volume = int(float(output[0].split(" ")[3]))`, `state = output[1].split(" ")[2]`; `Info` → секции `+----[ ... ]`; `Password` → «wrong password»/«welcome».
- HA `media_player.py`: `MAX_VOLUME = 500` (ещё одна шкала), `volume_level = audio_volume/500`; состояние `playing/paused → PLAYING/PAUSED`, иначе `IDLE`; `media_position_updated_at` фиксируется только при изменении `time`; заголовок из `info` (`title`, `now_playing`, иначе `filename` с отрезанием `?authSig=`); mute = запомнить громкость и выставить 0; при недоступности — переподключение на каждом `async_update`, `AuthError` → перезагрузка config entry (reauth-поток «Please enter the correct password for host»); `play_media` → `add <url>` (для TTS); `config_flow`: host по умолчанию `localhost`, порт 4212, пароль обязателен, ошибки `cannot_connect`/`invalid_auth`.
- Компонента `vlc` (python-vlc, локальный) в текущем dev-дереве HA нет — остался только `vlc_telnet`.
- Форум HA (`community.home-assistant.io/t/remote-vlc/10951`): телнет-интеграция «buggy and frustrating» из-за необратимо сломанного сокета → самодельные HTTP-версии:
  - `gambalaya/vlc_remote` (kingo55 → jambalaya): `status.xml`, `volume/256`, mute = `volume&val=0`/`256` (не восстанавливает прежний уровень), `TIMEOUT 10`; README показывает, что то же достижимо штатными `rest_command` + `rest` sensor + `universal` media player.
  - `jeena/vlc_remote` (`vlc_http.py`): `requests.get(url, auth=("", password), timeout=1)`, копия `vlc_telnet` с заменой бэкенда.

### 4.3 python-vlc-http (MatejMecka, MIT, 0.0.11 / 2023-04, последний push 2024-01)
`status.json`/`browse.json`/`playlist.json`, `requests.get(..., auth=(username,password))` без таймаута; `set_volume(volume)`: `str(int(volume * 320))` — третья «шкала» (0..1 → 0..320); хост обязан содержать порт (иначе исключение). Методы `video_track(val)`, `subtitle_track(val)` без способа получить ID.

### 4.4 rest-vlc (Python, MIT, 2022)
`status.xml`, sync/async; `set_volume(volume, percent)` с `volume*2.56` («0-512 = 0-200%»); отправляет несуществующие параметры `pl_random&state=True`, `pl_repeat&state=` и команду `pl_status` — VLC их игнорирует (toggle-семантику автор не учёл). Пример того, как легко неправильно прочитать README.

### 4.5 vlcrc (evdlabs, Python, MIT, 2024-12)
RC-интерфейс по TCP: на каждую команду `socket.create_connection(...)`, `sendall(cmd+"\n")`, `shutdown(1)`, чтение до закрытия, таймаут 1 с — **без долгоживущего соединения** (решение проблемы aiovlc). Умеет `adev` (список/выбор аудиоустройств), плейлист, громкость (заявлено 0..320 — не проверено), CLI.

### 4.6 vlc-mcp-server (piebro, Python, MIT, 2025-06)
Запускает VLC `--extraintf=http --http-host=localhost --http-port=8081 --http-password=...`; команды через `status.xml`, чтение `status.json`; громкость `256 = 100 %`, при старте фильма принудительно `volume&val=256`; **субтитры задаёт опцией при запуске**: `in_play&input=...&option=sub-track=<id>`; список субтитров получает через `mediainfo` на ПК (не из VLC).

## 5. Web и прочие UX-референсы

- **franciscobmacedo/vlc-remote-control** (Python, MIT, 2023-09, 21★): Flask-страница в стиле Apple TV Remote (тач-пад, крупные кнопки), QR-код с URL сервера при старте (`run.py`: `socket.gethostbyname(socket.gethostname())` → `qrcode`), **управляет эмуляцией клавиш** (`keyboard.send(action)`, `src/app.py`), настраиваемые шорткаты; Windows «not tested». Состояния VLC не знает.
- **harbour-vlc_remote** (SailfishOS, QML, 2018): `status.xml` через `XMLHttpRequest` с Basic, `checkConnection.js` различает `401`; дорожки — из имён категорий: `tempAudioTracks.push(xmlModel2.get(i).category.slice(-1))` (берёт **последний символ** имени `Stream N`, ломается при N ≥ 10), затем циклически `passCommands("audio_track&val=" + audioTracks[audioIndex++])`, для субтитров в список добавлен `-1` (выключить). Иллюстрация, что ID из `Stream N` используют и другие авторы.
- **KDE Connect, плагин `mpriscontrol`** (README desktop-плагина): асимметричный протокол — телефон запрашивает `requestPlayerList`, получает список MPRIS-плееров (идентифицируются только по имени), шлёт `{player, action: "Play"|"Next"|...}`, `setVolume` 0..100, `requestNowPlaying` → метаданные при смене трека или по запросу; на Android — уведомление с кнопками и lock-screen controls. Seek/обложка/интерполяция позиции в README не описаны (не проверено). Как UX-референс: пульт сразу показывает «что играет» и умеет выбирать плеер; для нас аналог — выбор экземпляра VLC/ПК.
- **Встроенный `mobile.html`** — см. §1.1; главное, чего в нём нет: выбор дорожек, задержки, скорость, главы.

## 6. Сводная таблица

| Проект | Стек / лицензия | Активность | Транспорт | Обнаружение ПК | Опрос | Дорожки | Громкость (макс.) | Обзор файлов | Особое |
|---|---|---|---|---|---|---|---|---|---|
| VlcFreemote | Java / GPL-3 | 2021 | HTTP XML | TCP-скан /24 :8080,:22 | 2.5 с только при playing + ответ на команду | хоткеи cycle | 400 (ошибка шкалы) | `browse.xml?dir=`, старт `/` (краш на Windows) | закладки, «просмотрено», случайная папка, очередь с коалесценцией |
| android-vlc-remote | Java / GPL-3 | 2014 | HTTP JSON→XML fallback | HTTP-свип /24 с кодами 200/401/403 | 1 с, seq-номера, +500 мс после команды | хоткеи cycle, 5 настраиваемых кнопок | 512 | Windows-пути, диски, Libraries, Home | уведомление, виджет, пауза на звонке, DVD-нав, стриминг на телефон |
| VLC-TV-Remote | Java / MIT (ATV-часть закрыта) | 2021 | телефон→ATV-API→HTTP VLC | «авто» (код закрыт) | — | — | — | NFO-медиатека | trakt.tv, постеры, ambilight |
| RagePeanut (Flutter) | Dart+Electron / нет лицензии | 2022 | HTTP JSON | QR от компаньона | 200 мс (!) + плейлист 500 мс | хоткеи cycle | 320 | `browse.json?uri=`, история | компаньон: запуск/закрытие VLC, смена монитора |
| Hobbyist VLC Remote | закрытый | 2026 | HTTP (+Setup Helper) | Bonjour (helper), скан, IP | — | список дорожек (как — не видно) | — | да, внешние диски | helper чинит фаервол, пароль `vlcremote`, troubleshooting-доки |
| VLC Mobile Remote | закрытый | 2026 | HTTP (+VMR Connect) | автопоиск в Wi-Fi, wizard | — | список, добавление .srt | аппаратные кнопки | да, Home, избранное | аудиоустройства, скриншот, quit VLC, Shutdown/Sleep ПК через helper, Wear OS |
| Unified Remote VLC / VLC Web | Lua-скрипты / сервер закрыт | — | клавиатура / HTTP XML localhost | сервер на ПК | 500 мс | — | — | плейлист | диалог-инструкция при ошибке |
| node-vlc-client | TS / MIT | 2022 | HTTP JSON | нет | нет | `Stream N` → `audio_track&val` | 512 | `browse.json?dir` | playFile с ожиданием |
| aiovlc + HA vlc_telnet | Python / Apache-2 | 2026 | telnet | ручной | HA poll | нет | 500 | нет | reauth-поток, reconnect |
| vlcrc | Python / MIT | 2024 | RC, сокет на команду | ручной | — | — | 320 (заявлено) | — | `adev` |
| vlc-mcp-server | Python / MIT | 2025 | HTTP XML/JSON | localhost | — | `sub-track=` опцией `in_play` | 256 = 100 % | свой корень | субтитры через mediainfo |
| harbour-vlc_remote | QML | 2018 | HTTP XML | ручной | таймер | `Stream N`.slice(-1) → `audio_track` | — | есть | 401 → экран |

## 7. Типичные проблемы пользователей

Собрано из issue-трекеров изученных репозиториев, форума VideoLAN, документации Hobbyist Software, Unified Remote и HA-форума.

1. **Пароль обязателен с VLC 2.1.0.** Самая массовая причина «не подключается» после обновления VLC: HTTP-интерфейс без пароля не поднимается (`http.lua`: «Password unset, insecure web interface disabled», `/` отвечает 403). Форум VideoLAN 2013–2014: «новая версия требует пароль», «VLC 2.1.x несовместим с пультом — ставьте старую версию» (ответ саппорта одного из пультов), ответ разработчика VLC — «задайте пароль в настройках». Unified Remote и Hobbyist зашили в инструкции пароль по умолчанию `vlcremote`.
2. **Web-интерфейс не включён / «не включился после обновления».** Переустановка VLC может сбросить `vlcrc` или `Main interfaces`; изменения вступают в силу только после полного перезапуска VLC (все инструкции это подчёркивают). Hobbyist прямо советует «запустите helper заново».
3. **Windows Firewall / профиль сети.** При первом запуске с включённым Web Windows спрашивает разрешение — пользователи жмут «Отмена» или VLC разрешён только для частных сетей, а сеть помечена как «Общедоступная». Hobbyist: «VLC checked for both public and private networks», helper настраивает только штатный фаервол, сторонние (антивирусы) — вручную. Форум: «выключил фаервол, не помогло» — т.е. причина не всегда в нём.
4. **Телефон в другой сети**: гостевая сеть, 5 GHz с другим SSID и изоляцией, Wi-Fi-репитер, отельный Wi-Fi, соседская открытая сеть, VPN на телефоне (последнее — в документах не найдено, не проверено). Hobbyist посвятил этому отдельную страницу `/wrong-wifi/` и первым пунктом советует перезагрузить роутер.
5. **Сменился IP ПК после перезагрузки** (DHCP) — сохранённый пульт «не видит компьютер»; решение Hobbyist — резервация IP или Bonjour; у android-vlc-remote — реверс-DNS имени и повторный свип.
6. **Привязка интерфейса к localhost** (`--http-host=127.0.0.1`) или нестандартный порт — пульт в сети не видит VLC; «computer not found» при открытом 8080 на другом порту (FAQ samicemalone). До 2.1 — ACL `.hosts` (403 при доступе по IP; файл перезаписывался при обновлении VLC).
7. **VLC не запущен** — все пульты показывают это как «нет связи»; только Hobbyist различает «компьютер найден» и «VLC запущен» (traffic cone), VMR/RagePeanut умеют запускать VLC через компаньон.
8. **ПК уснул** — пульт не поможет без WoL/компаньона; ни один open-source пульт WoL не реализует (VMR Connect умеет Shutdown/Sleep, но не пробуждение — не проверено).
9. **Обзор файлов на Windows**: стартовый путь `/` или `~` против дисков `C:\`, `D:\`; корень Windows = `dir=""`; `..` на уровне диска; обратные слэши; `file:///C:/...` против `C:\...` — крэши VlcFreemote (#6, #21, #22), фиксы в android-vlc-remote (README: «Fix for VLC 2.x not showing the parent (..) entry on Windows»). Имена файлов с кавычкой не играются (samicemalone #5).
10. **Дорожки нельзя выбрать явно** — пользователи просят выбор/отключение субтитров (samicemalone #4, VlcFreemote только cycle); причина — `status.json` не содержит ID и текущую дорожку (см. live-probe).
11. **Неверная подсветка текущего элемента плейлиста** (samicemalone #3): VLC отдавал неверное имя/`current` для некоторых .mkv; до 2.1 `current` определялся по имени.
12. **Разнобой шкалы громкости** (400/320/500/512/256) → «100 % в пульте слишком громко/тихо», mute без восстановления (HA-форки).
13. **Устаревшие ответы и «дрожание» слайдера**: при опросе без порядковых номеров слайдер прыгает назад после seek; без коалесценции слайдер спамит VLC (RagePeanut — 5 запросов/с, VlcFreemote — seek на каждом движении).
14. **Зависший telnet-сокет** (HA): после рестарта VLC/сети соединение не восстанавливается без перезапуска клиента.
15. **Пульт «подключается, но ничего не делает»** (форум, VLC Mobile Remote на Linux 2022): web-интерфейс не отвечает даже в браузере телефона → первый шаг диагностики — открыть `http://<ip>:8080` с телефона.
16. **Пустой ответ сразу после старта VLC** (live-probe) — нужен ретрай.
17. **VLC 4**: web-интерфейс сохранён, но `apiversion` 4, убраны `information.chapters/titles` (массивы), `pl_sd`; Lua API переписан (`vlc.player`), добавлены `set_renderer`/`rd.json`; строковые ID дорожек (не проверено). Публичных заявлений Hobbyist/VMR о VLC 4 не найдено.
18. **Крэш-репорты Android** (VlcFreemote #7, #22): `Can't create handler inside thread without Looper` (сетевые колбэки не на главном потоке), ошибки парсинга XML у Windows-пользователей.
19. **Самоназванный «VLC Remote»** — трейдмарк-конфликты (форк samicemalone переименован в «Remote for VLC (Fork)» «to avoid the trademarked "VLC Remote" name»); VLC for Android «Remote access» и пульты для desktop VLC путают (дисклеймер VMR: «do not support VLC for Android app»).

## 8. Что взять и чего избегать

### Протокол
- **HTTP `status.json` как основной канал** (все работающие пульты на нём); XML не нужен (fallback на `status.xml` имел смысл только для VLC 1.x). Разбирать `fullscreen` как `bool|int`, отсутствие `information` в `stopped`, `equalizer: []`.
- **Ответ на команду = свежий статус** (так устроен сам `status.json?command=`): использовать его для немедленного обновления UI вместо отдельного запроса; плюс «чистый» `status` через ~500 мс после команды (android-vlc-remote, mobile.html) для догона асинхронных изменений (длина/метаданные подтягиваются не сразу).
- **Опрос 1 с в активном состоянии, реже в `stopped`/в фоне** (android-vlc-remote 1 с, mobile.html 1 с, Unified Remote 0.5 с); **не** 200 мс (RagePeanut). Позицию между опросами интерполировать локально по `time` + монотонным часам (так делает HA через `media_position_updated_at`), а не дёргать VLC чаще.
- **Порядковые номера запросов и отбрасывание устаревших ответов; коалесценция «ignorable»-команд (seek/volume/status)** — лучшие идеи android-vlc-remote (`StatusService`) и VlcFreemote (`RemoteVlc.queueCommand`). Один запрос в полёте по каждому типу, слайдер шлёт `seek` на отпускание + throttle при перетаскивании.
- **Оптимистичные обновления только для toggle-состояний** (pause/fullscreen/random) с подтверждением из ответа; для seek — показывать целевую позицию до прихода статуса с флагом `seeking` (Unified Remote).
- **Идемпотентные команды**: `pl_forcepause`/`pl_forceresume` вместо `pl_pause` там, где важна предсказуемость (двойной тап не должен «отжать» паузу); `pl_pause&id=` для старта из `stopped`.
- **Громкость**: внутренне 0..512, 256 = 0 dB; в UI — проценты с меткой 100 % на 256 и явной зоной усиления >100 % (как у VLC). Не повторять 400/320/500. Mute — запоминать уровень (android-vlc-remote), а не `0/256` (HA-форк). Аппаратные кнопки → `volume&val=%2B20`/`-20` (android-vlc-remote) или шаг в процентах.
- **Seek**: абсолютный в секундах (`seek&val=<sec>`) при отпускании слайдера; относительные прыжки `%2B10`/`-10` и настраиваемый шаг (пресеты 5/10/30/60 с); главы через `chapter&val=`. Процентный seek (`+0.5%`) — удобная «перемотка по длине фильма» (VlcFreemote), но точность 1 % (замечание Unified Remote).
- **Дорожки**: строить список из `information.category["Stream N"]` (`Type`, `Language`, `Description`, `Codec`) и слать `audio_track|subtitle_track&val=N` (node-vlc-client, harbour; подтверждено live-probe для 3.0.23), `val=-1` для «выключить субтитры». Хоткеи `key&val=audio-track` оставить как fallback/«цикл» (единственный способ у всех Android-пультов). Текущую дорожку HTTP не отдаёт — хранить выбор на клиенте и/или получать через RC/собственную Lua-страницу (live-probe). Парсить номер полным `parseInt`, не `slice(-1)`.
- **Субтитры из файла**: `addsubtitle&val=<uri>` (VMR: «requires VLC 2.2.0+»), `in_play&option=sub-track=<id>` при запуске (vlc-mcp-server).
- **RC/telnet — только через компаньон или строго «сокет на команду»** (vlcrc): долгоживущий сокет ломается (HA), один клиент за раз. Нужен для `atrack *`, `adev` (переключение HDMI/колонки — заявленная фича VMR), push-событий, `quit`.
- **Не полагаться на `pl_delete`** без проверки (README: «completly UNSUPPORTED»), на `pl_sd` (удалён в 4.0), на `information.chapters`/`titles` (удалены в 4.0) — читать `chapters_count` если есть, иначе массив.
- **Диагностика по кодам**: `403` на `/` → «в VLC не задан пароль», `401` → «неверный пароль», connection refused → «web-интерфейс выключен или другой порт», timeout → «фаервол/другая сеть/ПК спит», пустой ответ → «VLC только что запустился, повтор». Показывать пользователю *разные* сообщения (android-vlc-remote различает 200/401/403 уже на этапе сканирования; VlcFreemote всё сводит к «select a new server» — плохо).
- **VLC 4 готовность**: ветвить по `apiversion` (3 vs 4), не падать на отсутствующих полях; выбор дорожек на 4.x перепроверить (строковые ID).

### Обнаружение и подключение
- Комбинировать: (1) сохранённый IP + имя хоста (реверс-DNS, как android-vlc-remote; `.local`/NetBIOS-имя — не проверено), (2) параллельный HTTP-свип `/24` «от своего IP наружу» с таймаутом ~1 с и сохранением кода ответа (android-vlc-remote), а не последовательный TCP-скан по 100 мс (VlcFreemote, ~25 с на /24), (3) ручной ввод, (4) QR-код/пейринг-код от компаньона (RagePeanut, VLC Android Remote access). mDNS/Bonjour без компаньона бесполезен — VLC ничего не анонсирует (Hobbyist анонсирует через helper).
- «Test connection» перед сохранением с человекочитаемым результатом (android-vlc-remote), индикатор «ПК найден / VLC запущен» (Hobbyist traffic cone).
- Хранить пароль не в plaintext SQLite (VlcFreemote `COLUMN_PASS` с TODO) — EncryptedSharedPreferences/Keystore.
- Setup-мастер в приложении (VMR «Setup Wizard», Hobbyist helper): пошагово «включить Web → задать пароль → перезапустить VLC → фаервол (частная+общедоступная) → проверить с телефона в браузере», с кнопкой «проверить сейчас». Опционально компаньон/скрипт на ПК, который правит `vlcrc`, фаервол и кладёт Lua-страницу (live-probe показал, что `%APPDATA%\vlc\lua\http\requests\` подхватывается без прав администратора).

### UX, за который хвалят
- Автоподключение к последнему ПК и мгновенный «что играет» (все закрытые конкуренты; KDE Connect).
- Обзор файлов с «Home»/избранными папками (VMR, android-vlc-remote), закладки и отметка «просмотрено» (VlcFreemote), сортировка «папки первыми», запоминание последнего пути per-server, при запуске файла — не плодить дубликаты в плейлисте (RagePeanut: enqueue только если `uri` ещё нет).
- Аппаратные кнопки громкости, уведомление/экран блокировки с кнопками, виджет, пауза при звонке (android-vlc-remote, VMR).
- Пресеты задержки субтитров/звука одной кнопкой (android-vlc-remote `DelayPresetButton`), настраиваемая панель 5 кнопок, aspect/crop, главы.
- Компактный «мини-пульт» для телефона (VlcFreemote), крупные кнопки и тач-пад в стиле Apple TV Remote (vlc-remote-control), жесты (VMR).
- Для ТВ-сценария: перенос окна VLC на другой монитор и запуск/закрытие VLC (RagePeanut companion), Shutdown/Sleep ПК и смена аудиоустройства (VMR Connect) — всё это требует компаньона на ПК.

### Чего избегать
- Опрос чаще 1 с и запросы без отмены устаревших (RagePeanut).
- Старт обзора с `/` и фатальный крэш при ошибке каталога (VlcFreemote); текстовый поиск `<title>Error loading` вместо кодов — допустимо как эвристика, но корень Windows = `dir=""`/`uri=file:///`.
- Единое сообщение «не могу подключиться» на все классы ошибок; зацикленные тосты при отсутствии сети (samicemalone #2).
- Сохранение пароля открытым текстом; хранение IP без имени хоста.
- Выбор дорожек только циклическим хоткеем без списка (все Android-пульты).
- Привязка к полям, которых нет в VLC 4 (`chapters[]`, `titles[]`), и к `pl_sd`.
- Эмуляция клавиатуры как основной транспорт (Unified Remote «VLC», vlc-remote-control): требует фокуса окна и не даёт состояния.
- Долгоживущий telnet-сокет из приложения без переподключения (HA).
- Собственная «шкала» громкости, не совпадающая с VLC.

## Приложение: ссылки

- VideoLAN wiki: `Documentation:Modules/http_intf` (пароль с 2.1.0, `.hosts` до 2.1, 401/403), `Control_VLC_from_an_iPhone`, `Control_VLC_from_an_Android_Phone` (список пультов 2019 г.).
- vlc-devel 2013-04, «http intf: ensure the password is set» (Rob Jonson).
- Hobbyist Software: `hobbyistsoftware.com/vlc`, `/vlc-more`, `/VLCSetup-win-manual`, `/vlc-versions`, `blog.hobbyistsoftware.com/vlc-remote-troubleshooting/`, `/wrong-wifi/`; App Store id297244048; Google Play `com.hobbyistsoftware.android.vlcremote_us`.
- VLC Mobile Remote: Google Play `adarshurs.android.vlcmobileremote`, `vlcmobileremote.com`; forum.videolan.org p=530687.
- Unified Remote: `help.unifiedremote.com/article/57-configuring-vlc-advanced`, github.com/unifiedremote/Remotes (`Main/VLC`, `Main/VLC Web`).
- Home Assistant: `community.home-assistant.io/t/remote-vlc/10951`, `homeassistant/components/vlc_telnet`, github.com/MartinHjelmare/aiovlc, github.com/gambalaya/vlc_remote, git.jeena.net/jeena/vlc_remote.
- VLC for Android 3.6.0 Remote access: dev.to/npomepuy/vlc-for-android-360-beta-11n4, github.com/videolan/vlc-android `application/remote-access-server`.
- KDE Connect: `lxr.kde.org/source/network/kdeconnect-kde/plugins/mpriscontrol/README`.
- samicemalone FAQ: `samicemalone.co.uk/remote-for-vlc/faq.html` (VLC 2.1+ пароль, TCP 8080 в фаерволе, Home/Libraries).
