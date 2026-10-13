# VLC Remote Companion (Windows)

Компаньон для Android‑пульта `vlc-remote`: трей‑приложение на .NET 9, которое владеет жизненным
циклом VLC 3.0.x на ПК у телевизора и отдаёт телефону уровень 2 протокола
([protocol/README.md](../protocol/README.md)): текущие дорожки, названия глав, аудиоустройства,
push‑события, запуск VLC, перенос окна на ТВ, питание, системная громкость.

Этапы **M2** (LAN) и **M3** (Bluetooth RFCOMM). Установщик Velopack и TMDB‑библиотека — позже (см. ниже).

## Состав

```
companion/
├── VlcRemote.sln
├── VlcRemote.Core/            библиотека net9.0 без Windows‑API (тестируется на любой ОС)
│   ├── Protocol/              сообщения протокола (System.Text.Json, source‑generated, полиморфизм по "t")
│   ├── Vlc/                   VlcHttpClient, VlcJsonParser, VlcRcClient, RcParser
│   ├── State/                 StateBuilder (status.json + tracks.json + RC → state), StateCoalescer (≤ 4/с)
│   ├── Pairing/               PairingService (код на 5 мин, токен 32 байта base64url, хранится SHA‑256)
│   ├── Session/               Session (NDJSON‑сессия поверх ILineTransport), StreamLineTransport (NDJSON поверх потока байт), ISessionHost, CommandArgs
│   ├── Bluetooth/             BluetoothFormat (адрес AA:BB:…, кодирование атрибута SDP)
│   ├── Displays/              MonitorTopology (имена/id/тип мониторов), DisplayController (sys‑команды экранов,
│   │                          предпочтительный монитор, разовый перенос нового окна VLC), AutoPlacement
│   ├── Lua/                   LuaPagesInstaller (копия штатных страниц VLC + наши в %APPDATA%)
│   └── Setup/                 FirewallPlanner (which firewall rules allow/block, what to remove/add), SetupPolicy
│                              (when the setup window and the startup notice appear)
├── VlcRemote.Companion/       WinExe net9.0-windows10.0.19041.0: трей, Kestrel, VlcManager, Win32
│   ├── Net/                   WebServer (/ws, /info, /art), WebSocketLineTransport, UdpBeaconResponder, DnsSdAdvertiser,
│   │                          BluetoothRfcommServer + RfcommLineTransport (служба RFCOMM, M3)
│   ├── Vlc/                   VlcLocator, VlcManager (процесс, опрос, RC, команды), EmbeddedLuaPages
│   ├── Services/              DisplayService (мониторы через CCD API, окно VLC), VlcWindowHost, PowerService,
│   │                          AudioService, AutostartService
│   ├── Setup/                 SetupForm (setup and permissions window), SetupBackend, ComFirewallRuleSource (COM
│   │                          HNetCfg.FwPolicy2), FirewallElevation (elevated --apply-firewall helper), NetworkCategories
│   └── Tray/                  TrayContext (меню), PairingForm (код на экране ТВ), TrayIconFactory
├── VlcRemote.Core.Tests/      xunit: парсеры на реальных ответах VLC 3.0.23 (docs/research/samples), сессия, сопряжение
└── lua/                       Lua‑страницы уровня 1 и install.ps1 (работают и без компаньона)
```

Принцип разделения: `Core` не знает про WinForms и ASP.NET — сессия работает поверх абстракции
«строка туда / строка сюда» (`ILineTransport`): WebSocket и RFCOMM — два транспорта одной и той же сессии
(кадрирование потока RFCOMM — `StreamLineTransport` в `Core`, тестируется без WinRT).

## Сборка и тесты

Нужен .NET SDK 9.0 (проверено на 9.0.310) и Windows для проекта компаньона.

```powershell
dotnet build companion\VlcRemote.sln -c Release
dotnet test  companion\VlcRemote.sln
```

Запуск из исходников:

```powershell
dotnet run --project companion\VlcRemote.Companion -c Release
```

Для отладки с отдельной папкой данных: `VlcRemote.Companion.exe --data-dir D:\tmp\vlcremote`
(или переменная окружения `VLCREMOTE_DATA_DIR`).

