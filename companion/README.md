# VLC Remote Companion (Windows)

Компаньон для Android‑пульта `vlc-remote`: трей‑приложение на .NET 9, которое владеет жизненным
циклом VLC 3.0.x на ПК у телевизора и отдаёт телефону уровень 2 протокола
([protocol/README.md](../protocol/README.md)): текущие дорожки, названия глав, аудиоустройства,
push‑события, запуск VLC, перенос окна на ТВ, питание, системная громкость.

Этап **M2** (LAN). Bluetooth RFCOMM, установщик Velopack и TMDB‑библиотека — позже (см. ниже).

## Состав

```
companion/
├── VlcRemote.sln
├── VlcRemote.Core/            библиотека net9.0 без Windows‑API (тестируется на любой ОС)
│   ├── Protocol/              сообщения протокола (System.Text.Json, source‑generated, полиморфизм по "t")
│   ├── Vlc/                   VlcHttpClient, VlcJsonParser, VlcRcClient, RcParser
│   ├── State/                 StateBuilder (status.json + tracks.json + RC → state), StateCoalescer (≤ 4/с)
│   ├── Pairing/               PairingService (код на 5 мин, токен 32 байта base64url, хранится SHA‑256)
│   ├── Session/               Session (NDJSON‑сессия поверх ILineTransport), ISessionHost, CommandArgs
│   └── Lua/                   LuaPagesInstaller (копия штатных страниц VLC + наши в %APPDATA%)
├── VlcRemote.Companion/       WinExe net9.0-windows10.0.19041.0: трей, Kestrel, VlcManager, Win32
│   ├── Net/                   WebServer (/ws, /info, /art), WebSocketLineTransport, UdpBeaconResponder, DnsSdAdvertiser
│   ├── Vlc/                   VlcLocator, VlcManager (процесс, опрос, RC, команды), EmbeddedLuaPages
│   ├── Services/              DisplayService, PowerService, AudioService, AutostartService
│   └── Tray/                  TrayContext (меню), PairingForm (код на экране ТВ), TrayIconFactory
├── VlcRemote.Core.Tests/      xunit: парсеры на реальных ответах VLC 3.0.23 (docs/research/samples), сессия, сопряжение
└── lua/                       Lua‑страницы уровня 1 и install.ps1 (работают и без компаньона)
```

Принцип разделения: `Core` не знает про WinForms и ASP.NET — сессия работает поверх абстракции
«строка туда / строка сюда» (`ILineTransport`), так что транспорт RFCOMM в M3 добавляется
отдельным классом рядом с `WebSocketLineTransport`.

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

`http-host` не трогается (по умолчанию VLC слушает все интерфейсы — телефон может ходить к VLC и напрямую).

- Файл правится **только когда ни один `vlc.exe` не запущен** (VLC перезаписывает vlcrc из памяти): при старте
  компаньона и каждый раз, когда VLC закрывается.
- Закомментированные умолчания (`#extraintf=`) раскомментируются на месте; отсутствующий ключ дописывается в
  конец секции, отсутствующая секция — в конец файла. Кодировка (UTF‑8 с BOM) и окончания строк сохраняются,
  запись атомарная (временный файл + замена). Если файла нет (VLC ни разу не запускался) — создаётся
  минимальный файл с этими тремя секциями.
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

## Меню в трее

- строка статуса VLC и число подключённых телефонов;
- **Сопряжение телефона…** — окно без рамки поверх всех окон на экране VLC / предпочитаемом экране (ТВ) с
  6‑значным кодом и адресом `host:port`; код действует 5 минут, не более 5 попыток (открывается и само,
  когда несопряжённый телефон пытается подключиться — см. «Сопряжение»);
- **Запустить VLC**, **Перезапустить VLC с веб‑интерфейсом**, **Закрыть VLC**;
- **Перенести VLC на экран ▸** — список мониторов; перенос = снять полный экран → `SetWindowPos`
  на выбранный монитор → полный экран;
- **Установить Lua‑страницы**, **Автозапуск** (`HKCU\…\Run`), **Не давать ПК уснуть во время фильма**;
- **Открыть журнал**, **Открыть папку настроек**, **Выход**.

