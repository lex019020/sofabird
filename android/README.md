# vlc-remote — Android‑приложение

Пульт для VLC 3.0.x, запущенного на Windows‑ПК у телевизора. Работает в двух режимах:
напрямую с HTTP‑интерфейсом VLC (уровни 0/1) и через Windows‑компаньон (уровень 2) — по Wi‑Fi
(WebSocket) или, если Wi‑Fi недоступен, по Bluetooth (RFCOMM).
Протокол — [`../protocol/README.md`](../protocol/README.md) и [`../protocol/vlc-http.md`](../protocol/vlc-http.md).

## Сборка

Требования: JDK 21, Android SDK с `platforms;android-35` и `build-tools;35.0.0`, доступ к Maven Central/Google.

```powershell
cd android
$env:JAVA_HOME = 'C:\Program Files\Android\openjdk\jdk-21.0.8'   # любой JDK 17/21
# local.properties (не коммитится): sdk.dir=D\:\\Android\\android-sdk
.\gradlew.bat :app:assembleDebug      # APK: app\build\outputs\apk\debug\app-debug.apk
.\gradlew.bat test                    # unit‑тесты обоих модулей
```

Стек: Gradle 8.6 (wrapper), AGP 8.4.2, Kotlin 2.0.21, Compose BOM 2024.10.01 (Material 3),
Ktor 3.0.1 (движок OkHttp 4.12), kotlinx.serialization 1.7.3, Koin 4.0.0, Media3 1.4.1,
DataStore 1.1.1, Coil 3.0.4, security‑crypto 1.1.0‑alpha06. `compileSdk`/`targetSdk` 35, `minSdk` 26.

## Варианты сборки и подпись

- `debug` — `dev.vlcremote.app.debug`, «VLC Remote (dev)», debug‑ключ; ставится рядом с основной.
- `release` — `dev.vlcremote.app`, подписывается общим ключом из `android/keystore.properties` (не в git) или
  переменных `VLCR_KEYSTORE_FILE`/`VLCR_KEYSTORE_PASSWORD`/`VLCR_KEY_ALIAS`/`VLCR_KEY_PASSWORD`. Так собираются
  тестовые сборки и релизы в CI; см. CLAUDE.md «Подпись APK».
- Версия: `versionName` из файла `VERSION` + `-Pvlcr.versionSuffix=beta.N`, `versionCode` из
  `-Pvlcr.versionCode=<число коммитов>`. Видна в «Настройки → О приложении».

## Структура

```
android/
  core/protocol/        чистый Kotlin/JVM, без Android‑зависимостей (dev.vlcremote.protocol)
    model/              PlayerState и части (Playback, Tracks, Chapters, Volume…), Capabilities,
                        ConnectionState/ConnectionError, Entry, PlaylistItem
    backend/            PlayerBackend (интерфейс), PlayerCommand, SystemCommand
    vlc/                VlcStatusParser (status/playlist/browse/tracks.json), VlcStateMapper,
                        VlcCommands (строки запросов), VlcErrorPage (детект «Error loading»)
    companion/          CompanionMessage (sealed, дискриминатор `t`), CompanionCodec,
                        CommandMapping (PlayerCommand → cmd/sys), NdjsonFramer/NdjsonReader (строки
                        NDJSON поверх потока байт RFCOMM, ≤ 1 МиБ), BtAddress, COMPANION_RFCOMM_UUID
    src/test/           тесты на фикстурах из docs/research/samples (скопированы в resources)
  app/                  Android‑приложение (dev.vlcremote.app)
    data/vlc/           VlcHttpApi, VlcErrorClassifier, VlcHttpBackend (опрос, команды, уровни 0/1)
    data/companion/     CompanionTransport (+ рукопожатие поверх любого канала), WebSocketTransport,
                        RfcommTransport, BluetoothSupport (разрешение, радио, сопряжённые устройства),
                        AndroidCompanionTransports (WS / RFCOMM / проба LAN), CompanionBackend (уровень 2,
                        выбор канала, возврат на Wi‑Fi)
    data/discovery/     NsdDiscovery, UdpBeaconDiscovery, SubnetScanner, LocalNetwork, DiscoveryRepository
    data/player/        PlayerController (единственный владелец активного backend), ConnectionTester, AppVisibility,
                        PairingFlow (подключение/сопряжение с компаньоном) + CompanionPairingLink, DeviceIdentity
    data/servers/       ServerRepository, CompanionServerSync (адреса LAN/Bluetooth из обнаружения → сохранённые ПК)
    data/settings|secrets|net
    service/            PlaybackService (FGS + MediaSession), RemotePlayer (SimpleBasePlayer), ServiceCoordinator
    domain/permissions/ PermissionRegistry: the single list of runtime permissions (see "Runtime permissions")
    ui/                 AppRoot (навигация, строка состояния), remote/, connection/, library/, playlist/, settings/,
                        permissions/ (first-run screen, Settings → Permissions, shared card and state)
    di/AppModule.kt     Koin
```