## Установка через скрипт

Обычно компаньон ставится не вручную, а скриптом `scripts\deploy-windows.ps1` (из GitHub Releases или `-Local` из
исходников) в `%LOCALAPPDATA%\VlcRemote\app`; обновление закрывает запущенный экземпляр командой
`VlcRemote.Companion.exe --shutdown` и сохраняет предыдущую версию для `-Rollback`. Подробности — CLAUDE.md.
Версия берётся из файла `VERSION` в корне (`-p:VersionSuffix=beta.N` для тестовых сборок) и видна в подсказке
иконки в трее и в `/info`.

## Публикация

```powershell
dotnet publish companion\VlcRemote.Companion -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

Результат — `publish\VlcRemote.Companion.exe` (framework‑dependent, ~26 МБ, из них 24 МБ —
проекция Windows SDK `Microsoft.Windows.SDK.NET.dll`, которую тянет TFM `net9.0-windows10.0.19041.0`
под WinRT‑Bluetooth этапа M3). На целевом ПК нужны **.NET Desktop Runtime 9** и
**ASP.NET Core Runtime 9** (Kestrel). Вариант без зависимостей:
`--self-contained true -p:EnableCompressionInSingleFile=true` (~60 МБ).

В `companion/global.json` SDK закреплён на 9.0.3xx: у SDK 10 `--self-contained false`
разбирается иначе и получается self‑contained сборка на 150 МБ (в csproj на всякий случай стоит
`<SelfContained>false</SelfContained>`).

## Что делает при запуске

0. **Setup and permissions window** on the first start (and once more after an update that adds an item) — see
   "Setup and permissions" below. If the firewall has no rules for the companion/VLC yet, the window is shown
   *before* the network listeners start, so Windows never shows its own "allow access" prompt. Otherwise it opens
   next to the running companion. On every later start the companion checks the firewall and offers a fix in a tray
   notification (see below).
1. Поднимает Kestrel на `0.0.0.0:47555`: `ws://…/ws` (NDJSON‑сессия), `GET /info` (без
   авторизации), `GET /art?plId=N` (заголовок `Authorization: Bearer <token>`, проксирует `/art` VLC).
2. Отвечает на UDP‑маяк `VLCREMOTE_DISCOVER 1` на порту 47556 unicast‑JSON `{"t":"beacon",…}`.
3. Объявляет `_vlcremote._tcp` через встроенный DNS‑SD‑responder Windows (`DnsServiceRegister`);
   при ошибке пишет в журнал и продолжает работать (остаются маяк и ручной ввод адреса).
4. Находит VLC (`HKLM\SOFTWARE\VideoLAN\VLC`, затем Program Files). Если VLC не запущен — прописывает
   в `vlcrc` веб‑интерфейс и RC (см. «Автонастройка VLC»). Если VLC запущен и отвечает по HTTP с нашим
   паролем — берёт его под контроль; если запущен без управления — `vlc.status = noControl`, уведомление в
   трее и пункт меню «Перезапустить VLC с веб‑интерфейсом» (см. «VLC открыт без управления»).
5. Опрашивает `status.json` каждые 500 мс при воспроизведении и каждые 2 с иначе, держит один
   RC‑сокет `127.0.0.1:4212` (события `status change:`, `adev`, текущая дорожка при отсутствии
   `tracks.json`, `quit`), собирает полный объект `state` и рассылает его всем телефонам при
   изменениях, не чаще 4 раз в секунду.
6. Устанавливает Lua‑страницы уровня 1 в `%APPDATA%\vlc\lua\http` (копия штатной папки VLC +
   `requests/tracks.json`, `requests/setvar.json`, файл `vlcremote.version`). Переустанавливает при
   смене версии VLC или самих страниц. Делает это только когда VLC не запущен; иначе откладывает
   до его закрытия (пункт меню показывает «после закрытия VLC»).
7. Публикует Bluetooth‑службу RFCOMM «VLC Remote» для телефона без Wi‑Fi (см. «Bluetooth»). Нет адаптера или
   Bluetooth выключен — пишет в журнал и работает без него.

Запуск VLC компаньоном:

```
vlc.exe --extraintf http:rc --http-host 0.0.0.0 --http-port 8080 --http-password <из settings.json>
        --rc-host 127.0.0.1:4212 --rc-quiet [--qt-fullscreen-screennumber=N] [--fullscreen] [uri]
```

## Автонастройка VLC (vlcrc)

Чтобы VLC, открытый как угодно (двойной клик по фильму в Проводнике, меню «Пуск»), сразу управлялся с
телефона, компаньон прописывает в `%APPDATA%\vlc\vlcrc` (в «родные» секции файла):

| Секция | Ключ | Значение |
|---|---|---|
| `[core]` | `extraintf` | добавляются `http` и `rc` к уже указанным интерфейсам (например, `qt` → `qt:http:rc`) |
| `[core]` | `http-port` | `vlcHttpPort` из `settings.json` (8080) |
| `[lua]` | `http-password` | `vlcHttpPassword` из `settings.json` |
| `[oldrc]` | `rc-host` | `127.0.0.1:<vlcRcPort>` (4212) |
| `[oldrc]` | `rc-quiet` | `1` — иначе на Windows RC открывает окно консоли |
| `[qt]` | `qt-continue` | `0` — VLC's own "continue playback?" is off; the phone asks instead (see "Watch history") |

`http-host` не трогается (по умолчанию VLC слушает все интерфейсы — телефон может ходить к VLC и напрямую).

- Файл правится **только когда ни один `vlc.exe` не запущен** (VLC перезаписывает vlcrc из памяти): при старте
  компаньона и каждый раз, когда VLC закрывается.
- Закомментированные умолчания (`#extraintf=`) раскомментируются на месте; отсутствующий ключ дописывается в
  конец секции, отсутствующая секция — в конец файла. Кодировка (UTF‑8 с BOM) и окончания строк сохраняются,
  запись атомарная (временный файл + замена). Если файла нет (VLC ни разу не запускался) — создаётся
  минимальный файл с этими секциями.
- Перед **первым** изменением делается копия `%APPDATA%\vlc\vlcrc.vlcremote-backup` (дальше не перезаписывается).
  Вернуть всё как было: закрыть VLC, удалить `vlcrc` и переименовать копию обратно.
- Если всё уже настроено, файл не трогается; в журнал пишется только факт изменения.
- Отключить: `"configureVlc": false` в `settings.json` (перезапустить компаньон). `"vlcrcPath"` — другой путь
  к файлу (для тестов).

## VLC открыт без управления

Если VLC был открыт до того, как компаньон настроил vlcrc (или настройку отключили), процесс есть, а
веб‑интерфейс с паролем компаньона не отвечает — это `vlc.status = noControl` (`running = false`). Компаньон:

- один раз на процесс VLC показывает уведомление «VLC открыт без управления с телефона. Перезапустите его —
  дальше VLC будет запускаться с управлением сам.»; щелчок по уведомлению перезапускает VLC;
- по `cmd launch` с телефона (кнопка «Перезапустить с управлением») и по пункту меню «Перезапустить VLC с
  веб‑интерфейсом» перезапускает VLC: берёт файл/URI из командной строки процесса (последний аргумент,
  похожий на путь или URI; `--started-from-file` и значения опций пропускаются), закрывает VLC штатно
  (закрытие главного окна, до 5 с, затем принудительно), прописывает vlcrc и запускает VLC с тем же файлом.
  Позиция воспроизведения теряется.

Первые 20 с после запуска процесса VLC считается «запускающимся» (`starting`), чтобы не путать поднимающийся
веб‑интерфейс с отсутствием управления; три подряд неудачных запроса к управляемому VLC — тоже не повод сразу
объявлять `noControl`.

## Сопряжение

Телефон без токена (или с забытым токеном) присылает `hello` без кода — компаньон отвечает
`err pairing_required` и **сам открывает окно с кодом** поверх всех окон: на экране, где сейчас окно VLC, иначе на
предпочитаемом (ТВ), иначе на основном. В заголовке — «Телефон «<имя>» хочет подключиться», ниже код, адрес и
обратный отсчёт. Телефон показывает поле «Введите код с экрана ПК» и переподключается с `pairCode`.

