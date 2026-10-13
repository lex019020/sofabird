# vlc-remote — Android‑приложение

Пульт для VLC 3.0.x, запущенного на Windows‑ПК у телевизора. Работает в двух режимах:
напрямую с HTTP‑интерфейсом VLC (уровни 0/1) и через Windows‑компаньон (уровень 2).
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
                        CommandMapping (PlayerCommand → cmd/sys)
    src/test/           тесты на фикстурах из docs/research/samples (скопированы в resources)
  app/                  Android‑приложение (dev.vlcremote.app)
    data/vlc/           VlcHttpApi, VlcErrorClassifier, VlcHttpBackend (опрос, команды, уровни 0/1)
    data/companion/     CompanionSocket (рукопожатие), CompanionBackend (WebSocket, уровень 2)
    data/discovery/     NsdDiscovery, UdpBeaconDiscovery, SubnetScanner, LocalNetwork, DiscoveryRepository
    data/player/        PlayerController (единственный владелец активного backend), ConnectionTester, AppVisibility,
                        PairingFlow (подключение/сопряжение с компаньоном) + CompanionPairingLink, DeviceIdentity
    data/servers|settings|secrets|net
    service/            PlaybackService (FGS + MediaSession), RemotePlayer (SimpleBasePlayer), ServiceCoordinator
    ui/                 AppRoot (навигация, строка состояния), remote/, connection/, library/, playlist/, settings/
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

**Аппаратные клавиши громкости**: `MainActivity.dispatchKeyEvent` при включённой настройке и живом
соединении шлёт `volume&val=` на ПК и показывает результат снэкбаром; иначе — штатное поведение.

## Известные ограничения

- Насечки глав на шкале расположены равномерно: HTTP‑интерфейс VLC 3 не отдаёт смещения глав,
  только их количество и индекс (названия — на уровне ≥ 1).
- `pl_delete` в VLC ненадёжен — после удаления из плейлиста список перечитывается.
- На уровне 0 выбранная дорожка известна только телефону; если её переключили на ПК, пульт этого не увидит.
- `/art` VLC отдаёт обложку только текущего элемента; URL снабжён `?item=<plid>` как ключ кэша Coil.
- Удалённая громкость через MediaSession с погашенным экраном зависит от прошивки (см. research §2.1).
- Android 17 / targetSdk 37 потребует runtime‑разрешения `ACCESS_LOCAL_NETWORK` — пока targetSdk 35.
- Автоподключение при старте идёт к последнему ПК по LAN; выбора канала BT нет.

## Отложено

- **M3 — Bluetooth RFCOMM** (`BtTransport`, `CompanionDeviceManager`, автопереключение LAN↔BT) — `Capability.BLUETOOTH` и NDJSON‑кодек уже готовы.
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
9. Sheet «Ещё» с компаньоном: список аудиоустройств (`getAudioDevices` → `sys audioDevice`), экраны (`listDisplays` → `sys moveToDisplay`), «не спать»/экран/сон (`sys keepAwake|displayPower|sleep`), слайдер громкости Windows (`sys systemVolume`) — секции появляются только при соответствующих `caps`.
10. Имя телефона в окне сопряжения на ПК (`Settings.Global.DEVICE_NAME`, например «Galaxy S24»).
11. Состояния VLC на пульте: VLC закрыт → «Запустить VLC»; VLC открыт вручную → «без управления» →
    «Перезапустить с управлением» (тот же фильм на ТВ); статус на карточке ПК обновляется (~8–10 с).
12. «Другие способы подключения» раскрываются сами, если компаньон не найден за ~6 с.