## Где лежат секреты

- Пароль HTTP‑интерфейса VLC и токен компаньона — `EncryptedSharedPreferences` (`secrets`, AES‑256 через
  Android Keystore), ключ — id сохранённого ПК (для компаньонов, найденных в сети, это GUID установки
  компаньона из маяка/`welcome`). Если Keystore на устройстве недоступен, `SecretStore`
  откатывается на обычные SharedPreferences (`secrets_fallback`) и пишет предупреждение в лог.
- Список ПК (без секретов) и настройки — DataStore (`servers`, `settings`).
- `local.properties` с путём к SDK не коммитится.

## Как работают режимы и уровни

| Режим | Что на ПК | Как определяется | Что умеет пульт |
|---|---|---|---|
| Прямой, уровень 0 | VLC с включённым веб‑интерфейсом и паролем | `GET /requests/tracks.json` → 404 | транспорт, громкость, главы по номерам, скорость, задержки, список дорожек (из `Stream N`), обзор файлов, плейлист, обложка |
| Прямой, уровень 1 | + наши Lua‑страницы в `%APPDATA%\vlc\lua\http` | `tracks.json` → 200 | + текущая дорожка, названия глав |
| Компаньон, уровень 2 | трей‑приложение на Windows | `welcome.caps` | + обнаружение, push‑состояние, запуск/закрытие VLC, «не спать», сон, экран, системная громкость, аудиоустройство |

**Прямой режим** (`VlcHttpBackend`): Basic‑auth с пустым логином; сетевой интерцептор OkHttp
удаляет заголовок `Connection` (иначе VLC рвёт соединение после каждого запроса); опрос 1 с при
воспроизведении, 3 с в паузе/стопе; опрос останавливается, когда приложение не видно и нет
foreground‑сервиса; один запрос в полёте на тип (status/playlist/browse), порядковые номера,
устаревшие ответы отбрасываются; ответ на команду применяется сразу, «чистый» статус — через 500 мс;
позиция интерполируется локально (`PlayerState.interpolatedTime`). Mute эмулируется (запоминаем
громкость, ставим 0). На уровне 0 текущая дорожка запоминается на телефоне и сбрасывается при
смене `currentplid`. Ошибки различаются: 403 «пароль не задан», 401 «неверный пароль», refused,
timeout, пустое тело, Lua‑страница с `<title>Error loading`.

**Компаньон** (`CompanionBackend`): `ws://host:47555/ws`, `hello` с токеном, `welcome` с `caps`,
push `state`, запросы `cmd`/`sys`/`browse`/`getPlaylist` по `id`, `ping` каждые 30 с (нет `pong` 10 с →
переподключение), экспоненциальный backoff, команды буферизуются до 5 с на время переподключения.
`pairing_required`/`unauthorized` при рукопожатии — терминальные (`ConnectionError.PairingRequired` /
`InvalidPairCode`): backend не долбит ПК повторами, а экран «Подключение» просит код.

**Bluetooth (M3)** — резервный канал к компаньону, когда Wi‑Fi нет или ПК по нему не отвечает:

- Протокол тот же: RFCOMM‑служба `47fe5bd6-98ad-4d9f-9812-174b3394037d`, NDJSON (строка UTF‑8 на сообщение,
  разделитель `\n`, ≤ 1 МиБ), то же `hello` с токеном. Сокет — *secure* `createRfcommSocketToServiceRecord` (ПК
  должен быть сопряжён, канал шифруется), канал RFCOMM ищется по SDP. `read() == -1` (Android 17) и `IOException` —
  обрыв. Блокирующие `connect()`/`read()` идут на `Dispatchers.IO`; отмена корутины закрывает сокет, чтобы они
  вернулись.
- Адрес Bluetooth ПК (`bt` из `welcome`, маяка и `/info`) хранится в `SavedServer.btAddress` и обновляется при
  каждом `welcome`; маяк/`/info` того же компаньона (по GUID) дописывают в сохранённый ПК адрес LAN или Bluetooth
  (`CompanionServerSync`). Адреса не стираются: маяк без `bt` (на ПК выключен Bluetooth) известный адрес не удаляет.
- Выбор канала при каждом (пере)подключении (`CompanionBackend`): сначала WebSocket (таймаут ~3 с); если не вышло,
  адрес Bluetooth известен, настройка «Использовать Bluetooth, когда Wi‑Fi недоступен» включена (по умолчанию),
  Bluetooth включён, разрешение есть и ПК среди сопряжённых → RFCOMM (до 12 с). Отказ в сопряжении или другой
  протокол по LAN — окончательный ответ, по Bluetooth его не перепроверяем. ПК, известный только по Bluetooth
  (адреса LAN ещё нет), подключается сразу по RFCOMM независимо от настройки.