- Пока код активен, повторные запросы (в том числе от другого телефона) новый код не создают — окно только
  поднимается поверх.
- Неверный код → `err unauthorized`, окно остаётся. Если код истёк или попытки (5) исчерпаны, следующая попытка
  телефона открывает окно с новым кодом.
- Не чаще одного окна в 10 с на телефон и не чаще одного нового кода в 10 с вообще — телефон, переподключающийся
  в цикле, не заваливает экран окнами, а перебор кода не ускоряется.
- При успехе окно показывает «✓» и закрывается через 3 с, в трее — «Телефон «<имя>» сопряжён».
- Закрыть окно (щелчок, Esc) = отменить код. Вручную окно открывается пунктом меню «Сопряжение телефона…» или
  двойным щелчком по иконке.

## Bluetooth

Запасной канал, когда у телефона нет общей сети с ПК (протокол — `protocol/README.md`, «Транспорт» и
«Обнаружение → Bluetooth»).

**Как работает.** Компаньон публикует в SDP службу RFCOMM с UUID `47fe5bd6-98ad-4d9f-9812-174b3394037d` и именем
`VLC Remote` (WinRT `RfcommServiceProvider` + `StreamSocketListener`, без прав администратора). ПК при этом **не**
становится видимым для поиска: подключается только телефон, уже сопряжённый с ним. По каналу идёт тот же NDJSON,
что по WebSocket (UTF‑8, строка на сообщение, разделитель `\n`, не больше 1 МиБ на строку), то же `hello` с
токеном и то же окно с кодом для нового телефона; в журнале сессия выглядит как `bt <имя телефона> (<адрес>)`. Одновременно — до
4 Bluetooth‑подключений.

Адрес адаптера (`AA:BB:CC:DD:EE:FF`) компаньон сообщает в поле `bt` ответа `/info`, UDP‑маяка и `welcome`, пока
радио включено; пока служба опубликована, в `caps` есть `bluetooth`. Телефон запоминает адрес при подключении по
Wi‑Fi и сам переходит на Bluetooth, если ПК в сети не отвечает.

**Что нужно.**

- Адаптер Bluetooth Classic (BR/EDR) и включённый Bluetooth в Windows.
- Телефон **сопряжён с ПК** средствами Windows («Параметры → Bluetooth и устройства → Добавить устройство») или
  Android. Компаньон сопряжение Bluetooth не инициирует. Токен приложения всё равно нужен: первое подключение —
  обычное окно с кодом на экране ПК.
- Настройка `bluetooth` в `settings.json` (по умолчанию `true`; `false` — служба не публикуется, поля `bt` нет).
- Уровень защиты канала — `BluetoothEncryptionAllowNullAuthentication`: шифрование обязательно, подходит любое
  сопряжение, включая «Just Works» без сравнения кода (так в образце Microsoft и в проверке на этом ПК). Телефон
  аутентифицирует токен протокола. `bluetoothRequireAuthentication: true` требует ключ с защитой от MITM
  (`BluetoothEncryptionWithAuthentication`) — тогда телефон, сопряжённый без сравнения кода, не подключится.

**Устойчивость.** Нет адаптера или радио выключено — компаньон работает без Bluetooth и перепроверяет раз в
минуту. Службу и слушатель он пересоздаёт после выхода из сна (`PowerModeChanged → Resume`, через 5 с) и при
включении радио (`Radio.StateChanged`); ошибки WinRT повторяет с паузами 2 с … 5 мин. Состояние видно в меню в трее:
«Bluetooth: включён / запускается… / выключен на этом ПК / недоступен / выключен в настройках».

**Если телефон не подключается по Bluetooth.**

- В трее «Bluetooth: недоступен» — в журнале строка `Bluetooth RFCOMM service failed to start … (0x…)`; проверьте,
  что служба Windows «Служба поддержки Bluetooth» (`bthserv`) запущена, и перезапустите компаньон.
- «Bluetooth: выключен на этом ПК» — включите Bluetooth в Windows (режим «в самолёте» тоже выключает радио).
- В трее «включён», а телефон не находит службу — Android кэширует список служб ПК: удалите ПК из сопряжённых на
  телефоне и сопрягите заново (или выключите и включите Bluetooth на телефоне).
