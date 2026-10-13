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
4. Находит VLC (`HKLM\SOFTWARE\VideoLAN\VLC`, затем Program Files). Если VLC запущен и отвечает
   по HTTP с нашим паролем — берёт его под контроль; если запущен без веб‑интерфейса — статус
   «VLC запущен без веб‑интерфейса» и пункт меню «Перезапустить VLC с веб‑интерфейсом».
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

## Меню в трее

- строка статуса VLC и число подключённых телефонов;
- **Сопряжение телефона…** — окно без рамки поверх всех окон на предпочитаемом экране (ТВ) с
  6‑значным кодом и адресом `host:port`; код действует 5 минут, не более 5 попыток;
- **Запустить VLC**, **Перезапустить VLC с веб‑интерфейсом**, **Закрыть VLC**;
- **Перенести VLC на экран ▸** — список мониторов; перенос = снять полный экран → `SetWindowPos`
  на выбранный монитор → полный экран;
- **Установить Lua‑страницы**, **Автозапуск** (`HKCU\…\Run`), **Не давать ПК уснуть во время фильма**;
- **Открыть журнал**, **Открыть папку настроек**, **Выход**.

## Где лежат настройки и журналы

`%LocalAppData%\VlcRemote\`:

| Файл | Что |
|---|---|
| `settings.json` | порты (`wsPort` 47555, `beaconPort` 47556), `listenAddress`, путь к VLC (`vlcPath`), `vlcHttpHost/Port/Password` (пароль генерируется при первом запуске), `vlcRcPort`, `launchFullscreen`, `vlcExtraArgs`, `mediaRoots`, `preferredDisplay`, `keepAwake`, `autoInstallLuaPages`, `autoLaunchVlc`, `advertiseDnsSd`, `installationId` |
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
- Запуск GUI‑VLC компаньоном (`--fullscreen`) и перезапуск VLC, запущенного из Проводника без API.

## Отложено

- **Bluetooth RFCOMM (M3)** — `RfcommServiceProvider` + тот же `Session` поверх `ILineTransport`.
- **Velopack** — установщик и обновления; сейчас exe запускается откуда угодно, автозапуск через `HKCU\Run`.
- **TMDB / библиотека** (M4) — разбор имён файлов, постеры.
- **UAC‑помощник для брандмауэра**, TLS с пиннингом, HDMI‑CEC.