- На RFCOMM `ping` каждые 15 с (на WebSocket — 30 с), нет `pong` 10 с → переподключение. Каждые ~20 с телефон
  проверяет LAN: `GET /info` по известному адресу (2 с), иначе короткий раунд UDP‑маяка с поиском по GUID/`bt`
  (так ПК, сопряжённый по Bluetooth, узнаёт свой IP). Ответил — открывается WebSocket, `hello` с тем же токеном и
  `getState`, и только потом закрывается RFCOMM (ответов на запросы «в полёте» ждём до 1 с): состояние на экране
  не сбрасывается, статус не уходит в «Подключение…».
- В шапке пульта рядом с именем ПК — значок «Wi‑Fi» / «Bluetooth», в строке статуса «Компаньон · Bluetooth»;
  переключение канала — снэкбар «Нет связи с ПК по Wi‑Fi — переключились на Bluetooth» / «Снова по Wi‑Fi».
- Permissions: `BLUETOOTH_CONNECT` (Android 12+, "Nearby devices") is requested on the first-run permissions screen
  (see "Runtime permissions"). In-context requests remain as a fallback and do nothing when it is already granted:
  turning the setting on, the remote's "… is unavailable over Wi‑Fi" card that appears when the LAN is down and
  Bluetooth could help (the card text is the rationale), and "Connect via Bluetooth" (a rationale dialog first).
  A refusal → a dialog linking to the app settings. Radio off → the system `ACTION_REQUEST_ENABLE` dialog ("Turn on").
  After the permission is granted / the radio is on, the reconnect happens at once, without waiting for the backoff.
  `BLUETOOTH`/`BLUETOOTH_ADMIN` are declared only up to Android 11 (`maxSdkVersion="30"`). Neither `BLUETOOTH_SCAN` nor
  location is needed: only bonded devices are used (hence `cancelDiscovery()` is not called on Android 12+ — it needs
  `BLUETOOTH_SCAN`).
- Первое подключение по Bluetooth: «Другие способы подключения» → «Подключиться по Bluetooth» → список
  сопряжённых устройств (имя + адрес, компьютеры — `BluetoothClass.Device.Major.COMPUTER` — сверху; кнопка
  «Настройки Bluetooth», список перечитывается при возврате) → `hello` по RFCOMM → тот же `PairingFlow`: код на
  экране ПК, окно для кода на телефоне, токен. Сохранённый ПК создаётся с id = GUID компаньона, `btAddress` и
  пустым `host`; как только маяк/`/info` этого компаньона виден в LAN, адрес дописывается в ту же запись (тот же
  токен). Если ПК уже был сопряжён по Wi‑Fi, `welcome.id` находит существующую запись — второй не появляется.
  Ошибка RFCOMM — «Компаньон не отвечает по Bluetooth: проверьте, что ПК сопряжён с телефоном и компаньон запущен».
- Нажатие на сохранённый ПК при недоступной LAN: `hello` по LAN, затем (если Bluetooth готов) по RFCOMM.
- Фон: foreground‑сервис `connectedDevice` подходит и для Bluetooth. Пока идёт переподключение (Wi‑Fi пропал →
  RFCOMM), `ServiceCoordinator` не гасит сервис до 2 минут: остановленный в фоне сервис нельзя запустить снова, а
  вместе с ним пропал бы и переход на Bluetooth.

**Подключение и сопряжение** (`PairingFlow`, экран «Подключение»):

1. Сверху — «Компьютеры рядом»: крупные карточки найденных компаньонов с именем ПК, состоянием VLC
   (`/info` → `vlc.status`: запущен / не запущен / без управления / запускается / не установлен) и
   отметкой «сопряжён», если для этого ПК есть токен.
2. Нажатие на карточку → `hello` с сохранённым токеном. Токен принят → сразу пульт. Ответ
   `pairing_required` (токена нет или ПК его забыл; старый компаньон отвечает `unauthorized`) → токен
   удаляется, **компаньон сам показывает код на экране ТВ/ноутбука**, а телефон — окно «Введите код с
   экрана компьютера»: шесть крупных ячеек, цифровая клавиатура открывается сама, код отправляется на
   шестой цифре (`hello` с `pairCode`). Успех → токен в `EncryptedSharedPreferences`, ПК сохраняется,
   подключение, переход на пульт, снэкбар «Сопряжение выполнено». `unauthorized` → «Неверный или
   устаревший код», поле очищается, окно остаётся. Отмена закрывает окно.