- Связь рвётся после простоя — снимите в диспетчере устройств у адаптера Bluetooth галочку «Разрешить отключение
  этого устройства для экономии энергии» (если она есть); телефон переподключается сам.
- Телефон сопряжён «Just Works» и включено `bluetoothRequireAuthentication` — выключите настройку или пересопрягите
  телефон со сравнением кода.

## Экран для VLC (ТВ)

Компаньон помнит **предпочтительный монитор** и открывает VLC на нём — и запущенный с телефона, и открытый вручную
(двойной клик по фильму в Проводнике).

- **Мониторы.** Список строится из `Screen.AllScreens` (границы, основной) и CCD API Windows (`QueryDisplayConfig` +
  `DisplayConfigGetDeviceInfo`): понятное имя из EDID («SAMSUNG», «L27q-35»), тип подключения (`internal` —
  встроенная панель ноутбука: eDP/LVDS/«internal»; `external` — HDMI/DP/…) и **устойчивый id** — путь устройства
  монитора (`\\?\DISPLAY#SAM0F99#…`). Id не меняется, когда ТВ выключают и включают (в отличие от номера
  `\\.\DISPLAYn` и индекса в списке). Нет имени в EDID — «Экран N» (на языке телефона / Windows), у встроенной
  панели — «Встроенный экран»; нет пути устройства — id = имя GDI. В режиме дублирования — одна строка на
  источник (имена через « + », id — ТВ). При запуске и при каждом изменении конфигурации экранов в журнал
  пишется по строке на монитор: имя, тип, основной, `gdi`, границы, id — так список проверяется удалённо
  (`dev-laptop.ps1 -Logs`). По SSH `Screen.AllScreens` видит только фиктивный экран — проверять через компаньон.
- **Выбор.** На телефоне (`sys moveToDisplay`) или в трее («Перенести VLC на экран ▸»): окно VLC переносится
  (снять полный экран → переместить → вернуть полный экран, если был) **и монитор запоминается**
  (`preferredDisplayId` в `settings.json`). VLC не запущен — только запоминается. «Не управлять экраном» в трее
  (`sys preferredDisplay {"displayId": null}`) — компаньон больше не трогает окно VLC.
- **Новое окно VLC** переносится на предпочтительный монитор **один раз**, если он подключён и окно не там
  (главное окно ждём до 10 с). Потом окно не трогается — если перетащить его обратно, компаньон не спорит.
  Не трогаются и окна VLC, запущенного до старта компаньона. ТВ выключен — окно остаётся, где открылось.
- **Запуск компаньоном** (с телефона или из трея; предпочтительный монитор подключён и мониторов 2+):
  VLC стартует без файла и с `--qt-fullscreen-screennumber=-1` (полный экран — на мониторе главного окна; номера
  экранов у Qt не совпадают с нашими), окно переносится, и только потом файл открывается по HTTP — с
  `launchFullscreen` фильм сразу разворачивается на нужном экране.
- **Перенос окна.** Обычное окно сохраняет размер (не больше рабочей области) и встаёт по центру, развёрнутое —
  разворачивается там же. Полноэкранный VLC под управлением — через HTTP `fullscreen`; без управления (открыт
  до настройки vlcrc) окно без рамки на весь монитор просто растягивается на новый монитор.
- **Старый формат.** `preferredDisplay` (индекс) из прежних версий при первом запуске заменяется на id монитора с
  этим индексом; `moveToDisplay {"display": int}` и `display` в `launch`/`open` по‑прежнему понимаются
  (`display` в `launch` — разовый выбор, не запоминается).

## Watch history

The companion is the source of truth for what was watched (protocol: «Watch history and resume», capability
`history`). Code: `VlcRemote.Core/History/` — `HistoryStore` (the file), `HistoryTracker` (decisions from VLC polls),
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

## Меню в трее

- строка статуса VLC, число подключённых телефонов и состояние Bluetooth;
- **Сопряжение телефона…** — окно без рамки поверх всех окон на экране VLC / предпочитаемом экране (ТВ) с
  6‑значным кодом и адресом `host:port`; код действует 5 минут, не более 5 попыток (открывается и само,
  когда несопряжённый телефон пытается подключиться — см. «Сопряжение»);