## Где лежат настройки и журналы

`%LocalAppData%\VlcRemote\`:

| Файл | Что |
|---|---|
| `settings.json` | порты (`wsPort` 47555, `beaconPort` 47556), `listenAddress`, путь к VLC (`vlcPath`), `vlcHttpHost/Port/Password` (пароль генерируется при первом запуске), `vlcRcPort`, `launchFullscreen`, `vlcExtraArgs`, `mediaRoots`, `preferredDisplay`, `keepAwake`, `autoInstallLuaPages`, `autoLaunchVlc`, `configureVlc` (автонастройка vlcrc, по умолчанию `true`), `vlcrcPath`, `advertiseDnsSd`, `installationId` |
| `paired.json` | сопряжённые устройства: SHA‑256 токена, имя, id, дата |
| `logs\companion-YYYYMMDD.log` | Serilog, ротация по дням, 14 файлов |

Пароль HTTP‑интерфейса VLC в журнал не пишется.

## Брандмауэр

При первом `listen` Windows покажет диалог «Оповещение системы безопасности». Нажмите
**Разрешить доступ** для частных сетей. Кнопка **Отмена** молча создаёт блокирующие правила для
exe — тогда телефон не увидит ПК; правила придётся удалить вручную
(`Параметры → Брандмауэр → Разрешить взаимодействие с приложением`) или добавить от администратора:

```powershell
netsh advfirewall firewall add rule name="VLC Remote Companion" dir=in action=allow program="<путь к exe>" protocol=TCP localport=47555 profile=private remoteip=localsubnet
netsh advfirewall firewall add rule name="VLC Remote Companion UDP" dir=in action=allow program="<путь к exe>" protocol=UDP localport=47556 profile=private remoteip=localsubnet
```

Такой же диалог появится для `vlc.exe`, если его HTTP‑интерфейс слушает `0.0.0.0` (прямой режим
с телефона). Компаньон сам ходит к VLC только через `127.0.0.1`.

## Что проверено на этом ПК (один монитор, VLC 3.0.23)

Живая проверка с безголовым VLC (`-I dummy --vout dummy --aout dummy`, HTTP `127.0.0.1:8099`,
RC `4212`) и компаньоном на `127.0.0.1:47557` с отдельной папкой данных:

- `/info`, UDP‑маяк, `/art` (401 без токена, 200 `image/jpeg` с Bearer), DNS‑SD регистрация;
- `hello` с неверным токеном → `err unauthorized` и закрытие; с токеном → `welcome` + первый `state`;
- `pause/play/seek/track/volume/mute/chapter/rate/random/playlistRemove/open/launch/quit`,
  `getState/getPlaylist/getTracks/getAudioDevices/browse`, `sys listDisplays/keepAwake/moveToDisplay`;
- текущие дорожки через RC `atrack/strack` (без Lua‑страниц в VLC) и выбор дорожек после `open`;
- события `vlcStarted`, `inputChanged`, `vlcExited`; push `state` ~2–3 раза в секунду при воспроизведении;
- `LuaPagesInstaller` — только против временной папки (юнит‑тесты), в `%APPDATA%` не ставился.

### Проверить на ноутбуке у ТВ

- Перенос окна на второй монитор (`moveToDisplay`, `--qt-fullscreen-screennumber`): здесь один
  экран и безголовый VLC без окна — проверена только ветка «окно не найдено». Порядок экранов в
  меню стабилизирован по имени устройства (`\\.\DISPLAY1`, `\\.\DISPLAY2`); Qt может нумеровать иначе.
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

## Отложено

- **Bluetooth RFCOMM (M3)** — `RfcommServiceProvider` + тот же `Session` поверх `ILineTransport`.
- **Velopack** — установщик и обновления; сейчас exe запускается откуда угодно, автозапуск через `HKCU\Run`.
- **TMDB / библиотека** (M4) — разбор имён файлов, постеры.
- **UAC‑помощник для брандмауэра**, TLS с пиннингом, HDMI‑CEC.