3. Первый запуск (сохранённых ПК нет) и в сети ровно один компаньон (ждём 1,5 с после первого ответа)
   → подключение стартует само: карточка «Найден компьютер … — подключаюсь…» с кнопкой «Отмена», затем
   окно для кода. Если ПК уже сохранены, автостарта нет: при запуске приложения идёт автоподключение к
   последнему ПК (`PlayerController.autoConnectLast`), как и раньше.
4. Если активный компаньон отверг токен, при открытии экрана окно для кода появляется сразу (и есть
   кнопка «Ввести код» в карточке текущего подключения).
5. Всё техническое — прямой режим VLC, адрес/порт, пароль веб‑интерфейса, скан подсети, инструкция по
   веб‑интерфейсу — под свёрнутым блоком «Другие способы подключения». Он раскрывается сам, только
   если за ~6 с поиска не найдено ни одного компаньона. Ручной ввод компаньона идёт тем же путём (код
   появится на экране ПК).

ПК сопоставляется с сохранённым по id, затем по GUID установки (`pcId`; адрес обновляется, если DHCP
выдал ПК другой IP), затем по адресу и порту.

**Состояние VLC на пульте (компаньон)**: `vlc.status` (нет у старых компаньонов → выводится из
`running`). Пока VLC не управляем, вместо шкалы, громкости и кнопок транспорта — карточка:
`notRunning` — «VLC не запущен» + «Запустить VLC» (`cmd launch`, только при `caps.launchVlc`);
`starting` — «VLC запускается…»; `noControl` (VLC открыт вручную до того, как компаньон его настроил) —
«VLC открыт без управления с телефона» + «Перезапустить с управлением» (`cmd launch`, позиция в фильме
не сохраняется); `notInstalled` — «На компьютере не найден VLC». После нажатия кнопка показывает
прогресс, пока компаньон не сообщит новое состояние (до 20 с). Sheet «Ещё» (громкость Windows, сон и
т. п.) доступен и в этих состояниях. В прямом режиме поведение прежнее.

**Экран для VLC (компаньон, `caps.windowControl`)**: на пульте рядом с чипами «Аудио»/«Субтитры» — чип
«Экран: <имя>» (значок ТВ для внешнего монитора, ноутбука — для встроенной панели). Он показывает монитор, где
сейчас окно VLC, а если VLC закрыт — монитор, на котором VLC откроется; имена мониторов читаются (`sys listDisplays`)
сразу после подключения. Чип работает и при закрытом VLC. Нажатие открывает sheet «Экран для VLC» (туда же ведёт
пункт «Экран» в «Ещё»): список мониторов — значок, понятное имя из EDID, разрешение, метки «основной» и «VLC здесь»;
переключатель отмечает предпочтительный монитор. Нажатие на монитор → `sys moveToDisplay {displayId}` (хаптика,
прогресс в строке): VLC переезжает туда сейчас, если открыт, и дальше открывается там; при закрытом VLC под
выбранным монитором подпись «VLC откроется на этом экране». Последний пункт «Не управлять» →
`sys preferredDisplay {displayId: null}`: компаньон не трогает окно. Предпочтительный монитор не подключён
(ТВ выключен) — пояснение под списком. Список перечитывается при открытии и после каждого действия; ошибка —
текстом в sheet и снэкбаром. После успешной команды выбор отмечается сразу (оптимистично), пока компаньон не
пришлёт новое `state.system` (`displayId`, `preferredDisplayId`); затем верит ему. Логика выбора — чистая
функция `displayChoice` (`ui/remote/DisplayChoice.kt`), покрыта тестами.
Старые компаньоны (мониторы без `id`): переключатель показывает монитор с VLC, перенос — `moveToDisplay {display: N}`
по индексу и только при открытом VLC, пункта «Не управлять» нет.

**Обнаружение**: NSD `_vlcremote._tcp` (одна сессия discovery, последовательный resolve,
MulticastLock, «потеря» с задержкой 3 с), UDP‑маяк `VLCREMOTE_DISCOVER 1` → `255.255.255.255:47556`
(плюс направленный broadcast подсети), опциональный скан `/24` по порту 8080 (48 параллельно,
таймаут 1 с, ближайшие к своему IP адреса первыми; 200/401/403 = «VLC есть»), ручной ввод.
Для найденных компаньонов дополнительно читается `GET /info` (состояние VLC, нужно ли сопряжение) —
сразу и затем не чаще раза в 8 с, пока экран открыт.

**Соединение в UI**: статус всегда в шапке (точка + уровень + задержка), переподключение тихое,
со снэкбаром; блокирующих диалогов нет (кроме окна для кода сопряжения на экране «Подключение»).
Ошибки авторизации (401/403 VLC, `pairing_required`/`unauthorized` компаньона) — терминальные: нужно
исправить пароль на экране «Подключение» или ввести код.