- **Запустить VLC**, **Перезапустить VLC с веб‑интерфейсом**, **Закрыть VLC**;
- **Перенести VLC на экран ▸** — мониторы с понятными именами, галочка — предпочтительный (на нём открывается
  VLC); щелчок = перенести окно и запомнить, как с телефона; **Не управлять экраном** — забыть
  (см. «Экран для VLC»);
- **Setup and permissions…** — the setup window (state re-read on every open);
- **Установить Lua‑страницы**, **Автозапуск** (`HKCU\…\Run`), **Не давать ПК уснуть во время фильма**;
- **Открыть журнал**, **Открыть папку настроек**, **Выход**.

## Где лежат настройки и журналы

`%LocalAppData%\VlcRemote\`:

| Файл | Что |
|---|---|
| `settings.json` | порты (`wsPort` 47555, `beaconPort` 47556), `listenAddress`, путь к VLC (`vlcPath`), `vlcHttpHost/Port/Password` (пароль генерируется при первом запуске), `vlcRcPort`, `launchFullscreen`, `vlcExtraArgs`, `mediaRoots`, `preferredDisplayId` / `preferredDisplayName` (предпочтительный монитор; старый `preferredDisplay` переносится сам), `keepAwake`, `autoInstallLuaPages`, `autoLaunchVlc`, `configureVlc` (автонастройка vlcrc, по умолчанию `true`), `vlcrcPath`, `advertiseDnsSd`, `bluetooth` (служба RFCOMM, по умолчанию `true`), `bluetoothRequireAuthentication` (по умолчанию `false`, см. «Bluetooth»), `installationId`; `setupCompleted` / `setupVersion` (the setup window was completed, for which item-set version), `manageFirewall` (network access was chosen in the setup window: check the firewall at startup, default `true`) |
| `paired.json` | сопряжённые устройства: SHA‑256 токена, имя, id, дата |
| `history.json` | watch history and per-folder track choices (see "Watch history"); a corrupt file is renamed to `history.json.corrupt-<time>` |
| `logs\companion-YYYYMMDD.log` | Serilog, ротация по дням, 14 файлов |

Пароль HTTP‑интерфейса VLC в журнал не пишется.

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
| Bluetooth (info only) | nothing: Windows needs no permission; a link opens `ms-settings:bluetooth` to pair the phone | — |

Each item shows its current state (✓ / not set up / ⚠ / ✗ with the reason) and, after Apply, the result. If a
connected network is **public**, the firewall item warns that Windows will not let the phone in there and links to
the network settings (the category is read via `INetworkListManager`; the companion does not change it).

When the window appears by itself: `setupCompleted` is `false`, or `setupVersion` is lower than
`SetupPolicy.CurrentVersion` (Core). The tray item **Setup and permissions…** opens it at any time.

> **Rule:** every new permission or system change (firewall, registry, files of other programs, power, devices…)
> must be added as an item of this window, with a short "what and why" text in `tools/gen_localization.py`, and
> `SetupPolicy.CurrentVersion` must be bumped so users who completed the window see it once more.

### Firewall

Windows shows its "allow access" prompt the first time a program listens on the network. **Cancel** there silently
creates **block** rules, and block wins over allow — the phone then cannot find the PC. The companion therefore
manages the rules itself:

- **Detection** (no admin rights): COM `HNetCfg.FwPolicy2` (`ComFirewallRuleSource`), only rules bound to
  `VlcRemote.Companion.exe` / `vlc.exe`. A program is *allowed* when an enabled inbound allow rule for its path covers
  the private profile and TCP (plus UDP for the companion's discovery beacon); it is *blocked* when an enabled inbound
  block rule for it covers the private profile. Block rules for the public profile only (left by "Allow" with only
  "Private networks" ticked) are fine and kept. Decisions are pure functions in Core (`FirewallPlanner`,
  `SetupPolicy`) with unit tests.
- **Applying** needs admin rights, the companion runs without them. It starts **one elevated copy of itself**
  (`Verb=runas`, one UAC prompt):
  `VlcRemote.Companion.exe --apply-firewall --companion-exe "<path>" [--vlc-exe "<path>"] --data-dir "<dir>"`.
  The helper removes the blocking rules, replaces our own rules for these paths with fresh ones — inbound allow,
  **private profile**, any protocol, names `VLC Remote Companion` and `VLC (VLC Remote)` (the same as
  `deploy-windows.ps1 -Firewall`), group `VLC Remote` — reads the rules again and exits: `0` OK, `2` bad arguments,
  `3` error, `4` rules still not right (e.g. a Group Policy rule). It logs every change to the companion log and never
  starts the tray, the listeners or VLC. Declined UAC → the window says so; Apply can be pressed again.
- **Every start** (when `manageFirewall` is on): if a block rule exists, or the allow rules are gone after the window
  was completed, a tray notification offers the fix; a click opens the window.

Manual equivalent (administrator PowerShell):

```powershell
netsh advfirewall firewall add rule name="VLC Remote Companion" dir=in action=allow profile=private program="<path to VlcRemote.Companion.exe>" enable=yes
netsh advfirewall firewall add rule name="VLC (VLC Remote)" dir=in action=allow profile=private program="C:\Program Files\VideoLAN\VLC\vlc.exe" enable=yes
```

## Что проверено на этом ПК (один монитор, VLC 3.0.23)

Живая проверка с безголовым VLC (`-I dummy --vout dummy --aout dummy`, HTTP `127.0.0.1:8099`,
RC `4212`) и компаньоном на `127.0.0.1:47557` с отдельной папкой данных:

- `/info`, UDP‑маяк, `/art` (401 без токена, 200 `image/jpeg` с Bearer), DNS‑SD регистрация;
- `hello` с неверным токеном → `err unauthorized` и закрытие; с токеном → `welcome` + первый `state`;
- `pause/play/seek/track/volume/mute/chapter/rate/random/playlistRemove/open/launch/quit`,
  `getState/getPlaylist/getTracks/getAudioDevices/browse`, `sys listDisplays/keepAwake/moveToDisplay`;
- мониторы: CCD API видит «L27q-35» (`external`, id `\\?\DISPLAY#LEN66D5#…`); `listDisplays` с `current`/
  `preferred`/`preferredId`, `moveToDisplay` по id и по старому индексу без VLC (только запоминание, без ошибки),
  неподключённый id, `preferredDisplay null`, `state.system.preferredDisplayId`, перенос старого `preferredDisplay`
  в settings.json; безголовый VLC, открытый «вручную», — автоперенос ждёт окно 10 с и ничего не двигает;