**Фон**: `PlaybackService` — foreground‑сервис типа `connectedDevice` (пререквизит —
`CHANGE_WIFI_MULTICAST_STATE` в манифесте) с media‑уведомлением (prev / −10 / play‑pause / +30 / next)
и Media3 `MediaSession` поверх `RemotePlayer : SimpleBasePlayer` (громкость объявлена как
`PLAYBACK_TYPE_REMOTE`, 0..100). Сервис запускается, когда есть соединение и (идёт воспроизведение
или приложение видно) при включённой настройке «Держать соединение в фоне»; останавливается через
10 минут после stop/паузы, при отключении или выключении настройки. Сервис — обычный
`LifecycleService`, а не `MediaSessionService`: последний сам вызывает `startForeground` с типом
`mediaPlayback`, что конфликтует с объявленным `connectedDevice`.

**Runtime permissions.** Project rule: every runtime permission is requested **at the first launch, after an
explanation**, and is listed in one place — `PermissionRegistry.all` (`domain/permissions/AppPermissions.kt`):
`PermissionItem(kind, permission, minSdk, addedInScreenVersion, title, rationale, without)`. Adding a permission:

1. Declare it in `AndroidManifest.xml`.
2. Add a `PermissionItem` to the registry with `addedInScreenVersion` = current maximum + 1, a new `PermissionKind`
   (and its icon in `ui/permissions/PermissionsScreen.kt`), and the three strings in `values/strings.xml` and
   `values-ru/strings.xml` (name; why it is needed — one sentence; what does not work without it).
3. Extend `AppPermissionsTest` (filtering by SDK level). An in-context request at the moment of use is optional
   and only a fallback.

Behaviour:

- First-run screen (`ui/permissions/PermissionsScreen.kt`), shown before the connection screen: title, an intro line
  and one card per permission that is a runtime permission on this Android version (Notifications — Android 13+,
  Nearby devices — Android 12+; on Android 8–11 there is nothing to ask and the screen is skipped). "Continue" asks
  for all of them in one system request (`RequestMultiplePermissions`); if everything is granted the app goes on at
  once, otherwise the cards show the result ("Allowed" / "Not allowed" / "Blocked" + "Open settings" → the app
  details page) and the next "Continue" goes on. "Not now" and Back skip.
- When it is shown: the DataStore key `permissions_screen_version` stores the registry version the user has passed
  (0 = never). The screen is shown while some applicable item with `addedInScreenVersion` greater than the stored
  version is not granted; i.e. on the first launch, and once more after an update that added a new permission. If
  there is nothing to ask, the current version is stored right away, so a permission revoked later in the system
  settings never brings the screen back (Settings → Permissions and the in-context fallbacks handle that).
- Status: granted → "Allowed"; `shouldShowRequestPermissionRationale` → "Not allowed" (can ask again); asked before
  (DataStore `permissions_requested`, filled by every request in the app) and no rationale → "Blocked" (only the
  app settings can grant it). Pure function `permissionStatus`, covered by tests.
- Settings → "Permissions": the same cards with the current status and "Allow" / "Open settings"; statuses are
  re-read on resume (returning from the system settings).
- Fallbacks: the notification request on connect fires only if the user skipped the screen and was never asked
  (not after answering the system dialog); the Bluetooth requests stay where they were.
- Test hook (no data loss):
  `adb shell am start -S -n <package>/dev.vlcremote.app.ui.MainActivity --ez show_permissions true` shows the screen
  again; `adb shell pm revoke <package> android.permission.BLUETOOTH_CONNECT` (and `POST_NOTIFICATIONS`) to start
  from scratch; `adb shell pm clear <package>` resets everything, including pairings.

**Аппаратные клавиши громкости**: `MainActivity.dispatchKeyEvent` при включённой настройке и живом
соединении шлёт `volume&val=` на ПК и показывает результат снэкбаром; иначе — штатное поведение.

## Watch history

Companion mode with the `history` capability only (protocol: "Watch history and resume" in
[`../protocol/README.md`](../protocol/README.md)); direct VLC mode and older companions show none of this.
The companion is the source of truth: the phone keeps no history of its own.

- **Protocol** (`:core:protocol`): `Capability.HISTORY`; `PlayerState.resume` / `next` / `history`
  (`ResumePrompt`, `NextEpisode`, `HistoryInfo`); `Entry.progress` / `finished`; messages `getHistory` → `history`,
  `historyUpdate` (only `finished` or `remove` is sent), `historyClear`; `PlayerCommand.Resume(ResumeChoice)` →
  `cmd resume {"choice"}`, `PlayerCommand.DismissNext` → `cmd dismissNext`. `PlayerBackend.history` /
  `updateHistory` / `clearHistory` fail with `UnsupportedOperationException` unless the backend implements them
  (only `CompanionBackend` does). All new fields are optional: old payloads decode as before.