- текущие дорожки через RC `atrack/strack` (без Lua‑страниц в VLC) и выбор дорожек после `open`;
- события `vlcStarted`, `inputChanged`, `vlcExited`; push `state` ~2–3 раза в секунду при воспроизведении;
- `LuaPagesInstaller` — только против временной папки (юнит‑тесты), в `%APPDATA%` не ставился.
- Watch history (2026-10-13, headless VLC 3.0.23 on `127.0.0.1:18099`, companion on `127.0.0.1:47655` with a temp data
  dir, the 2160p LOTR remux): played from `--start-time 400` for ~11 s with `cmd track audio 2` → `history.json` got
  position 411 and the folder's audio choice; reopened → `resumePrompt` ~60 ms after the input was seen, VLC paused at
  0 and stayed paused, `state.resume.pending`, the folder's audio track applied while paused (current audio 2 after the
  answer); `cmd resume resume` → playing from ~411; a second answer → `ok`, ignored; third open → `pl_forceresume`
  sent to VLC directly ("Play in VLC") → `state.resume` cleared, the resume point (416) kept after a few seconds;
  `getHistory continue/all`, `browse` with `progress`/`finished`, `historyUpdate finished`, `historyUpdate` of a
  non-file URI → `bad_request` in Russian. Next episode and VLC-side track changes — unit tests only.