- **Resume prompt** (`data/history/WatchPrompts.kt`, a Koin singleton): while `state.resume.pending` is set and the
  link is up, `ResumePromptHost` (called from `AppRoot`, so over any screen, including the permissions screen) shows
  a modal dialog: the movie title (up to 3 lines), a progress bar, "Stopped at 1:23:45 of 2:58:25" and two large
  full-width buttons, "Continue from 1:23:45" / "From the beginning" (labels wrap at large font sizes, never cut).
  Back and outside taps do not close it: VLC waits paused for the answer. The first tap wins locally
  (`ResumeDialogState`: a second tap sends nothing) and across phones (the PC drops `state.resume`, every dialog
  disappears). A failed answer brings the dialog back if the PC still waits (after a short grace when the PC
  rejected it, as it does when another phone was faster). Confirm haptic on the choice (setting "Button vibration").
- **Notification**: while the prompt stands, the foreground-service notification switches from the media style to a
  plain one with the movie title, "Continue from 1:23:45?" and the actions "Continue from 1:23:45" / "From the
  beginning" (on Android 13+ a media-style notification shows only the media session's buttons, so custom actions
  would be invisible there). The same for the next-episode offer: "Next: <title>" with "Play next", play/pause and
  "Dismiss". Afterwards the media controls come back.
- **Next episode**: `state.next` → a card on the Remote screen under the title: "Next episode", the title (2 lines
  max), "Dismiss" (`cmd dismissNext`) and "Play" (`cmd open {uri}`). Nothing is played automatically. Either button
  hides the card at once (also in the notification) until the PC offers something else.
- **Library**: the tab starts with "Continue watching" (`getHistory continue`, up to 20): horizontal cards with the
  title (always 2 lines, ellipsis), a progress bar and "1:23:45 of 2:58:25", or a "Next episode" badge. Cards grow
  with the system font size. Tap → `cmd open` (the PC then asks about resuming) and the Remote screen; long press →
  sheet with Play / Mark as watched / Remove from history. The row scrolls away with the list and the folder
  toolbar then sticks to the top. With nothing to continue the row is replaced by a "History" link; the row header
  has a "History" button too. Files of the folder that are in the history show a thin progress bar (started) or a
  check mark (finished), from the optional `progress` / `finished` of `entries`.
- **History screen** (route `history`): `getHistory all`, pages of 50 loaded as the end comes into view; grouped
  into Today / Yesterday / This week (calendar week, first day from the locale) / Earlier (`groupHistoryByDate`,
  clock injectable); each row: title, folder, progress bar + times or "Watched" with a check mark. Swipe left to
  remove; long press or "⋮" → Play / Mark as watched or unwatched / Remove; "⋮" in the top bar → "Clear history"
  with a confirmation naming the PC. Edits are optimistic; a failure shows a snackbar and reloads.
- **Refresh**: Library and History watch `state.history.revision` while they are on screen and refresh (debounced
  1–1.5 s; the PC saves the position every 10 s during playback). A refresh failure keeps the current list.

## Известные ограничения

- Насечки глав на шкале расположены равномерно: HTTP‑интерфейс VLC 3 не отдаёт смещения глав,
  только их количество и индекс (названия — на уровне ≥ 1).
- `pl_delete` в VLC ненадёжен — после удаления из плейлиста список перечитывается.
- На уровне 0 выбранная дорожка известна только телефону; если её переключили на ПК, пульт этого не увидит.
- `/art` VLC отдаёт обложку только текущего элемента; URL снабжён `?item=<plid>` как ключ кэша Coil.
- Удалённая громкость через MediaSession с погашенным экраном зависит от прошивки (см. research §2.1).
- Android 17 / targetSdk 37 потребует runtime‑разрешения `ACCESS_LOCAL_NETWORK` — пока targetSdk 35.
- Автоподключение при старте идёт к последнему ПК: LAN, затем Bluetooth (если адрес известен и разрешение есть).
- Сопряжение Bluetooth (телефон ↔ ПК) делается в настройках Android/Windows; приложение его не инициирует и
  не ищет новые устройства (нет `BLUETOOTH_SCAN`). `CompanionDeviceManager` не используется.
- Обложки и миниатюры по Bluetooth не передаются (`/art` — HTTP по LAN).

## Отложено

- `CompanionDeviceManager` (системный выбор ПК, presence‑события) вместо собственного списка сопряжённых устройств.
- **M4 — постеры/метаданные** (TMDB через компаньон, Room для «продолжить просмотр», Quick Settings‑плитка, Glance‑виджет).
- Таймер сна.

## Проверить на устройстве