- Bluetooth (адаптер MediaTek, без сопряжения и подключения устройств, состояние радио не менялось): служба RFCOMM
  публикуется из обычного процесса без прав администратора (`… advertising on adapter 04:68:74:9A:2B:FC`), поле `bt`
  есть в `/info`, маяке и `welcome`, `caps` содержит `bluetooth`; с `bluetooth: false` поля нет; `--shutdown`
  снимает службу. Кадрирование RFCOMM (`StreamLineTransport`) — юнит‑тесты.

### Проверить на ноутбуке у ТВ

- Setup window: first start with a fresh data dir (`--data-dir`) — the window appears before the listeners when rules
  are missing; Apply → exactly one UAC prompt, rules `VLC Remote Companion` / `VLC (VLC Remote)` (private profile,
  group "VLC Remote") appear, no Windows "allow access" prompt afterwards, the phone connects. Declining UAC → "not
  given" status, Apply again works. With the existing allow rules (current state of the laptop) the window opens
  next to the running companion with ✓ for network access. A block rule created by hand
  (`netsh advfirewall firewall add rule name=test dir=in action=block profile=private program=<companion exe>`) →
  tray notification at the next start, Apply removes it. Text must not be cut at the laptop's and the TV's DPI
  (window moved between them), in English and Russian Windows.

- Мониторы (встроенная панель + ТВ по HDMI): имена и `kind` в `listDisplays`/журнале, id ТВ после выключения и
  включения ТВ; перенос окна VLC (обычного, развёрнутого, полноэкранного под управлением и без него) и обратно;
  автоперенос нового окна — запуск с телефона с `launchFullscreen` (фильм сразу на ТВ) и двойной клик по фильму;
  ТВ выключен — окно не трогается; режим дублирования; разный масштаб (DPI) панели и ТВ.
- `adev` с реальным `mmdevice` (с `--aout dummy` список пуст) и выбор устройства; «по умолчанию»
  (`id=""`) RC не умеет — компаньон делает это через `setvar.json?obj=aout&name=device&val=`,
  т.е. только при установленных Lua‑страницах.
- Громкость VLC при `--aout dummy` всегда читается как 0 — проверить `volume`/`mute` с настоящим выводом.
- `displayPower` (`SC_MONITORPOWER`) под Modern Standby, `sleep` (`SetSuspendState`), `shutdown.exe`
  и keep‑awake во время фильма — на этом ПК намеренно не вызывались.
- Системная громкость через NAudio: чтение проверено (`system.volume`), запись — нет.
- Обнаружение `_vlcremote._tcp` с телефона (`NsdManager`) и приём broadcast‑маяка через Wi‑Fi‑роутер.
- Запуск GUI‑VLC компаньоном (`--fullscreen`) и перезапуск VLC, запущенного из Проводника без API
  (закрытие главного окна Qt, восстановление файла из командной строки, уведомление и щелчок по нему).
- Автонастройка настоящего `%APPDATA%\vlc\vlcrc` и запуск VLC из Проводника после неё (здесь — только копия
  во временной папке).
- Окно сопряжения, открывающееся само поверх полноэкранного VLC на экране ТВ.
- Watch history with the real GUI VLC: VLC's own "continue playback?" bar no longer appears after `qt-continue=0`;
  a movie double-clicked in Explorer with a resume point → paused, then fullscreen on the TV after the phone's answer
  (and after Play pressed in VLC); a movie launched from the phone (`--fullscreen`) → paused fullscreen; the time from
  the start to the pause (a fraction of a second of picture/sound is expected); the next-episode card with a real
  series folder on the share; a track change made in VLC's menu (needs the Lua pages) remembered for the folder.
- Bluetooth с телефоном: подключение сопряжённого телефона к службе (Android `createRfcommSocketToServiceRecord`),
  `hello`/окно с кодом/команды по RFCOMM, задержка; телефон, сопряжённый «Just Works»; переход Wi‑Fi ↔ Bluetooth;
  выключение/включение Bluetooth на ноутбуке во время работы и восстановление службы после сна (Modern Standby).

## Отложено

- **Velopack** — установщик и обновления; сейчас exe запускается откуда угодно, автозапуск через `HKCU\Run`.
- **TMDB / библиотека** (M4) — разбор имён файлов, постеры.
- TLS с пиннингом, HDMI‑CEC.