Эмулятора на этой машине нет, собрано и проверено только unit‑тестами (парсеры, команды,
сообщения компаньона, опрос/команды `VlcHttpBackend` на Ktor `MockEngine`). Нужна проверка на телефоне:

1. NSD: находит ли `NsdManager` службу компаньона (`_vlcremote._tcp`), работает ли resolve на целевой прошивке; UDP‑маяк как запасной путь.
2. Foreground‑сервис `connectedDevice` на Android 14/15: старт из `ServiceCoordinator`, уведомление с кнопками, остановка через 10 минут; запрос `POST_NOTIFICATIONS` на Android 13+.
3. MediaSession: кнопки гарнитуры и экран блокировки; клавиши громкости при погашенном экране (`RemotePlayer`, `PLAYBACK_TYPE_REMOTE`).
4. Клавиши громкости на переднем плане (`dispatchKeyEvent`), хаптика на кнопках.
5. Coil и `/art` с Basic‑auth (интерцептор `ArtAuth`), обложка в уведомлении.
6. `EncryptedSharedPreferences` на целевом устройстве (fallback не должен срабатывать).
7. Ландшафтный макет пульта (две колонки), predictive back в sheet’ах.
8. Сопряжение: первый запуск с одним компаньоном в сети → автостарт, код появляется на ТВ, окно для кода
   на телефоне (автофокус, цифровая клавиатура Samsung, отправка на 6‑й цифре); неверный код → ошибка и
   очистка поля; «Отмена»; токен сохраняется, после перезапуска приложения — подключение без кода;
   реконнект после сна ноутбука; «Забыть этот компьютер» → при следующем нажатии снова код.
9. Sheet «Ещё» с компаньоном: список аудиоустройств (`getAudioDevices` → `sys audioDevice`), пункт «Экран» открывает «Экран для VLC» (п. 14), «не спать»/экран/сон (`sys keepAwake|displayPower|sleep`), слайдер громкости Windows (`sys systemVolume`) — секции появляются только при соответствующих `caps`.
10. Имя телефона в окне сопряжения на ПК (`Settings.Global.DEVICE_NAME`, например «Galaxy S24»).
11. Состояния VLC на пульте: VLC закрыт → «Запустить VLC»; VLC открыт вручную → «без управления» →
    «Перезапустить с управлением» (тот же фильм на ТВ); статус на карточке ПК обновляется (~8–10 с).
12. «Другие способы подключения» раскрываются сами, если компаньон не найден за ~6 с.
13. **Bluetooth** (Galaxy S24 ↔ ноутбук у ТВ с Intel BT, уже сопряжены):
    1. Подключение по Wi‑Fi → в `welcome` пришёл `bt`; в «Сохранённых ПК» в строке ПК есть «Bluetooth».
    2. Выключить Wi‑Fi на телефоне при открытом пульте → снэкбар «переключились на Bluetooth» и значок «Bluetooth»
       в шапке за ~3–5 с; пауза, громкость, перемотка работают, задержка на глаз приемлема.
    3. Без разрешения «Устройства поблизости»: карточка «… недоступен по Wi‑Fi» → «Разрешить» → системный диалог →
       подключение по Bluetooth сразу. Отказ → диалог «Настройки приложения».
    4. Включить Wi‑Fi обратно → не позже ~20 с значок «Wi‑Fi», снэкбар «Снова по Wi‑Fi»; статус ни разу не ушёл в
       «Подключение…», название и позиция не мигнули.
    5. Wi‑Fi и Bluetooth телефона выключены → карточка «включите Bluetooth» → «Включить» → системный диалог → подключение.
    6. «Другие способы подключения» → «Подключиться по Bluetooth»: ноутбук наверху списка (иконка компьютера), адрес
       совпадает с `bt` компаньона; выбор → код на экране ТВ → ввод → пульт. Затем включить Wi‑Fi → у сохранённого ПК
       появился IP (та же запись, без повторного кода).
    7. Ноутбук не сопряжён / компаньон закрыт → «Компаньон не отвечает по Bluetooth…» не позже чем через ~12 с,
       приложение не виснет, «Отмена» во время подключения работает.
    8. Фон: идёт фильм, приложение свёрнуто, уведомление есть → выключить Wi‑Fi → через минуту кнопки уведомления
       работают (сервис пережил переход на RFCOMM). Экран погашен 10+ минут — соединение живо (ping 15 с).
    9. Сон ноутбука во время сессии по Bluetooth → переподключение после пробуждения.
    10. Компаньон закрывается во время сессии по Bluetooth → обрыв замечен сразу (`read() == -1`/`IOException`),
        дальше обычные попытки переподключения.
14. **Экран для VLC** (Galaxy S24 ↔ ноутбук у ТВ: встроенная панель + ТВ по HDMI, компаньон с `id` мониторов):
    1. На пульте чип «Экран: <имя ТВ>» со значком ТВ (или «<панель>» со значком ноутбука); имя — из EDID, как в
       параметрах экрана Windows; на узком экране чипы переносятся на вторую строку без обрезки «Ещё».
    2. VLC открыт на ноутбуке → sheet: «VLC здесь» у панели, переключатель на предпочтительном мониторе, разрешения
       верные, «основной» у основного. Нажать ТВ → хаптика, прогресс, окно VLC на ТВ (полный экран сохранился),
       «VLC здесь» и переключатель переехали на ТВ, чип «Экран: <ТВ>».
    3. Закрыть VLC → чип показывает предпочтительный монитор; в sheet подпись «VLC откроется на этом экране»;
       выбрать панель ноутбука → запустить VLC с телефона и двойным кликом по файлу на ПК → оба раза окно на панели.
    4. «Не управлять» → переключатель на нём; VLC открывается там, где решит Windows, компаньон окно не двигает.
    5. Выключить ТВ при выбранном ТВ → в sheet нет ТВ, пояснение «Выбранный экран сейчас не подключён»; включить ТВ →
       кнопка «Обновить» (или переоткрыть sheet) → ТВ снова в списке и выбран.
    6. Перетащить окно VLC мышью на другой монитор → чип и «VLC здесь» обновились (push `state`), выбор не сбросился.
    7. Старый компаньон (без `id`): список по индексам, переключатель = где VLC, «Не управлять» нет, при закрытом VLC
       строки неактивны с подсказкой обновить компаньон.
    8. Пульт по Bluetooth: перенос работает так же.
15. **First-run permissions** (Galaxy S24, Android 16). Reset: `adb shell pm clear dev.vlcremote.app` (forgets
    pairings) or `pm revoke` both permissions + the `show_permissions` hook above.
    1. Fresh start → the "Permissions" screen before "Connection": two cards (Notifications, Nearby devices), nothing
       cut off; also with the largest font size and in Russian.
    2. "Continue" → two system dialogs in a row → both allowed → straight to "Connection"; the in-context notification
       request does not appear on connect; the Bluetooth fallback (Wi‑Fi off) works without any dialog.
    3. Refuse one → the card shows "Not allowed", "Continue" goes on. Refuse twice (or "Don't allow" again from
       Settings → Permissions) → "Blocked" + "Open settings" opens the app details page; after allowing there and
       returning, the status is "Allowed".
    4. "Not now" → "Connection"; on the first connection Android asks for notifications once (fallback).
    5. Restart the app → the screen does not appear again.
16. **Watch history** (Galaxy S24 ↔ TV laptop, companion with `history`; check in English and Russian, also with the
    largest font size).
    1. Play a movie for 6+ minutes, stop, open it again from the Library → VLC pauses on the TV, the phone shows
       "Continue watching?" with the title, progress and "Continue from 1:23:45" / "From the beginning"; nothing is cut
       off, buttons are large; a tap gives a firm vibration and VLC seeks (or starts from 0) and plays.
    2. Open the same movie by double-clicking it on the laptop → the dialog appears on the phone too (any tab, also
       Settings / Connection). Pressing Play in VLC itself → the dialog disappears.
    3. Two phones (or phone + second app install): both show the dialog; answer on one → the other closes it within
       ~1 s, no error snackbar.
    4. App in the background with the notification → open a resumable movie on the laptop → the notification shows
       "Continue from …?" with both actions; each works; afterwards the media controls return. Phone locked: the actions
       are reachable from the lock screen notification.
    5. Wi‑Fi off while the dialog is open → it closes; after the reconnect (Wi‑Fi or Bluetooth) it reappears if VLC
       still waits.
    6. Finish an episode of a series folder (or seek to the last 5 %) → the Remote screen shows "Next episode: …" with
       Play / Dismiss and the notification "Play next"; Play opens the next file (then the resume question if it was
       started); Dismiss hides it on the phone and the PC; nothing starts by itself.
    7. Library: "Continue watching" row on top with cards (2-line titles, bar, "1:23:45 of 2:58:25", "Next episode"
       badge); tap plays and goes to the Remote; long press → Mark as watched (card disappears, the file in its folder
       gets a check mark) / Remove from history. Scroll: the row scrolls away, the folder toolbar sticks. Folder rows:
       thin bar on started files, check mark on finished ones; marks update while a movie plays (~10 s).
    8. History: groups Today / Yesterday / This week / Earlier; rows with folder and progress or "Watched"; swipe left
       removes; menu → Mark as unwatched resets the progress; scroll through 50+ entries loads more; "Clear history" →
       confirmation with the PC name → empty screen text; the Library row is empty too ("History" link).
    9. Old companion (no `history`) and direct VLC mode: no dialog, no card, no row, no History link, no marks.
