# Мобильный стек и UX для Android‑пульта VLC (HTPC на Windows)

Дата: 2026‑10‑12. Статус: исследование (research), не спецификация.

Связанные документы:
- [live-probe.md](live-probe.md) — живая проверка HTTP/RC‑интерфейсов VLC 3.0.23 на этом ПК (шкала громкости 0..512, ID дорожек `Stream N`, `browse.json`, `/art`, собственные Lua‑страницы). Здесь на него опираемся и не дублируем.
- `vlc-protocols.md` — планируется (протоколы VLC и компаньона).

Обозначения: **✅** — проверено по первоисточнику (ссылка рядом); **«не проверено»** — по памяти, вторичным источникам или требует эксперимента на устройстве.

Сценарий: ноутбук с Windows 11 подключён к телевизору, фильмы лежат в `D:\shared`, зритель сидит на диване с телефоном. Пульт должен работать в полутёмной комнате, одной рукой, не глядя на экран телефона дольше секунды.

---

## 0. Кратко (TL;DR)

1. **Стек: Kotlin + Jetpack Compose** (то, что уже есть в проекте). Все критичные для пульта возможности — RFCOMM, `NsdManager`, `MediaSession` с «удалённой громкостью», `TileService`, Glance‑виджет, foreground‑сервис, перехват клавиш громкости — это платформенные API без обёрток. Flutter / React Native / .NET MAUI добавляют слой плагинов ровно там, где у нас самая рискованная часть. Compose Multiplatform держать в уме структурно (чистый Kotlin‑модуль протокола и парсера имён без Android‑зависимостей), но UI в CMP не переводить: iOS не в планах, а Material 3 в CMP отстаёт от Jetpack.
2. **Версии (октябрь 2026, проверены):** Kotlin 2.4.21, AGP 9.4.0 (Gradle 9.6, JDK 17), Compose BOM 2026.09.00 (ui 1.12.1, material3 1.4.0), Navigation 3 1.2.0, Media3 1.11.1, Ktor 3.6.0, OkHttp 5.5.0, kotlinx.serialization 1.11.0, Koin 4.2.2 / Hilt 2.60.1, Room 2.8.5, DataStore 1.2.1, Coil 3.6.3, Glance 1.2.0. Material 3 Expressive — только в `material3:1.5.0-beta01` под `@ExperimentalMaterial3ExpressiveApi`; для MVP остаёмся на 1.4.0.
3. **Платформа:** клавиши громкости в foreground — `dispatchKeyEvent` в Activity; при погашенном экране — через `MediaSession` + `Player` с `DeviceInfo.PLAYBACK_TYPE_REMOTE` (Media3 сам подключает `VolumeProviderCompat`). Это документированный механизм (так работает Cast), но на Android 12 была регрессия и есть OEM‑нюансы — проверять на реальном телефоне. Соединение держать foreground‑сервисом типа `connectedDevice` (пререквизит: `BLUETOOTH_CONNECT` или manifest‑разрешение `CHANGE_WIFI_MULTICAST_STATE`), **не** `mediaPlayback`. Android 17 (API 37) вводит runtime‑разрешение `ACCESS_LOCAL_NETWORK` — без него ни mDNS, ни HTTP к 192.168.x.x не работают при targetSdk 37.
4. **UX:** одна главная вкладка «Пульт» = Now Playing + транспорт + громкость; крупный play/pause внизу в зоне большого пальца; кнопки ±10/±30 с; scrub‑бар с главами; быстрые sheet’ы «Субтитры/Аудио» с языками и тонкой подстройкой задержки; «Продолжить просмотр»; тёмная тема с опцией true‑black; хаптика на каждом «тике». Главные жалобы на существующие VLC‑пульты: *отваливается соединение*, *мучительная настройка пароля HTTP‑интерфейса*, *навязчивая реклама и дроблёные покупки*, *слишком чувствительная перемотка*. Это и есть наше поле для отличия.
5. **Метаданные:** парсить имена файлов по правилам guessit/PTN (regex‑срезы: год, SxxEyy, качество, кодеки, группа → остаток = название). Поскольку разработчик знает C#, разумно делать парсер и индексацию **в ПК‑компаньоне** (там же `Emby.Naming`‑подобные правила и ffmpeg‑миниатюры), а телефону отдавать готовый JSON; Kotlin‑порт — лёгкий fallback для режима «без компаньона». Постеры — TMDB (бесплатно для некоммерческого использования, обязательная атрибуция, кэш ≤ 6 месяцев, лимит ~40 rps без жёсткой цифры); кэш — Coil 3; fallback — `/art` VLC и миниатюры от компаньона. Субтитры (OpenSubtitles/Subdl) — **не в v1**.
6. **IA:** 3 вкладки (Пульт · Библиотека · Плейлист) + экран «Подключение» + «Настройки». Состояния: `Disconnected → Connecting → Connected{VlcNotRunning | Idle | Playing/Paused}` + `Error(reason)`. Онбординг: «Найти ПК → Пароль/PIN → Тест (пауза на ТВ)».
7. **Тесты:** Compose‑тесты + Robolectric/Roborazzi, Preview Screenshot Testing; fake‑VLC на Ktor, воспроизводящий причуды из `live-probe.md`; эмулятор ходит к хосту через `10.0.2.2` (mDNS в эмуляторе не работает); Bluetooth — только реальное устройство.

---

## 1. Выбор стека для телефона

### 1.1. Критерии и сравнение

Критерии взяты из требований пульта: Bluetooth Classic (RFCOMM/SPP) как резервный канал, обнаружение ПК по mDNS (`NsdManager`), WebSocket к компаньону / HTTP к VLC, живучесть соединения в фоне, перехват аппаратных клавиш громкости, хаптика, виджет + плитка быстрых настроек, Wear OS потом, боль сборки, размер APK.

| Критерий | Kotlin + Jetpack Compose | Compose Multiplatform / KMP | Flutter | React Native | .NET MAUI |
|---|---|---|---|---|---|
| Bluetooth RFCOMM | Нативно: `BluetoothSocket`/`createRfcommSocketToServiceRecord`, runtime `BLUETOOTH_CONNECT` (API 31+) ✅ [[BluetoothSocket]](https://developer.android.com/reference/android/bluetooth/BluetoothSocket) | То же, в `androidMain` (expect/actual) | Плагины: `flutter_bluetooth_serial` заброшен (последний push янв. 2025, 102 issue, Android‑only); `flutter_classic_bluetooth` 0.1.x — молодой ✅ [[pub.dev]](https://pub.dev/packages/flutter_classic_bluetooth/versions/0.1.4) | `react-native-bluetooth-classic` 1.73.0‑rc — статус New Architecture не подтверждён ✅ [[jsdelivr]](https://www.jsdelivr.com/package/npm/react-native-bluetooth-classic) | Первой стороны нет; `Plugin.BLE` — только BLE ✅ [[nuget]](https://www.nuget.org/packages/plugin.ble). RFCOMM — писать руками через Android‑биндинги |
| mDNS / `NsdManager` | Нативно; `MulticastLock` до T‑ext‑7; с Android 17 — `ACCESS_LOCAL_NETWORK` (см. §3.7) | То же | Плагины (`nsd`, `bonsoir`) — не проверено | `react-native-zeroconf` — статус New Arch не подтверждён | Платформенный код |
| WebSocket / HTTP | OkHttp 5.5 или Ktor 3.6 (оба с WebSocket) | Ktor (мультиплатформенно) | `web_socket_channel`, `dio` — зрелые | `ws` встроен | `ClientWebSocket` в .NET — зрелый |
| Фоновая связь (FGS, типы) | Нативно (`connectedDevice`) | То же | Плагин/нативный код для FGS‑типов Android 14+ | Нативный модуль | Платформенный код |
| Клавиши громкости | `Activity.dispatchKeyEvent`; `MediaSession` + `VolumeProvider` | То же в `androidMain` | Плагин (`hardware_buttons`‑класс, часто заброшены); MediaSession — через `audio_service` | Нативный модуль | `OnKeyDown` override в `MainActivity` — просто (это Android‑класс) |
| Хаптика | `LocalHapticFeedback` + `HapticFeedbackType.Confirm/Reject/SegmentTick/GestureEnd` ✅ [[API]](https://developer.android.com/reference/kotlin/androidx/compose/ui/hapticfeedback/HapticFeedbackType) | То же | `HapticFeedback.*` — базовое | `Vibration`/`expo-haptics` | `HapticFeedback.Perform` — базовое |
| Виджет / QS‑плитка | Glance 1.2.0 + `TileService` | Только Android‑часть | `home_widget` + нативный `TileService` | Нативно | Платформенный код, RemoteViews руками |
| Wear OS | Wear Compose 1.7.1 + ProtoLayout/Tiles, Data Layer | Android‑часть | Отдельный нативный модуль | Нативно | Практически нет |
| Сборка/инструменты | Один Gradle‑проект, Studio, Compose Preview, Hot Reload (через CMP‑плагин) | + iOS‑toolchain, Xcode; Hot Reload «из коробки» | Отдельный SDK, Dart; стабильный DevTools; Flutter 3.47 (авг. 2026) ✅ [[docs]](https://docs.flutter.dev/release/whats-new.html) | Node + Gradle, New Architecture обязательна с RN 0.82 ✅ [[Expo]](https://docs.expo.dev/guides/new-architecture.md) | MAUI 10 (патч 10.0.90, июль 2026; EOL май 2027) ✅ [[policy]](https://dotnet.microsoft.com/ru-ru/platform/support/policy/maui) |
| Размер APK | Минимальный: Compose использует системный Skia; тест 2023: ~1.5 МБ vs Flutter ~×5 ✅ [[Medium]](https://medium.com/@jacobras/android-ios-native-vs-flutter-vs-compose-multiplatform-7ef3d5ec2a56) (старый бенчмарк, не hello‑world) | На Android = как нативный | Движок в бандле, +несколько МБ | Hermes + JS bundle, +несколько МБ | .NET runtime, обычно 10+ МБ (не проверено) |

Вывод по таблице: **каждая «сложная» строка (BT, mDNS, FGS, громкость, виджеты) — это Android‑API**, и в кроссплатформенных стеках она превращается либо в чужой плагин с неясной поддержкой, либо в тот же Kotlin‑код, завёрнутый в биндинг. Выигрыш кроссплатформы нулевой при нулевом спросе на iOS.

### 1.2. Состояние Jetpack Compose в октябре 2026

- **Compose BOM 2026.09.00** → `ui/foundation/runtime/animation 1.12.1`, `material3 1.4.0` ✅ [[BOM mapping]](https://developer.android.com/develop/ui/compose/bom/bom-mapping).
- **Material 3 Expressive.** В стабильном `material3:1.4.0` публичны только `WideNavigationRail` и `ShortNavigationBar`; `MaterialExpressiveTheme`, `MotionScheme`, `ButtonGroup`, `FloatingToolbar`, `SplitButton`, `LoadingIndicator`, FAB‑menu и пр. — в линии **1.5.0** (beta01 от 07.10.2026) под `@ExperimentalMaterial3ExpressiveApi` ✅ [[release notes]](https://developer.android.com/jetpack/androidx/releases/compose-material3), [[composables.com]](https://composables.com/material3/materialexpressivetheme). Для MVP: 1.4.0; Expressive‑компоненты (особенно `ButtonGroup` для ±10/±30 и `FloatingToolbar`) — после выхода 1.5.0 stable. В 1.5.0‑alpha29 есть source‑breaking изменение `Slider` (`onValueChange` обязателен, переход на `SliderState`) — учесть при миграции.
- **Адаптивные макеты:** `material3-adaptive 1.3.0` (12.08.2026) — `ListDetailPaneScaffold`, `SupportingPaneScaffold`, `NavigationSuiteScaffold`, `adaptive-navigation3` ✅ [[releases]](https://developer.android.com/jetpack/androidx/releases/compose-material3-adaptive). Важно для пульта: телефон в ландшафте = ширина *medium*, высота *compact* → двухпанельный макет по одной ширине не работает, нужно учитывать и высоту ✅ [[ProAndroidDev]](https://proandroiddev.com/adaptive-compose-layouts-86b7f1e51338).
- **Navigation 3 1.2.0** (23.09.2026): свой back‑stack как state, `NavDisplay`, Result API, predictive back через `NavigationBackHandler` ✅ [[releases]](https://developer.android.com/jetpack/androidx/releases/navigation3). Для 3 вкладок + sheet’ов — более чем достаточно; `navigation-compose` 2.x оставлять не нужно.
- **Compose Preview Screenshot Testing** требует AGP ≥ 9.0, Kotlin ≥ 2.2.10, JDK 17 ✅ [[docs]](https://developer.android.com/studio/preview/compose-screenshot-testing).
- Компилятор Compose — плагин `org.jetbrains.kotlin.plugin.compose` версии Kotlin (с 2.0), у нас уже подключён.

### 1.3. Compose Multiplatform / KMP: зрелость для iOS в 2026

- iOS в CMP стабилен с **1.8.0 (май 2025)**: нативная физика скролла, выделение текста, RTL, drag‑and‑drop; JetBrains заявляет паритет по старту и скроллу с SwiftUI и ~+9 МБ к размеру ✅ [[JetBrains blog]](https://blog.jetbrains.com/kotlin/2025/05/compose-multiplatform-1-8-0-released-compose-multiplatform-for-ios-is-stable-and-production-ready/).
- Текущая версия **1.12.1 (22.09.2026)**; Material3 в CMP — `1.12.0-alpha03` на базе Jetpack `material3 1.5.0-alpha22`, т.е. **Material 3 в CMP идёт по alpha‑ветке**; Navigation3 1.1.2; Compose Hot Reload 1.2.0 + MCP‑сервер ✅ [[releases]](https://github.com/JetBrains/compose-multiplatform/releases).
- Практика: единичные сообщения о проблемах `TextField` с сторонними IME на iOS (2025, не воспроизведено другими) ✅ [[Zenn]](https://zenn.dev/bcc/articles/45170a8518ef78) — не критично для пульта, но показывает, что edge‑cases ещё живы.
- **Решение:** UI остаётся Jetpack Compose. Но слои **протокол VLC/компаньона, модель состояния плеера, парсер имён файлов** оформить как чистый Kotlin‑модуль (`commonMain`‑совместимый: без `android.*`, на Ktor + kotlinx.serialization + coroutines). Это бесплатно, упрощает JVM‑тесты и оставляет дверь в CMP.

### 1.4. Hilt vs Koin

| | Hilt (Dagger 2.60.1) | Koin 4.2.2 |
|---|---|---|
| Проверка графа | Compile‑time (KSP 2.3.12) | Runtime (есть `koin-annotations` с compile‑time проверкой, не проверено на 4.2) |
| Требования | AGP ≥ 9.0, minSdk 23 (мультидекс выпилен в 2.60) ✅ [[releases]](https://github.com/google/dagger/releases) | Нет; есть `koin-compose-navigation3` для Navigation 3 ✅ [[releases]](https://github.com/InsertKoinIO/koin/releases) |
| KMP | Нет (JVM/Android) | Да |
| Boilerplate | `@HiltViewModel`, `@AndroidEntryPoint`, модули | `module { viewModelOf(::X) }`, `koinViewModel()` |
| Сервисы/TileService/Glance | `@AndroidEntryPoint` для сервисов; для Glance — `EntryPointAccessors` | `KoinComponent` / `get()` везде |

Рекомендация: **Koin** — приложение небольшое, много точек входа вне Activity (FGS, `TileService`, Glance‑коллбеки, `MediaSessionService`), где Hilt требует обходных путей; плюс KMP‑совместимость чистого модуля. Если важна compile‑time проверка — Hilt тоже нормально, но тогда минимум AGP 9 и Kotlin‑версия, которую Dagger уже поддерживает (2.60 собран с Kotlin 2.3.21; с 2.4.x — проверить).

### 1.5. Версии библиотек (проверено 2026‑10‑12)

| Компонент | Стабильная версия | Дата | Источник |
|---|---|---|---|
| Kotlin | **2.4.21** | 08.10.2026 | ✅ [github](https://github.com/JetBrains/kotlin/releases) |
| Android Gradle Plugin | **9.4.0**; Gradle 9.6.0, JDK 17, Build‑Tools 36.0.0, maxSdk 37 | сент. 2026 | ✅ [dev.android](https://developer.android.com/build/releases/gradle-plugin) |
| Compose BOM | **2026.09.00** (ui 1.12.1, material3 1.4.0) | сент. 2026 | ✅ [mapping](https://developer.android.com/develop/ui/compose/bom/bom-mapping) |
| Material 3 | 1.4.0; 1.5.0‑beta01 (Expressive) | 07.10.2026 | ✅ [releases](https://developer.android.com/jetpack/androidx/releases/compose-material3) |
| material3‑adaptive | **1.3.0** | 12.08.2026 | ✅ [releases](https://developer.android.com/jetpack/androidx/releases/compose-material3-adaptive) |
| Navigation 3 | **1.2.0** | 23.09.2026 | ✅ [releases](https://developer.android.com/jetpack/androidx/releases/navigation3) |
| Media3 (session) | **1.11.1** | 10.09.2026 | ✅ [releases](https://developer.android.com/jetpack/androidx/releases/media3) |
| Ktor | **3.6.0** | 16.09.2026 | ✅ [github](https://github.com/ktorio/ktor/releases) |
| OkHttp | **5.5.0** | 16.08.2026 | ✅ [changelog](https://github.com/square/okhttp/blob/master/CHANGELOG.md) |
| kotlinx.serialization | **1.11.0** (1.12.0‑RC 04.09) | 09.04.2026 | ✅ [github](https://github.com/Kotlin/kotlinx.serialization/releases) |
| kotlinx.coroutines | **1.11.0** | 08.05.2026 | ✅ [github](https://github.com/Kotlin/kotlinx.coroutines/releases) |
| KSP | **2.3.12** (версия больше не привязана к Kotlin) | 09.09.2026 | ✅ [github](https://github.com/google/ksp/releases), [PR](https://github.com/google/ksp/pull/3107) |
| Dagger/Hilt | **2.60.1**; androidx.hilt 1.4.0 | 06.07.2026 | ✅ [github](https://github.com/google/dagger/releases) |
| Koin | **4.2.2** | 15.06.2026 | ✅ [github](https://github.com/InsertKoinIO/koin/releases) |
| Room | **2.8.5**; Room 3.0.1 (`androidx.room3`, KMP‑ориентированный) | 09.09.2026 / 29.07.2026 | ✅ [room](https://developer.android.com/jetpack/androidx/releases/room), [room3](https://developer.android.com/jetpack/androidx/releases/room3) |
| DataStore | **1.2.1** (1.3.0‑alpha11) | 11.03.2026 | ✅ [releases](https://developer.android.com/jetpack/androidx/releases/datastore) |
| Coil | **3.6.3** (`coil-network-okhttp` / `coil-network-ktor3`) | 18.09.2026 | ✅ [github](https://github.com/coil-kt/coil/releases) |
| Glance | **1.2.0** (1.3.0‑alpha02) | 26.08.2026 | ✅ [releases](https://developer.android.com/jetpack/androidx/releases/glance) |
| Wear Compose | **1.7.1** (Material 3 для Wear стабилен) | 07.10.2026 | ✅ [releases](https://developer.android.com/jetpack/androidx/releases/wear-compose) |
| Compose Multiplatform | **1.12.1** | 22.09.2026 | ✅ [github](https://github.com/JetBrains/compose-multiplatform/releases) |
| Android 17 (API 37) | выпущен | июнь 2026 | ✅ [OkHttp changelog](https://github.com/square/okhttp/blob/master/CHANGELOG.md) (упоминание) |

Замечания:
- Room 3 (`androidx.room3`) — новый пакет с фокусом на KMP; для Android‑only приложения Room 2.8.x остаётся актуальным (на странице Room 2 deprecation‑нотиса нет). Для нашей БД (позиции просмотра, кэш метаданных, список ПК) достаточно Room 2.8.5 или даже DataStore + файл JSON; Room берём ради запросов «продолжить просмотр».
- Media3 1.10 убрал команды изменения *локальной* громкости из `MediaController`; удалённая (`DeviceInfo.PLAYBACK_TYPE_REMOTE`) — работает (см. §3.1).
- Ktor 3.5 чинил пинг WebSocket в OkHttp‑движке, 3.4.x — зацикливание на битых фреймах; брать ≥ 3.6.0.

### 1.6. Рекомендация по стеку

**Kotlin 2.4.21 + Jetpack Compose (BOM 2026.09.00, Material 3 1.4.0) + Navigation 3 + Media3 Session + Ktor‑client 3.6 (OkHttp‑engine) + kotlinx.serialization + Koin + Room 2.8 + DataStore + Coil 3 + Glance.** Разделить на модули: `:core:protocol` (чистый Kotlin), `:core:naming` (парсер имён, чистый Kotlin), `:app`. Почему Ktor, а не «голый» OkHttp: один клиент для HTTP‑поллинга VLC, WebSocket к компаньону и (позже) TMDB; `MockEngine` для тестов; движок всё равно OkHttp. Coil подключить через `coil-network-okhttp` на том же `OkHttpClient`, что и Ktor (общий пул соединений, заголовок `Authorization` для `/art`).

---

## 2. Платформенные особенности Android для пульта

### 2.1. Аппаратные клавиши громкости

**Приложение на переднем плане.** Переопределить `dispatchKeyEvent` в `ComponentActivity`: на `KEYCODE_VOLUME_UP/DOWN` с `ACTION_DOWN` отправить команду VLC и вернуть `true` (системный слайдер не покажется). Это стандартный механизм: необработанные события падают в `PhoneFallbackEventHandler`, где и обрабатывается громкость ✅ [[Tuts+]](https://code.tutsplus.com/android-sdk-intercepting-physical-key-events--mobile-10379t). `Modifier.onKeyEvent` в Compose ненадёжен для этих клавиш (нужен фокус) — не проверено, но лучше Activity‑уровень. Автоповтор при удержании: `event.repeatCount`. Сделать это **настройкой** («Клавиши громкости управляют: ПК / телефоном»), т.к. иногда нужно регулировать уведомления телефона.

**Экран погашен / приложение в фоне (главный вопрос).** Механизм — `MediaSession` с «удалённой» громкостью, так работает Cast:

- Legacy: `MediaSessionCompat.setPlaybackToRemote(VolumeProviderCompat)`; провайдер получает `onAdjustVolume(direction)` / `onSetVolumeTo(volume)` и **обязан** вызывать `setCurrentVolume()` после каждого изменения ✅ [[VolumeProvider]](https://developer.android.com/reference/android/media/VolumeProvider.html). Сессия должна быть `active` и иметь `PlaybackState` ✅ [[MediaSession]](https://developer.android.com/reference/android/media/session/MediaSession).
- Media3: пишем свой `Player` (наследник `SimpleBasePlayer`, который отражает состояние VLC), в `State` возвращаем `DeviceInfo(PLAYBACK_TYPE_REMOTE, min=0, max=100)` и команды `COMMAND_ADJUST_DEVICE_VOLUME_WITH_FLAGS` + `COMMAND_SET_DEVICE_VOLUME_WITH_FLAGS`. `MediaSessionLegacyStub` сам создаёт `VolumeProviderCompat` для любого `playbackType != PLAYBACK_TYPE_LOCAL`, вызывает `player.increaseDeviceVolume/decreaseDeviceVolume/setDeviceVolume` и пробрасывает `setCurrentVolume` ✅ [[источник MediaSessionLegacyStub.java]](https://github.com/androidx/media/blob/release/libraries/session/src/main/java/androidx/media3/session/MediaSessionLegacyStub.java). Тип контроля: relative при `ADJUST`, absolute при `ADJUST`+`SET`.
- Известные грабли: в Android 12 (beta 5) ключи громкости перестали доходить до удалённых сессий; починено в 12.0.0_r26 через `canHandleVolumeKey` — разрешено, если playback локальный, **или** у приложения один маршрут, **или** включён `config_volumeAdjustmentForRemoteGroupSessions` (на Pixel выключен для групп) ✅ [[esper.io]](https://esper.io/blog/android-12-cast-volume-changes-explained). Для нас (один маршрут) должно проходить. OEM‑прошивки с «управлением треками при выключенном экране» (LineageOS) могут перехватывать ✅ [[Poweramp forum]](https://forum.powerampapp.com/topic/21716-volume-keys-control-does-not-work-when-the-screen-is-off/). Пользователи Chromecast сообщают, что на заблокированном экране кнопки работают при включённом «Media Session usage» и ломались на некоторых QPR‑бетах ✅ [[Nest community]](https://www.googlenestcommunity.com/t5/Streaming/Enhancing-Chromecast-Experience-Simplifying-Media-Volume-Control-during/m-p/462235).
- Условия, чтобы сессия оставалась «верхней» и активной с погашенным экраном: `MediaSessionService` + foreground‑уведомление, `PlaybackState = PLAYING` (мы отражаем состояние VLC), `isActive = true`. Поскольку наш `Player` ничего не воспроизводит локально, аудиофокус не нужен.
- **Android 17 (все приложения):** «background audio hardening» ограничивает фоновые вызовы *воспроизведения, аудиофокуса и изменения громкости*; при targetSdk 37 фоновый аудио‑доступ требует FGS с while‑in‑use ✅ [[behavior-changes-all]](https://developer.android.com/about/versions/17/behavior-changes-all), [[behavior-changes-17]](https://developer.android.com/about/versions/17/behavior-changes-17). Относится ли это к `VolumeProvider` удалённой сессии (мы не трогаем `AudioManager`) — **не проверено**; риск, что при targetSdk 37 придётся держать FGS с WIU‑возможностью. Держать targetSdk 36 до проверки.
- **Doze.** Телефон на диване неподвижен → через ~30+ минут с погашенным экраном входит в Doze, сеть приостанавливается (кроме окон обслуживания) ✅ [[Doze]](https://developer.android.com/training/monitoring-device-state/doze-standby). Распространяется ли это на процесс с FGS — источники расходятся (третьи стороны говорят «да, всем») — **не проверено**. Практика: нажатие клавиши громкости вызывает `onAdjustVolume` в нашем процессе; если сокет заснул — послать одноразовый HTTP‑запрос с коротким таймаутом; если и он заблокирован — предложить в настройках «Не ограничивать батарею» (`ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS`; для личного приложения допустимо, для Play — требует обоснования).

**Итог по feasibility:** управление громкостью ПК с погашенным экраном **реализуемо и документировано** (Cast‑путь), но зависит от версии Android/прошивки и Doze. В MVP — foreground‑перехват (гарантированно) + MediaSession‑путь как «работает там, где работает», с тестом на реальном устройстве и чек‑листом в §6.4.

### 2.2. Поддержание соединения: foreground‑сервис и его тип

С Android 14 (API 34) тип FGS обязателен, плюс соответствующее `FOREGROUND_SERVICE_*` разрешение и декларация в Play Console ✅ [[FGS types]](https://developer.android.com/develop/background-work/services/fgs/service-types).

| Тип | Разрешение | Пререквизиты во время запуска | Подходит? |
|---|---|---|---|
| `connectedDevice` | `FOREGROUND_SERVICE_CONNECTED_DEVICE` | Хотя бы одно: manifest `CHANGE_NETWORK_STATE` / `CHANGE_WIFI_STATE` / **`CHANGE_WIFI_MULTICAST_STATE`** / `NFC` / `TRANSMIT_IR`, **или** granted runtime `BLUETOOTH_CONNECT`/`SCAN`/`ADVERTISE`, или `UsbManager.requestPermission()` | **Да.** `CHANGE_WIFI_MULTICAST_STATE` нам и так нужен для `MulticastLock` (mDNS); `BLUETOOTH_CONNECT` — для RFCOMM. Описание типа: «взаимодействие с внешними устройствами по Bluetooth/NFC/IR/USB **или сети**» |
| `mediaPlayback` | `FOREGROUND_SERVICE_MEDIA_PLAYBACK` | Нет | Формально — «продолжение аудио/видео в фоне»; мы ничего не воспроизводим; с targetSdk 15 нельзя стартовать из `BOOT_COMPLETED`. Использовать **только если** MediaSession‑путь громкости потребует именно этого типа (не проверено) |
| `dataSync` | `FOREGROUND_SERVICE_DATA_SYNC` | Нет | Нет: лимит 6 ч/сутки с targetSdk 35 ✅ [[Android 15]](https://developer.android.com/about/versions/15/behavior-changes-15) |
| `specialUse` | `FOREGROUND_SERVICE_SPECIAL_USE` | `<property PROPERTY_SPECIAL_USE_FGS_SUBTYPE>` с объяснением; ревью в Play | Запасной вариант |

Стратегия:
- FGS **не висит постоянно**. Запускается при подключении из UI (`startForeground` разрешён, пока приложение видно), держится пока: приложение на экране **или** идёт воспроизведение на ПК и пользователь не выходил из сеанса (уведомление MediaSession с кнопками). Останавливается через N минут после `stopped`/отключения VLC или по кнопке «Отключиться» в уведомлении.
- Android 16: задания JobScheduler, идущие параллельно с FGS, теперь тоже под квотой ✅ [[Android 16 all]](https://developer.android.com/about/versions/16/behavior-changes-all) — на нас не влияет (WorkManager не используем для связи).
- Android 17: `AbandonedJob`, `MessageQueue` lock‑free, лимит памяти на `RemoteViews` в виджетах `1.5×W×H×4` байт ✅ [[Android 17]](https://developer.android.com/about/versions/17/behavior-changes-17) — важно для Glance‑виджета с постером (ужимать до ≤ 300 px).

### 2.3. Уведомление и экран блокировки как у медиаплеера

`MediaSessionService` (Media3) + наш `SimpleBasePlayer`‑обёртка над состоянием VLC даёт: карточку в шторке с обложкой (из `/art` или TMDB), кнопками prev/±10/play‑pause/+30/next, прогрессом (мы знаем `time/length`), управлением с заблокированного экрана и из Android Auto/Wear бесплатно. Media3 1.11 ужесточил threading `MediaSession` и сделал `onConnect` read‑only для недоверенных контроллеров ✅ [[Media3 1.11]](https://developer.android.com/jetpack/androidx/releases/media3). Kore 3.0 сделал ровно это («интеграция с медиа‑уведомлениями Android») ✅ [[Kore 3.0]](https://kodi.tv/article/kore-3-0), а один из запросов к Kore — «вести себя как медиаплеер, принимать Media Buttons» ✅ [[issues]](https://github.com/xbmc/Kore/issues) — т.е. нужно и Bluetooth‑гарнитурам/часам.

### 2.4. Quick Settings tile

`TileService` с `BIND_QUICK_SETTINGS_TILE` и фильтром `QS_TILE`; на API 33+ предлагать добавить плитку через `StatusBarManager.requestAddTileService()` из видимой Activity ✅ [[API diff 33]](https://developer.android.com/sdk/api_diff/33/changes/android.app.StatusBarManager), [[docs]](https://developer.android.com/develop/ui/views/quicksettings-tiles). Режим `META_DATA_ACTIVE_TILE` + `requestListeningState()` при смене состояния VLC, чтобы плитка показывала ▶/❚❚ без биндинга при каждом открытии шторки. `onClick` → команда `pl_pause` (через наш репозиторий, а не через `MediaSessionManager` — тогда не нужен `NotificationListener`). Если нет соединения — `Tile.STATE_UNAVAILABLE` с подписью «ПК не найден».

### 2.5. Виджет на Glance

Glance 1.2.0: лямбда‑действия стабильны, `GlanceTheme`/`glance-material3`, `SizeMode.Responsive`, `providePreview` ✅ [[Glance]](https://developer.android.com/jetpack/androidx/releases/glance). Содержимое: постер (маленький, ≤ 300 px — лимит `RemoteViews` на Android 17), название, ▶/❚❚, −/+ громкость, «+30». Состояние — из `DataStore` через `GlanceStateDefinition`, обновление — из FGS при каждом изменении статуса. Нажатия идут через `actionRunCallback` → репозиторий → HTTP. Без соединения — одна кнопка «Подключиться» (открывает приложение).

### 2.6. Wear OS (позже)

Wear Compose 1.7.1; `compose-material` для Wear объявлен superseded в пользу `compose-material3` ✅ [[Wear Compose]](https://developer.android.com/jetpack/androidx/releases/wear-compose). Tiles — через ProtoLayout + `protolayout-material3` (Expressive) ✅ [[codelab]](https://developer.android.com/codelabs/wear-tiles). Самый дешёвый путь — вообще без watch‑приложения: MediaSession из §2.3 даёт стандартное медиа‑управление на часах. Отдельный Tile с play/pause/громкостью — когда появится компаньон и стабильный протокол. `glance-wear-tiles` deprecated, не использовать ✅.

### 2.7. Локальная сеть и mDNS

- **Android 17 / targetSdk 37:** новое runtime‑разрешение **`ACCESS_LOCAL_NETWORK`** (группа `NEARBY_DEVICES`), обязательно для любого трафика к локальным адресам: mDNS/SSDP сырыми сокетами, `NsdManager`, **OkHttp/Cronet к 192.168.0.0/16, 10/8, 172.16/12, link‑local, multicast/broadcast**. Исключения — только DNS:53 и Output Switcher. На Android 16 это opt‑in через временное `NEARBY_WIFI_DEVICES`; тест: `adb shell am compat enable RESTRICT_LOCAL_NETWORK <pkg>`; при блокировке — `EPERM`/`ECONNABORTED` ✅ [[Android 16 targeting]](https://developer.android.com/about/versions/16/behavior-changes-16), [[Android 17 targeting]](https://developer.android.com/about/versions/17/behavior-changes-17), [[local network permission]](https://developer.android.com/privacy-and-security/local-network-permission). Не запрашивать до перехода на targetSdk 37; в онбординге объяснять, зачем («чтобы видеть ваш ПК в домашней сети»).
- **`NsdManager`:** до SDK‑extension T‑7 приложение само берёт `WifiManager.MulticastLock` даже на переднем плане; с T‑ext‑7 система делает это сама для foreground‑приложений; проверять `SdkExtensions`, а не `Build.VERSION` ✅ [[NsdManager]](https://developer.android.com/reference/kotlin/android/net/nsd/NsdManager). Исторически «капризен» (одна сессия discovery за раз, зависшие listener’ы после краша) ✅ [[CommonsWare]](https://commonsware.com/office-hours/2021-04-27); Kore использует JmDNS ✅ [[Kore README]](https://github.com/xbmc/Kore). Для нас: `NsdManager` + ручной ввод IP + «последний удачный адрес» как fallback; компаньон публикует `_vlc-remote._tcp` с TXT (`vlc=3.0.23`, `companion=1`, `http=8080`).
- Эмулятор изолирован за виртуальным роутером — multicast не проходит, mDNS в эмуляторе **не работает**; хост = `10.0.2.2` ✅ [[emulator networking]](https://developer.android.com/studio/run/emulator-networking-address).

### 2.8. Bluetooth Classic (RFCOMM)

- API 31+: runtime `BLUETOOTH_CONNECT` для `createRfcommSocketToServiceRecord()`; `BLUETOOTH_SCAN` только если ищем устройства (нам достаточно `bondedDevices`) ✅ [[BluetoothSocket]](https://developer.android.com/reference/android/bluetooth/BluetoothSocket).
- **Android 17:** `InputStream.read()` на RFCOMM возвращает `-1` при закрытии/обрыве вместо `IOException` — цикл чтения должен проверять оба ✅ [[Android 17]](https://developer.android.com/about/versions/17/behavior-changes-17). Android 16: `ACTION_KEY_MISSING` при потере bond, Android 17 — автономное повторное сопряжение с диалогом пользователю ✅.
- Windows‑сторона (компаньон на C#): `RfcommServiceProvider.CreateAsync(RfcommServiceId.FromUuid(...))` + `StreamSocketListener` (WinRT, работает из desktop .NET) либо 32feet.NET ✅ [[MS docs]](https://msdn.microsoft.com/library/windows/apps/dn263511), [[32feet]](https://inthehand.com/components/32feet/). Сопряжение делается в настройках Windows/Android, приложение только подключается к SPP‑UUID. Bluetooth — **резервный канал** (Wi‑Fi упал, роутер перезагружается) и для «разбудить ноутбук» — не основной.

### 2.9. Батарея и когда отключаться

Правила:
1. Экран приложения виден → соединение держим (WebSocket/поллинг 500–1000 мс по `status.json`, см. `live-probe.md`: ~21 мс на localhost).
2. Приложение ушло в фон, на ПК идёт воспроизведение → FGS + MediaSession, поллинг реже (2–5 с) или только push от компаньона.
3. На ПК `stopped` > 10 мин **или** телефон потерял Wi‑Fi → закрыть сокет, остановить FGS, оставить «тихое» уведомление не нужно. Возобновление — по открытию приложения, плитке, виджету или клавише громкости (через MediaSession, если сессия ещё жива).
4. Doze: не бороться; реконнект с экспоненциальным backoff + jitter при возвращении; тест `adb shell dumpsys deviceidle force-idle`.

### 2.10. Прочие изменения платформы, влияющие на UI

- **Edge‑to‑edge обязателен** при targetSdk 36 на Android 16 (opt‑out отключён) ✅ — в Compose это `enableEdgeToEdge()` + `WindowInsets`; для пульта это плюс: контент под статус‑баром, кнопки над навигационной панелью.
- **Predictive back** включён по умолчанию при targetSdk 36; `onBackPressed`/`KEYCODE_BACK` не приходят ✅ — использовать `BackHandler`/Navigation 3.
- **Ориентация:** на экранах ≥ sw600dp (планшеты, Chromebook) `screenOrientation` игнорируется при targetSdk 36, временный opt‑out не действует для 37 ✅ — ландшафтный макет нужен в любом случае (§4.2).

---

## 3. UX‑исследование: что берём у лучших пультов

### 3.1. Обзор приложений

| Приложение | Что устроено хорошо (паттерны) | Что ругают / чего не хватает |
|---|---|---|
| **Kore** (Kodi, open source, Apache‑2.0, v3.1.0 янв. 2024) ✅ [[F‑Droid]](https://f-droid.org/en/packages/org.xbmc.kore/), [[Kore 3.0]](https://kodi.tv/article/kore-3-0) | Пульт с *текущим состоянием воспроизведения прямо на экране пульта*; Now Playing с деталями фильма/сериала; смена/синхронизация/загрузка субтитров и аудиодорожек; плейлист; библиотека с постерами; WoL; «Отправить в Kodi» (YouTube/URL через share‑sheet + ввод текста); Material 3 + dynamic color; медиа‑уведомления Android | Запросы: «Resume playing» (6 👍), «Playback speed» (2), «показывать диалоги Kodi в Kore», «вести себя как медиаплеер (media buttons)», «регулятор громкости слишком мелкий»; на бете 3.0 просили **большие прямоугольные кнопки вместо мелких иконок** ✅ [[issues]](https://github.com/xbmc/Kore/issues), [[forum]](https://forum.kodi.tv/showthread.php?tid=369711) |
| **Plex** (спецификация Companion/remote) ✅ [[wiki]](https://github.com/plexinc/plex-media-player/wiki/Remote-control-API), [[Android 7.14]](https://apkmirror.com/apk/plex-inc/plex/plex-7-14-1-9954-release) | Кнопка Now Playing *серая, когда ничего не играет*; при остановке на ТВ остаёмся на экране Now Playing в состоянии «остановлено», не выкидываем; ошибка команды → toast + считать плеер отключённым; удалённое управление размером/цветом/позицией/смещением субтитров | Companion фактически «почти deprecated», публичного API нет ✅ [[forum]](https://forums.plex.tv/t/feature-request-supported-server-mediated-api-for-controlling-remote-plex-plexamp-sessions/942315) |
| **Jellyfin** (Android) ✅ [[Symfonium]](https://support.symfonium.app/t/allow-symfonium-to-control-jellyfin-on-other-devices/7973) | «Cast to session»: пульт — просто ещё один пункт в меню Cast; список активных сессий | Зависит от возможностей целевого клиента; команды могут молча игнорироваться |
| **Google TV Remote** (M3 Expressive, v4.39, авг. 2025) ✅ [[9to5google]](https://9to5google.com/2025/08/23/google-tv-app-material-3-expressive/), [[TechHive]](https://www.techhive.com/article/582309/the-android-tv-remote-app-for-ios-is-as-bare-bones-as-the-android-version.html) | Пульт — полноэкранная страница, а не выезжающая панель; переключатель **D‑pad ↔ свайп‑тачпад**; голос; выбор устройства внизу крупными кнопками | Регрессия: long‑press на «select» сломался после редизайна — урок: тестировать долгие нажатия |
| **Apple TV Remote** (Control Center) ✅ [[HowToGeek]](https://www.howtogeek.com/720922/how-to-use-your-iphone-or-ipad-as-an-apple-tv-remote/), [[HIG]](https://developer-mdn.apple.com/design/human-interface-guidelines/inputs/remotes) | Большая жестовая поверхность; **физические кнопки громкости телефона управляют ТВ**; HIG: не реагировать на случайный тап во время воспроизведения, не переопределять стандартные жесты | D‑pad иногда доступен только из Control Center (баг) |
| **Infuse** ✅ [[support]](https://support.firecore.com/hc/en-us/articles/215090907), [[community]](https://community.firecore.com/t/subtitle-delay/14737.md), [[2x]](https://community.firecore.com/t/temporary-2x-speed-on-apple-tv-like-ios-has/57701.md) | Меню Playback с вкладкой Subtitles: язык/файл, внешний вид, позиция, **offset**; временная 2× при удержании (8.3.2) | Смещение субтитров приходится подбирать для каждого файла — просят запоминать; удержание > 10 с утомляет — просят toggle; OSD закрывает субтитры — прятать контролы быстро; просят список пресетов скорости |
| **Unified Remote** ✅ [[App Store]](https://apps.apple.com/us/app/825534179), [[Softpedia]](https://mobile.softpedia.com/android/Unified-Remote-Android-Review-2859.html) | Медиа‑пульт «play/pause/громкость/треки всегда в один тап»; хаптика (настраиваемая); без логина; list/grid; тёмная тема | Дёргания трекпада; drag отпускается; серверная часть глючит на lock‑screen Windows |
| **KDE Connect** (MPRIS) ✅ [[bug 390414]](https://bugs.kde.org/show_bug.cgi?id=390414), [[D12559]](https://phabricator.kde.org/D12559) | Слайдер громкости на каждый аудиовыход (sink) + mute по иконке; слайдер позиции = перемотка | Отказались добавлять FF/RW‑кнопки «ради простоты поддержки» — пользователи просили; MPRIS у VLC «kinda broken» |
| **Sonos** (редизайн 2024 → фикс 2026) ✅ [[CMSWire]](https://www.cmswire.com/customer-experience/sonos-app-redesign-fail-customer-outrage-and-falling-stock-prices), [[forum]](https://en.community.sonos.com/controllers-and-music-services-229131/apps-fiddly-volume-control-6933330), [[eCoustics]](https://www.ecoustics.com/news/sonos-app-update-2026/) | Урок «как не надо»: лаг/прыжки громкости; слайдер‑«тир»: надо попасть в точку, хотят **относительный** жест; случайный mute на малой громкости; убрали **цифры громкости**; убрали функции без объяснений; «всё в лицо сразу»; в 2026 вернули нижние вкладки и переделали громкость, выпускали как opt‑in бету | — |

### 3.2. Жалобы на существующие VLC‑пульты (Android/iOS)

Источники: App Store‑страницы (Play Store не отдаёт содержимое без JS; Android‑версии тех же приложений — те же авторы и функции), VideoLAN‑форум, агрегатор отзывов.

- **Соединение отваливается** «после коротких периодов использования, нужен перезапуск приложения» — главная жалоба на VLC Remote (агрегат 84 из 563 отзывов) ✅ [[marlvel.ai]](https://marlvel.ai/apps/vlc-remote/reviews). Урок: авто‑реконнект незаметно для пользователя, индикатор состояния, никогда не терять экран.
- **Пароль HTTP‑интерфейса.** С VLC 2.1 интерфейс без пароля не отвечает; клиенты запрашивают *имя пользователя*, которое должно быть пустым — люди не понимают ✅ [[форум 2013]](https://forum.videolan.org/viewtopic.php?p=392423), [[форум 2015]](https://forum.videolan.org/viewtopic.php?p=425210). Оба коммерческих пульта решили это **ПК‑помощником**: «Setup Wizard» + «VMR Connect» у Adarsh Urs; «free setup helper configures VLC» у Hobbyist ✅ [[App Store VMR]](https://apps.apple.com/us/app/vlc-mobile-remote/id1140931401), [[App Store Hobbyist]](https://apps.apple.com/us/app/id297244048). Наш компаньон обязан делать это сам (включить `http` в `vlcrc`, задать пароль, показать QR).
- **Реклама и дроблёные покупки.** У VMR 10 разных IAP ($0.99–$7.99), «incredibly intrusive advertising» после обновления; у VLC Remote — «подписка на уже купленное» ✅. Для нас не актуально (личное приложение), но объясняет, почему люди ищут open‑source.
- **Перемотка слишком чувствительна:** «‑10s иногда отбрасывает в начало» ✅ [[Hobbyist reviews]](https://apps.apple.com/us/app/id297244048) — у VLC `seek&val=-10` относительный, но если команда ушла дважды или состояние рассинхронизировалось, получаем прыжок. Урок: debounce, оптимистичное обновление с подтверждением по `status.json`, не слать `seek` с абсолютной позицией из устаревшего состояния.
- **Несколько ПК в сети:** «видит только один сервер, настройки перезаписываются» ✅ — список хостов с именами, как в Remote for VLC (nickname, test connection) ✅ [[android-vlc-remote]](https://github.com/samicemalone/android-vlc-remote).
- **Плейлист возвращается к первому элементу, пропадает громкость после загрузки новых файлов** ✅ — следствие полного перезапроса состояния; держать стабильные `id` из `playlist.json`.
- Что уже умеет open‑source Remote for VLC (GPL‑3, Java, заброшен): уведомление с контролами, кнопки crop/subtitle/fullscreen/audio/aspect в action bar, браузер с «библиотекой» из нескольких папок, главы, **пресеты задержки аудио/субтитров**, парсинг имён файлов на фильмы/серии ✅ — хороший чек‑лист паритета.

### 3.3. Извлечённые паттерны и наши решения

**Поверхность для перемотки и громкости.**
- *Слайдер* (Plex/Kore/KDE) — точен для абсолютной позиции, плох для «на 10 секунд назад, не глядя». *Свайп‑тачпад* (Apple TV/Google TV) — хорош для навигации по меню, но для перемотки без визуальной обратной связи на телефоне непредсказуем. *Кнопки‑скачки* — единственное, что работает в темноте не глядя.
- Решение: **три слоя.** (1) Кнопки ⟲10 / ⟳30 (настраиваемые: 10/30/60/90 с; в сериалах 90 с = «пропустить заставку»), автоповтор при удержании с ускорением; (2) scrub‑бар с главами (насечки по `information.chapters`, хаптика `SegmentTick` при пересечении главы), при перетаскивании — крупная подпись времени над пальцем; «зоны скорости» как в iOS: чем дальше палец ушёл вниз от бара, тем медленнее масштаб (1×, ½×, ¼×) — не проверено на Compose, реализуется через `pointerInput`; (3) опциональная жестовая поверхность поверх постера: горизонтальный свайп = ±10 с за каждые N dp, вертикальный = громкость, тап = play/pause, двойной тап слева/справа = ±10 с (конвенция мобильного VLC/YouTube). Обязательно: игнорировать случайный тап первые 300 мс после появления экрана (Apple HIG).
- Громкость: **относительные** жесты и ±кнопки, слайдер с **цифрой** (Sonos‑урок), защита от mute (mute — отдельная кнопка, слайдер в 0 не уводит). Шкала VLC 0..512 показывать как 0–200 % (256 = 100 %) с мягким ограничителем на 100 %; см. `live-probe.md`.

**Play/Pause и досягаемость.**
- Хубер: 49 % держат телефон одной рукой, ⅔ из них — правой; «зона большого пальца» — модель, а не шаблон; пользователи сами подтягивают контент к центру ✅ [[A List Apart]](https://alistapart.com/1-2-phone-grips-jpg/), [[Osmani]](https://addyosmani.com/blog/touch-friendly-design/).
- Решение: транспортный ряд в **нижней трети** экрана, play/pause ≥ 88 dp по центру, соседние 64 dp; верх экрана — информационный (постер, название), вниз не тянется ничего критичного. **Левша‑режим** — зеркалирование ряда быстрых действий и положения слайдера громкости (настройка, не автоопределение). Для планшета/ландшафта — `SupportingPaneScaffold`: слева Now Playing, справа транспорт; учитывать *height class compact* на телефоне в ландшафте — там два ряда кнопок вместо постера.

**Now Playing.** Постер/backdrop (TMDB) с затемнением, название + год, чипы «1080p · AC3 5.1 RU · SRT RU» (тап по чипу = соответствующий sheet), прогресс «00:42:17 / 2:58:25 · осталось 2:16», название главы (с компаньоном или Lua‑страницей — `live-probe.md`). В состоянии `stopped` экран **не пропадает** (Plex): показываем последний фильм и кнопку «Продолжить с 00:42».

**Быстрые переключатели дорожек.** Sheet с радио‑списком; формат строки: `Язык · Описание (кодек, каналы)`; сортировка: предпочитаемые языки пользователя первыми; «Выкл.» наверху для субтитров. Данные — из `information.category` (`Stream N` → ID), текущая — из Lua‑страницы/компаньона либо оптимистично (см. `live-probe.md`).

**Тонкая подстройка задержки.** Кнопки ±50 мс и ±500 мс + «сброс» + отображение текущего значения; кнопка «запомнить для этого файла» (Infuse‑боль) — хранить в Room по пути файла; применять при следующем запуске того же файла. Для аудио — то же.

**Таймер сна.** 15/30/45/60 мин и «в конце фильма/серии»; по истечении — `pl_pause` (не `stop`, чтобы сохранить позицию) и, с компаньоном, погасить монитор/усыпить ПК.

**Главы.** Список глав с названиями (при наличии) и временем; текущая подсвечена; тап = `chapter&val=N`.

**Скорость.** Пресеты 0.75/1/1.25/1.5/2 + «удержание ▶▶ = временная 2×» (Infuse), помня, что `rate` квантуется (сравнивать с допуском, `live-probe.md`).

**Аспект/зум/кроп.** Список из README (`16:9`, `4:3`, `221:100`, `235:100`, `239:100`, …) ✅ [[README]](https://github.com/videolan/vlc/tree/master/share/lua/http/requests); «по умолчанию» первым; кроп — отдельно; предпросмотр невозможен — просто подписи «Убрать чёрные полосы (2.35:1 → 16:9)».

**Продолжить просмотр.** VLC не хранит позиции per‑file по HTTP; делаем сами: каждые 10 с писать `path → time` в Room, при `stopped` — финальную; карточка «Продолжить» наверху библиотеки и на пульте; запуск = `in_play&input=<uri>` + `seek&val=<sec>` после смены `currentplid`. Если < 5 % или > 95 % — считать «не начат» / «просмотрен».

**Библиотека.** Два вида: *папки* (`browse.json?uri=file:///D:/shared/`, сортировка «папки сначала», хлебные крошки) и *постеры* (сетка 3×, 2:3, год, полоса прогресса). Сериалы = папка → список серий с SxxEyy. Fast‑scroll с буквой; поиск по названию (и по имени файла); фильтр «не просмотрено». Файлы без метаданных — иконка + имя файла, не ломать сетку.

**Тёмная тема.** Material: фон `#121212`, не чистый чёрный; текст не чисто белый (87 % белого) ✅ [[Android Devs]](https://medium.com/androiddevelopers/dark-theme-with-mdc-4c6fc357d956). Для OLED в тёмной комнате — **опция «True black»** (фон `#000000`, поверхности `#0A0A0A`) и приглушённые акценты; заявление о «смазывании» чёрного — из вторичных источников, не проверено. Яркость кнопок снижать по датчику освещённости — не проверено, опционально. Dynamic color (Material You) — да, но с проверкой контраста на тёмном.

**Хаптика.** `Confirm` на play/pause, `SegmentTick` на каждые 10 с удержания и на границе главы, `Reject` при ошибке команды (ПК не ответил), `GestureEnd` при отпускании scrub‑бара. Всё — выключаемо.

**Состояние соединения** — всегда видно одной строкой сверху (имя ПК · пинг · значок), тап → экран «Подключение». Ошибка команды → снэкбар с действием «Повторить», не модалка.

**Антипаттерны (Sonos):** не прятать цифры, не убирать функции молча, не грузить всё сразу, не ломать то, что работало (Google TV long‑press).

---

## 4. Метаданные, обложки, субтитры

### 4.1. Парсинг имён файлов

Эталон — Python‑библиотека **guessit** (title, year, season/episode, screen_size `720p/1080p/2160p`, source `Blu-ray/Web/HDTV/DVD`, video_codec `H.264/H.265`, audio_codec `AAC/Dolby Digital/DTS`, audio_channels, release_group, container, edition `Director's Cut/Extended`, other `Proper/Remux/HDR10/Dolby Vision`, language, subtitle_language, country) ✅ [[guessit properties]](https://guessit.readthedocs.io/en/latest/properties.html). JVM‑порта guessit нет (есть TypeScript/WASM `guessit-js` от OpenSubtitles) ✅.

Варианты для Kotlin:
1. **PTN‑подход** (parse‑torrent‑name): цепочка regex’ов, каждое совпадение *вырезается* из строки, остаток после нормализации разделителей — название ✅ [[parse-torrent-title]](https://pypi.org/project/parse-torrent-title/2.4/). Go‑порт `jhin` извлекает 46 полей и байт‑в‑байт совпадает с PTT на golden‑корпусе ✅ [[pkg.go.dev]](https://pkg.go.dev/github.com/dreulavelle/jhin) — удобный источник тестовых кейсов.
2. **Java‑библиотека** `io.github.mdaubie:torrent-name-parser` (MIT, Java 17, Maven Central) ✅ [[Sonatype]](https://central.sonatype.com/artifact/io.github.mdaubie/torrent-name-parser) — полнота полей не проверена.
3. **Emby.Naming (C#, Jellyfin)** — разработчик знает C#. Ключевое правило «очистки даты»: `(.+[^_\,\.\(\)\[\]\-])[_\,\.\(\)\[\]\-](19[0-9]{2}|20[0-9]{2})(?![0-9]+|\W[0-9]{2}\W[0-9]{2})([ _\,\.\(\)\[\]\-][^0-9]|$)` + ленивые варианты + `VideoVersionExpressions` (всё до последнего ` - `) ✅ [[Emby.Naming]](https://dev.emby.media/reference/pluginapi/Emby.Naming.Video.VideoResolver.html), [[diff]](https://gitea.hptrow.me/pt/jellyfin/compare/53d432852c9380e57f36adfebd4432c743028127...84f0c5fbe1a5f0352bba0396abff7ae5e3123ad6). Известная ловушка: CleanStrings‑regex с «se» обрезал названия ✅ [[Emby forum]](https://emby.media/community/topic/20947-se-keyword-for-home-videos/).

**Где парсить.** Рекомендация — **в ПК‑компаньоне (C#)**: там есть `D:\shared` целиком, можно хэшировать файлы, кэшировать TMDB‑ответы для всех телефонов, генерировать миниатюры ffmpeg и отдавать телефону готовый `library.json` (`path, title, year, s/e, quality, tmdbId, poster, backdrop, duration`). На телефоне — лёгкий Kotlin‑парсер для режима «без компаньона» (только `browse.json`) — ~60 строк regex по PTN‑схеме:

```kotlin
// :core:naming — набросок, порядок важен (сначала вырезаем «шум», потом ищем год/SxxEyy)
object ReleaseNameParser {
    private val sxe   = Regex("""(?i)\bS(\d{1,2})[ ._-]?E(\d{1,3})\b|\b(\d{1,2})x(\d{2,3})\b""")
    private val year  = Regex("""(?<![\d])(19\d{2}|20\d{2})(?![\d])""")
    private val res   = Regex("""(?i)\b(2160p|1080p|720p|480p|4k|uhd)\b""")
    private val src   = Regex("""(?i)\b(blu-?ray|bdrip|bdremux|remux|web-?dl|webrip|hdtv|dvdrip|hdrip)\b""")
    private val vcod  = Regex("""(?i)\b(x264|x265|h\.?264|h\.?265|hevc|av1|xvid)\b""")
    private val acod  = Regex("""(?i)\b(dts(-hd)?(\.?ma)?|truehd|atmos|ac3|dd(p|\+)?(5\.1|7\.1)?|aac|flac|eac3)\b""")
    private val other = Regex("""(?i)\b(proper|repack|hdr10\+?|dv|dolby\.?vision|10bit|extended|director'?s\.?cut|unrated|remastered)\b""")
    private val group = Regex("""-([A-Za-z0-9]+)$""")

    fun parse(fileName: String): Parsed {
        var s = fileName.substringBeforeLast('.').replace('_', ' ')
        val se = sxe.find(s)
        val yr = year.findAll(s).lastOrNull()            // последний год: «2001 Space Odyssey (1968)»
        val tags = listOf(res, src, vcod, acod, other).mapNotNull { it.find(s)?.value }
        // Название = всё до первого «якоря» (SxxEyy, год или первый тег качества)
        val cut = listOfNotNull(se?.range?.first, yr?.range?.first,
            tags.firstOrNull()?.let { s.indexOf(it) }).minOrNull() ?: s.length
        var title = s.substring(0, cut).replace(Regex("""[.\[\](){}]"""), " ").trim(' ', '-', '.')
        if (title.isBlank()) title = s.substringBefore(" -").trim()   // «Название - 1080p.mkv»
        return Parsed(title, yr?.value?.toInt(), se?.let { seasonOf(it) }, se?.let { episodeOf(it) }, tags)
    }
}
```

Golden‑тесты брать из корпуса PTT/jhin + свои имена из `D:\shared`. Для сериалов папка `Сериал/Season 01/` — сезон из папки, если в имени только `E05`.

### 4.2. TMDB: условия, лимиты, картинки

- **Условия (API Terms of Use):** бесплатно только для **некоммерческого** использования; коммерческое — отдельный договор. Обязательна атрибуция: логотип TMDB *менее заметный*, чем ваш, и текст «This [application] uses TMDB and the TMDB APIs but is not endorsed, certified, or otherwise approved by TMDB». **Кэшировать данные не дольше 6 месяцев.** Использование в AI/ML‑приложениях запрещено. Не использовать TMDB как хостинг картинок для рекламы ✅ [[Terms]](https://www.themoviedb.org/api-terms-of-use).
- **Лимиты:** жёсткого лимита нет с декабря 2019; «где‑то в районе 40 запросов в секунду», уважать `429` ✅ [[rate limiting]](https://developer.themoviedb.org/docs/rate-limiting). Для домашней библиотеки на 500 фильмов — один проход с паузой 100 мс.
- **Аутентификация:** `Authorization: Bearer <API Read Access Token>` (v4‑токен), либо `api_key` query (legacy) ✅ [[search-movie]](https://developer.themoviedb.org/reference/search-movie). Ключ — в компаньоне (не в APK), телефон получает уже URL картинок. Для режима без компаньона — ключ в `local.properties`/BuildConfig, понимая, что он извлекаем.
- **Поиск:** `GET /3/search/movie?query=&year=&primary_release_year=&language=ru-RU&include_adult=false&page=1`; `GET /3/search/tv`; затем `/3/movie/{id}?append_to_response=images&include_image_language=ru,null` для постеров на нужном языке; `/3/configuration` для `base_url` и размеров.
- **Картинки:** `https://image.tmdb.org/t/p/{size}{file_path}`; `poster_sizes: w92 w154 w185 w342 w500 w780 original`; `backdrop_sizes: w300 w780 w1280 original`; `still_sizes: w92 w185 w300 original` ✅ [[image basics]](https://developer.themoviedb.org/docs/image-basics), [[sample config]](https://community.kde.org/Macaw-Movies/TMDB-json-responses) (размеры читать из `/configuration` в рантайме, несуществующий размер → ошибка ✅ [[TMDB talk]](https://themoviedb.org/talk/5a6986550e0a260d6400d6c0)). Для сетки 3× на телефоне — `w342`; для Now Playing — `w780` постер или `w1280` backdrop; для виджета — `w185`.
- **Fallback‑цепочка:** TMDB → `GET /art` (обложка из кэша VLC, `image/jpeg`; `/art?item=<id>` для элемента плейлиста — `live-probe.md`, README) → миниатюра от компаньона (`ffmpeg -ss 00:05:00 -i f.mkv -frames:v 1 -vf scale=480:-1 thumb.jpg` или VLC `--video-filter=scene`) → плейсхолдер с первой буквой названия.

### 4.3. Кэширование на телефоне (Coil 3)

`coil-network-okhttp:3.6.3` на общем `OkHttpClient`; по умолчанию Coil **пишет всё в дисковый кэш, игнорируя `Cache-Control`** — для TMDB (иммутабельные пути) это идеально; `coil-network-cache-control` не нужен ✅ [[Coil network]](https://coil-kt.github.io/coil/network/). Ключ кэша — `file_path` TMDB (стабилен), `/art` — ключ `host + currentplid + filename`. Размер дискового кэша 100–250 МБ. Память: `crossfade`, `placeholder` цветом из палитры предыдущей картинки (не проверено, `Palette` API). Учитывать TMDB‑требование: **6 месяцев** — ставить `maxAge` на записи в Room и инвалидировать.

### 4.4. Субтитры: OpenSubtitles и Subdl — v1 или позже

- **OpenSubtitles REST API v1** (`api.opensubtitles.com/api/v1`): нужен API‑ключ consumer‑приложения, заголовки `Api-Key` и `User-Agent`, логин по JWT, лимит на логин 1/с, 10/мин, 30/ч (третьи источники) ✅ [[glama]](https://glama.ai/mcp/servers/@opensubtitles/mcp.opensubtitles.com/blob/607a9eb0ecd1d2e908642f9ecf5dd4335d997db0/README.md), [[termo]](https://termo.ai/skills/opensubtitles). **Квоты на скачивание расходятся по источникам:** 5/сутки на IP анонимно, 10/сутки для зарегистрированных, 1000 VIP ✅ [[Reddit 2024]](https://redlib.hbubli.cc/r/learnprogramming/comments/1bd0rxx/using_subtitles_api), [[VideoLAN forum]](https://forum.videolan.org/viewtopic.php?p=545241) против «200/сутки free» ✅ [[Mintlify]](https://www.mintlify.com/logscore/plank/integrations/opensubtitles) — официальная страница (`opensubtitles.stoplight.io`, `/consumers`) не отдаётся без JS/логина — **не проверено**. VLsub в 2024 упирался в VIP‑стену ✅. Вывод: API пригоден только с **аккаунтом пользователя** (логин в приложении) и аккуратным UX квоты.
- **Subdl:** `GET https://api.subdl.com/api/v1/subtitles?api_key=&film_name=|tmdb_id=|imdb_id=&season_number=&episode_number=&year=&languages=ru,en`, ответ — ссылки на zip ✅ [[cloudstream commit]](https://gitdab.com/recloudstream/cloudstream/commit/469a71236b6e78018f3f72c83f0b051bdab189c3); лимит ~2000 вызовов/сутки на ключ — из вторичного источника ✅ [[tMM issue]](https://gitlab.com/tinyMediaManager/tinyMediaManager/-/issues/2631), **не проверено**; официальная `subdl.com/api-doc` возвращает 403 для бота.
- **Решение: не в v1.** В v1 — выбор уже имеющихся дорожек + `addsubtitle&val=<uri>` для файлов рядом с видео (VLC сам подхватывает `.srt` с тем же именем). Загрузка субтитров — **в компаньоне (v1.x):** он знает путь, может посчитать OpenSubtitles‑hash файла, положить `.srt` рядом и дёрнуть `addsubtitle`; телефон только показывает список результатов и жмёт «скачать». Так ключи и квоты живут на ПК, а не в APK.

---

## 5. Информационная архитектура

### 5.1. Состояния соединения

```
                 ┌──────────────┐  найти/выбрать ПК   ┌──────────────┐
   старт ───────▶│ Disconnected │────────────────────▶│  Connecting  │
                 └──────▲───────┘                     └──────┬───────┘
                        │ «Отключиться» / Wi‑Fi пропал       │ 401 → PasswordRequired
                        │                                    │ timeout/refused → Error(reason)
                        │                                    ▼ 200 status.json
                 ┌──────┴────────────────────────────────────────────────┐
                 │ Connected                                             │
                 │  ├─ VlcNotRunning   (компаньон есть, VLC нет → «Запустить VLC»)│
                 │  ├─ Idle(stopped)   (последний фильм + «Продолжить»)  │
                 │  ├─ Playing / Paused (Now Playing, транспорт)         │
                 │  └─ Degraded       (нет ответа 3 опроса → спиннер в строке состояния, команды в очередь ≤ 5 с)│
                 └───────────────────────────────────────────────────────┘
```

Правила: `Degraded` не выбрасывает с экрана (Plex); три неудачных опроса подряд → `Connecting` с backoff 1‑2‑4‑8 с + jitter, без модалок; при `401` — поле пароля прямо в строке состояния. Команда при `Degraded` ставится в очередь на 5 с, иначе снэкбар «ПК не отвечает · Повторить».

### 5.2. Экраны и навигация

```
NavigationBar (3): [▣ Пульт] [▤ Библиотека] [≡ Плейлист]
   Пульт ── sheet: Субтитры/Аудио · Главы · Скорость · Аспект/Кроп · Таймер сна · ⋯(Ещё: fullscreen, звук.выход*, экран*, VLC quit*)
   Библиотека ── Папка/Сетка → Сведения о фильме (постер, описание, «Воспроизвести», «С начала», «В очередь») → sheet
   Плейлист ── reorder/удалить/очистить, мини‑плеер
Отдельные экраны: Подключение (список ПК, ручной ввод, Bluetooth), Настройки, Онбординг (3 шага)
* — только с компаньоном
```

Навигация — Navigation 3: back‑stack `[Remote]` по умолчанию; вкладки — три корневых ключа с сохранением состояния; sheet’ы — `ModalBottomSheet` без записи в back‑stack (закрываются predictive‑back). Deep links: `vlcremote://play?uri=…` (из share‑sheet: «Отправить в VLC на ТВ» для ссылок YouTube — паттерн Kore; VLC умеет `in_play&input=<url>`).

### 5.3. Онбординг: «Найти ПК → Связать → Проверить»

1. **Найти.** Автопоиск mDNS (`_vlc-remote._tcp` компаньона и/или `_http._tcp` с TXT `vlc`); показать найденные ПК с именем и IP; «Ввести адрес вручную». На Android 17 перед этим — запрос `ACCESS_LOCAL_NETWORK` с объяснением.
2. **Связать.** Если компаньон: он показывает на ТВ 6‑значный PIN/QR (QR содержит `host, port, token`), телефон сканирует/вводит → токен сохраняется в `EncryptedSharedPreferences`/DataStore. Без компаньона: поле «Пароль HTTP‑интерфейса VLC» + подсказка со скриншотом, где его включить (Preferences → All → Main interfaces → Lua → HTTP), и кнопка «Скопировать команду запуска VLC с `--extraintf http --http-password …`».
3. **Проверить.** Кнопка «Поставить на паузу на ТВ» → ожидаем изменение `state` → «Работает ✓». Затем предложить: добавить плитку (`requestAddTileService`), виджет, разрешить «клавиши громкости управляют ПК».

### 5.4. Настройки

Соединение (ПК, порт, пароль, компаньон, Bluetooth‑резерв, авто‑подключение при входе в Wi‑Fi), Управление (клавиши громкости: ПК/телефон; шаги перемотки 10/30/60/90; жестовая поверхность вкл/выкл; левша; хаптика), Библиотека (корневые папки, язык метаданных, «скрывать просмотренные», переиндексировать), Вид (тема: система/тёмная/true‑black; dynamic color; держать экран включённым на Пульте), Фон (FGS при воспроизведении; «не ограничивать батарею»), О программе (атрибуция TMDB с логотипом — обязательна по условиям).

### 5.5. Тон сообщений об ошибках

Коротко, по‑человечески, с действием: «ПК не отвечает · Повторить», «VLC не запущен на HTPC‑Lounge · Запустить», «Неверный пароль HTTP‑интерфейса · Изменить», «Нет Wi‑Fi · Проверить сеть / Bluetooth». Без кодов и стек‑трейсов в основном тексте (они — в «Подробнее»). Ошибка команды → снэкбар, не диалог; потеря связи → строка состояния, не модалка; успех — хаптика, не тост.

### 5.6. Низкодетальные макеты (ASCII)

**Пульт / Now Playing (портрет)**

```
┌──────────────────────────────────┐
│ ● HTPC‑Lounge · 24 мс         ⋮ │ ← строка состояния; тап → Подключение
│ ┌──────────────────────────────┐ │
│ │        backdrop (TMDB)       │ │ ← затемнён; жестовая поверхность (опц.)
│ │      затемнение 60 %         │ │
│ └──────────────────────────────┘ │
│ Властелин колец: Братство… 2001  │
│ [1080p] [AC3 5.1 RU] [SRT RU]    │ ← чипы → sheet дорожек
│ 00:42:17 ──────●───────── 2:58:25│ ← scrub‑бар, насечки глав
│  Гл. 12 «Мория»       −2:16:08   │
│                                  │
│  ⟲10     ◀◀      ▶❚❚     ▶▶  ⟳30 │ ← 64 / 64 / 88 / 64 / 64 dp
│                                  │
│ 🔈 −  ──────────●────── +  52 %  │ ← громкость ПК, цифра обязательна
│                                  │
│ [CC Субтитры] [♪ Аудио] [⏱ 1.0×] │ ← быстрые действия → sheet’ы
│ [▭ 16:9]  [⏾ Таймер]  [⋯ Ещё]    │
│  ▣ Пульт   ▤ Библиотека ≡ Плейлист│
└──────────────────────────────────┘
```

Состояние `Idle/stopped`: тот же экран, постер последнего фильма, вместо транспорта — «▶ Продолжить с 00:42» и «С начала». Состояние `Disconnected`: экран остаётся, поверх контента — карточка «Нет связи с HTPC‑Lounge · Подключить».

**Пульт (ландшафт, телефон; height = compact)**

```
┌───────────────────────────────────────────────────────────────┐
│ ● HTPC‑Lounge        Властелин колец: Братство… 2001        ⋮ │
│ ┌──────────┐  00:42:17 ─────●──────────── 2:58:25  Гл. 12     │
│ │  poster  │   ⟲10    ◀◀    ▶❚❚    ▶▶   ⟳30                   │
│ │          │  🔈 − ────────●──────── + 52 %  [CC] [♪] [⏱] [▭]  │
│ └──────────┘                                                  │
└───────────────────────────────────────────────────────────────┘
```

**Библиотека (сетка + папки)**

```
┌──────────────────────────────────┐
│ ← D:\shared › Фильмы      🔍  ⊞☰ │ ← крошки; переключатель сетка/список
│ ┌────────────────────────────┐   │
│ │ ▶ Продолжить: Дюна (2021)  │   │ ← resume‑карточка, 37 %
│ │   00:58 из 2:35       ▬▬▬░ │   │
│ └────────────────────────────┘   │
│ ┌──────┐ ┌──────┐ ┌──────┐       │
│ │poster│ │poster│ │poster│       │ ← 2:3, w342
│ │      │ │      │ │ ▬▬░░ │       │ ← полоса прогресса
│ └──────┘ └──────┘ └──────┘       │
│  Дюна     Матрица   Интерст…     │
│  2021     1999      2014         │
│ ┌──────┐ ┌──────┐ ┌──────┐       │
│ │ 📁   │ │poster│ │  ▶   │       │ ← папка «Сериалы»; файл без метаданных
│ │ 12   │ │      │ │ .mp4 │       │
│ └──────┘ └──────┘ └──────┘       │
│  Сериалы  Оппенг…   IMG_2041.mp4 │
│                              [Д] │ ← fast‑scroll с буквой
│  ▣ Пульт   ▤ Библиотека ≡ Плейлист│
└──────────────────────────────────┘
```

Long‑press по карточке → sheet: ▶ Воспроизвести · ▶ С начала · + В очередь · ⓘ Сведения · 👁 Отметить просмотренным.

**Плейлист**

```
┌──────────────────────────────────┐
│ Плейлист · 3            🔀 🔁 🗑 │
│ ▶ 1  Дюна (2021)            2:35 │ ← текущий: акцентная полоса слева
│ ≡ 2  Дюна: Часть вторая     2:46 │ ← ≡ drag‑handle; swipe ← удалить
│ ≡ 3  Making‑of.mkv          0:48 │
│                                  │
│          (пусто → «Очередь пуста.│
│           Выберите фильм в       │
│           Библиотеке»)           │
│                                  │
│ ┌────────────────────────────┐   │
│ │ ▶❚❚ Дюна · 00:42 / 2:35  ⟳30│   │ ← мини‑плеер; тап → Пульт
│ └────────────────────────────┘   │
│  ▣ Пульт   ▤ Библиотека ≡ Плейлист│
└──────────────────────────────────┘
```

**Sheet «Субтитры и аудио»**

```
┌──────────────────────────────────┐
│ ━━━━                              │
│ [ Субтитры ]   [ Аудио ]          │ ← две вкладки (или два раздела)
│ ─────────────────────────────────│
│ ○ Выкл.                           │
│ ● Русский · Мосфильм (SRT)        │ ← язык первым, кодек серым
│ ○ Русский · форсированные (PGS)   │
│ ○ English (PGS)                   │
│ ○ Ещё 3…                          │
│ ─────────────────────────────────│
│ Задержка субтитров       −0.50 с  │
│ [ −500 ] [ −50 ]  [ +50 ] [ +500 ]│ ← мс; хаптика SegmentTick
│ [ Сброс ]   [ Запомнить для файла]│
│ ─────────────────────────────────│
│ + Добавить файл .srt рядом с видео│
│ + Найти субтитры онлайн   (позже) │
└──────────────────────────────────┘
   Вкладка «Аудио»: ● Dub Мосфильм · AC3 5.1 · RU / ○ Original · DTS‑HD · EN / …
   Задержка аудио [−50][+50]; «Вывод звука: HDMI (TV) ▾» — только с компаньоном (RC adev)
```

**Подключение / сопряжение**

```
┌──────────────────────────────────┐
│ ← Подключение                    │
│ В сети Wi‑Fi «Home»:             │
│ ┌────────────────────────────┐   │
│ │ 🖥 HTPC‑Lounge 192.168.1.20│ ▶ │ ← найдено по mDNS
│ │    VLC 3.0.23 · компаньон ✓│   │
│ └────────────────────────────┘   │
│ ┌────────────────────────────┐   │
│ │ 🖥 DESKTOP‑7F  192.168.1.31│ ▶ │
│ │    VLC не запущен          │   │
│ └────────────────────────────┘   │
│   ⟳ Ищу…                          │
│ ─────────────────────────────────│
│ Добавить вручную                 │
│  Адрес [192.168.1.20] Порт [8080]│
│  Пароль HTTP [••••••] [Проверить]│
│ ─────────────────────────────────│
│ Bluetooth (резерв)        Связать│
│  Сопряжённые: HTPC‑Lounge        │
│ ─────────────────────────────────│
│ ① Найти  ② Пароль/PIN  ③ Тест ✓  │ ← прогресс онбординга
└──────────────────────────────────┘
```

---

## 6. Тестирование и инструменты

### 6.1. Compose‑тесты

- `createComposeRule()` / `createAndroidComposeRule<MainActivity>()`, семантические матчеры `onNodeWithText/Tag`, `useUnmergedTree` для составных кнопок; `ui-test-manifest` в debug ✅ [[docs]](https://developer.android.com/develop/ui/compose/testing/common-patterns). Тестировать: состояние кнопок при `Disconnected/Degraded`, формат времени, чипы дорожек, sheet задержки (±50 → команда `subdelay&val=…` в fake‑репозиторий).
- **JVM‑уровень:** Robolectric + **Roborazzi** для скриншотов Compose в тёмной/true‑black теме и в ландшафте; Paparazzi — альтернатива без Robolectric ✅ [[resumegeni guide]](https://resumegeni.com/blog/android-engineer/testing-and-ci) (вторичный источник).
- **Compose Preview Screenshot Testing** (`@PreviewTest`, HTML‑отчёт диффов): AGP ≥ 9.0, Kotlin ≥ 2.2.10, плагин в alpha ✅ [[docs]](https://developer.android.com/studio/preview/compose-screenshot-testing) — удобно для «золотых» макетов из §5.6.
- Логика: `:core:protocol` и `:core:naming` — чистые JUnit/kotlin‑test без Android; `MockEngine` Ktor для парсера `status.json` (включая `fullscreen` как `true/false/0`, пустой `equalizer: []`, пропавшие поля в `stopped` — см. `live-probe.md`).

### 6.2. Fake‑VLC для разработки

Цель — поднимать локально за секунду и воспроизводить **реальные причуды VLC 3.0.23**, а не идеализированный API. Вариант на Ktor‑server (тот же стек, можно встроить в debug‑сборку как «демо‑режим»):

```kotlin
// tools/fake-vlc/src/main/kotlin/Main.kt — набросок
fun main() = embeddedServer(CIO, port = 8080) {
    install(Authentication) { basic("vlc") { realm = "VLC stream"
        validate { if (it.name == "" && it.password == "probe") UserIdPrincipal("vlc") else null } } }
    val state = FakeVlcState()                       // volume 0..512, time/length, playlist, tracks "Stream N"
    routing { authenticate("vlc") {
        get("/requests/status.json") {
            call.parameters["command"]?.let { state.apply(it, call.parameters) }   // pl_pause, seek, volume, rate, subdelay…
            call.respondText(state.statusJson(), ContentType.Text.Plain)           // да, text/plain — как у VLC
        }
        get("/requests/playlist.json") { call.respondText(state.playlistJson(), ContentType.Text.Plain) }
        get("/requests/browse.json")   { call.respondText(state.browseJson(call.parameters["uri"]!!), ContentType.Text.Plain) }
        get("/art")                    { call.respondFile(File("tools/fake-vlc/art.jpg")) }
    } }
}.start(wait = true)
```

Что должен эмулировать `FakeVlcState` (из `live-probe.md`): `volume&val=+10` → 266; `seek&val=+30`/`-10`/`1000` (проценты — не работают, как в жизни); `rate` возвращать с «квантованием» `1.5015…`; `fullscreen` — boolean при игре, `0` в `stopped`; в `stopped` убирать `information/stats`, `currentplid=-1`; `audio_track/subtitle_track&val=<StreamN>`; `pl_pause` из `stopped` запускает последний элемент; неизвестная команда → `200` без ошибки; первые 500 мс после старта — пустой ответ (для теста ретрая); `401` с `WWW-Authenticate: Basic realm="VLC stream"`. Сэмплы ответов — `samples/status-playing.json`, `samples/status-stopped.json`, `samples/playlist.json`. Параметризовать задержку ответа (0/50/500 мс) и «потерю» каждого N‑го запроса для теста `Degraded`. Альтернатива на Python (`http.server`, ~80 строк) — если нужно запускать на ПК без JDK; но Ktor‑вариант переиспользует наши же DTO.

Для компаньона — второй fake: WebSocket‑эхо с push‑событиями (`{"type":"status","state":"playing",...}`), чтобы отлаживать реактивный UI без ПК.

### 6.3. Сеть эмулятора

- Хост = `10.0.2.2`; `localhost` внутри эмулятора — сам эмулятор ✅ [[emulator]](https://developer.android.com/studio/run/emulator-networking-address). Fake‑VLC на хосте `:8080` → в приложении `http://10.0.2.2:8080`. Alternatively `adb reverse tcp:8080 tcp:8080` → `127.0.0.1:8080` (не проверено в этом контексте, стандартная команда).
- **Cleartext HTTP** к VLC (он не умеет TLS) → `network_security_config` с `cleartextTrafficPermitted="true"` для локальных диапазонов, не глобально.
- mDNS в эмуляторе не работает (изоляция) — в debug‑сборке «демо‑ПК» с фиксированным адресом.
- Фаервол Windows может блокировать входящие к Ktor‑fake от эмулятора — разрешить порт при первом запуске.

### 6.4. Реальное устройство: Bluetooth, громкость, фон

Эмулятор не умеет Bluetooth (`BluetoothAdapter` null). Чек‑лист на реальном телефоне (минимум два: Pixel/стоковый Android 15–17 и Samsung/OneUI):
1. RFCOMM: подключение к SPP‑серверу компаньона, обрыв (выключить BT на ПК) → `read() == -1`/`IOException`, реконнект.
2. Громкость в foreground: `dispatchKeyEvent`, автоповтор, переключение «ПК/телефон».
3. Громкость с погашенным экраном: сразу после выключения экрана; через 5 мин; через 40 мин (Doze); с `force-idle`; после сворачивания другого медиа‑приложения (кто «верхняя» сессия).
4. Уведомление MediaSession на lock‑screen: кнопки, прогресс, обложка; Bluetooth‑гарнитура play/pause.
5. FGS `connectedDevice` на Android 14/15/16/17: старт из UI, остановка по таймауту, поведение при убийстве процесса.
6. Android 17 с targetSdk 37 на тестовой сборке: диалог `ACCESS_LOCAL_NETWORK`, mDNS, HTTP к 192.168.x.x; `am compat enable RESTRICT_LOCAL_NETWORK` на Android 16.
7. Glance‑виджет на Android 17: лимит `RemoteViews` с постером.
8. Ландшафт на телефоне и планшете (sw600dp с игнорированием `screenOrientation`).

### 6.5. CI

GitHub Actions: `./gradlew :core:*:test :app:testDebugUnitTest :app:verifyRoborazziDebug`; screenshot‑диффы как артефакты; `lint`; сборка APK с R8 и отчёт размера (`apkanalyzer apk file-size`). Инструментальные тесты — вручную на устройстве (Bluetooth/громкость не автоматизируются).

---

## 7. Рекомендации

### 7.1. Решение по стеку
Остаться на **Kotlin + Jetpack Compose**, вынести протокол и парсер имён в чистые Kotlin‑модули (CMP‑ready), **не** переходить на Flutter/RN/MAUI/CMP‑UI. targetSdk 36 сейчас, подготовить ветку с 37 для проверки `ACCESS_LOCAL_NETWORK` и аудио‑ограничений.

### 7.2. Библиотеки и версии (октябрь 2026)
`kotlin 2.4.21` · `agp 9.4.0` · `compose-bom 2026.09.00` (material3 1.4.0) · `material3-adaptive 1.3.0` · `navigation3 1.2.0` · `media3 1.11.1` (session) · `ktor 3.6.0` (client-okhttp, websockets, content-negotiation, serialization-kotlinx-json) · `okhttp 5.5.0` · `kotlinx-serialization-json 1.11.0` · `kotlinx-coroutines 1.11.0` · `ksp 2.3.12` · `koin 4.2.2` (koin-androidx-compose, koin-compose-navigation3) · `room 2.8.5` · `datastore 1.2.1` · `coil 3.6.3` (coil-compose, coil-network-okhttp) · `glance 1.2.0` (glance-appwidget, glance-material3) · тесты: `roborazzi` (версия — проверить), Robolectric, `ui-test-junit4`, `ui-test-manifest`. Material 3 Expressive — после 1.5.0 stable.

### 7.3. Принципы UX
1. Пульт работает **не глядя**: крупный play/pause внизу, ±10/±30 кнопки с автоповтором, хаптика на каждом «тике».
2. Громкость — относительные жесты + цифра; mute отдельно; клавиши телефона управляют ПК (настраиваемо), в т.ч. с погашенным экраном, где платформа позволяет.
3. Соединение **никогда не выбрасывает** с экрана: строка состояния, тихий реконнект, очередь команд на 5 с, снэкбар вместо диалога.
4. Now Playing остаётся и после `stop` («Продолжить с 00:42»).
5. Дорожки и задержки — один sheet, язык первым, ±50/±500 мс, «запомнить для файла».
6. Библиотека с постерами и «Продолжить просмотр»; файлы без метаданных не ломают сетку; fast‑scroll и поиск.
7. Тёмная тема по умолчанию, опция true‑black; dynamic color с проверкой контраста.
8. Ландшафт и планшет — отдельный макет (платформа всё равно заставит).
9. Онбординг в 3 шага, компаньон сам включает HTTP‑интерфейс и показывает PIN/QR; без компаньона — честная инструкция с паролем.
10. Ничего не убирать молча и не прятать цифры (Sonos); тестировать long‑press и автоповтор (Google TV).
11. Атрибуция TMDB — в «О программе» и на экране сведений о фильме; кэш метаданных ≤ 6 мес.

### 7.4. Набор экранов MVP
1. **Пульт / Now Playing** (портрет + ландшафт) с транспортом, громкостью, scrub‑баром, чипами дорожек.
2. **Sheet «Субтитры и аудио»** (выбор + задержки).
3. **Sheet «Ещё»**: главы, скорость, аспект/кроп, fullscreen, таймер сна.
4. **Библиотека**: папки (`browse.json`) + сетка с постерами (TMDB через компаньон или на телефоне) + «Продолжить».
5. **Плейлист** с мини‑плеером.
6. **Подключение** (mDNS + ручной ввод + пароль) и **онбординг** в 3 шага.
7. **Настройки** (минимум: клавиши громкости, шаги перемотки, тема, FGS).
8. **Фон:** `MediaSessionService` + FGS `connectedDevice`, уведомление с контролами; **QS‑плитка** play/pause.
Отложить: Glance‑виджет (v1.1), Bluetooth‑резерв (v1.1, после компаньона), Wear OS tile (v2), загрузка субтитров через компаньон (v1.x), жестовая поверхность (эксперимент за флагом).

---

## 8. Источники

Версии: [Kotlin releases](https://github.com/JetBrains/kotlin/releases) · [AGP release notes](https://developer.android.com/build/releases/gradle-plugin) · [Compose BOM mapping](https://developer.android.com/develop/ui/compose/bom/bom-mapping) · [Compose Material 3 releases](https://developer.android.com/jetpack/androidx/releases/compose-material3) · [material3‑adaptive](https://developer.android.com/jetpack/androidx/releases/compose-material3-adaptive) · [Navigation 3](https://developer.android.com/jetpack/androidx/releases/navigation3) · [Media3](https://developer.android.com/jetpack/androidx/releases/media3) · [Ktor releases](https://github.com/ktorio/ktor/releases) · [OkHttp CHANGELOG](https://github.com/square/okhttp/blob/master/CHANGELOG.md) · [kotlinx.serialization](https://github.com/Kotlin/kotlinx.serialization/releases) · [kotlinx.coroutines](https://github.com/Kotlin/kotlinx.coroutines/releases) · [KSP releases](https://github.com/google/ksp/releases), [KSP PR #3107](https://github.com/google/ksp/pull/3107) · [Dagger releases](https://github.com/google/dagger/releases) · [Koin releases](https://github.com/InsertKoinIO/koin/releases) · [Room](https://developer.android.com/jetpack/androidx/releases/room), [Room 3](https://developer.android.com/jetpack/androidx/releases/room3) · [DataStore](https://developer.android.com/jetpack/androidx/releases/datastore) · [Coil releases](https://github.com/coil-kt/coil/releases), [Coil network docs](https://coil-kt.github.io/coil/network/) · [Glance](https://developer.android.com/jetpack/androidx/releases/glance) · [Wear Compose](https://developer.android.com/jetpack/androidx/releases/wear-compose) · [Compose Multiplatform releases](https://github.com/JetBrains/compose-multiplatform/releases), [CMP 1.8.0 blog](https://blog.jetbrains.com/kotlin/2025/05/compose-multiplatform-1-8-0-released-compose-multiplatform-for-ios-is-stable-and-production-ready/) · [MaterialExpressiveTheme](https://composables.com/material3/materialexpressivetheme) · [Flutter what's new](https://docs.flutter.dev/release/whats-new.html) · [Expo New Architecture](https://docs.expo.dev/guides/new-architecture.md) · [MAUI support policy](https://dotnet.microsoft.com/ru-ru/platform/support/policy/maui) · [Plugin.BLE](https://www.nuget.org/packages/plugin.ble) · [flutter_classic_bluetooth](https://pub.dev/packages/flutter_classic_bluetooth/versions/0.1.4) · [flutter_bluetooth_serial](https://github.com/edufolly/flutter_bluetooth_serial) · [react-native-bluetooth-classic](https://www.jsdelivr.com/package/npm/react-native-bluetooth-classic) · [APK size comparison (Medium, 2023)](https://medium.com/@jacobras/android-ios-native-vs-flutter-vs-compose-multiplatform-7ef3d5ec2a56).

Платформа: [FGS types](https://developer.android.com/develop/background-work/services/fgs/service-types) · [Android 14 FGS types required](https://developer.android.com/about/versions/14/changes/fgs-types-required) · [Android 15 behavior changes](https://developer.android.com/about/versions/15/behavior-changes-15) · [Android 16 all apps](https://developer.android.com/about/versions/16/behavior-changes-all), [Android 16 targeting](https://developer.android.com/about/versions/16/behavior-changes-16) · [Android 17 all apps](https://developer.android.com/about/versions/17/behavior-changes-all), [Android 17 targeting](https://developer.android.com/about/versions/17/behavior-changes-17) · [Local network permission](https://developer.android.com/privacy-and-security/local-network-permission) · [NsdManager](https://developer.android.com/reference/kotlin/android/net/nsd/NsdManager) · [BluetoothSocket](https://developer.android.com/reference/android/bluetooth/BluetoothSocket) · [VolumeProvider](https://developer.android.com/reference/android/media/VolumeProvider.html) · [MediaSession](https://developer.android.com/reference/android/media/session/MediaSession) · [MediaSessionLegacyStub.java](https://github.com/androidx/media/blob/release/libraries/session/src/main/java/androidx/media3/session/MediaSessionLegacyStub.java) · [Android 12 cast volume regression (esper.io)](https://esper.io/blog/android-12-cast-volume-changes-explained) · [Poweramp screen‑off volume](https://forum.powerampapp.com/topic/21716-volume-keys-control-does-not-work-when-the-screen-is-off/) · [Nest community: lock screen volume](https://www.googlenestcommunity.com/t5/Streaming/Enhancing-Chromecast-Experience-Simplifying-Media-Volume-Control-during/m-p/462235) · [Intercepting key events](https://code.tutsplus.com/android-sdk-intercepting-physical-key-events--mobile-10379t) · [Quick Settings tiles](https://developer.android.com/develop/ui/views/quicksettings-tiles), [API 33 StatusBarManager](https://developer.android.com/sdk/api_diff/33/changes/android.app.StatusBarManager) · [Doze](https://developer.android.com/training/monitoring-device-state/doze-standby) · [HapticFeedbackType](https://developer.android.com/reference/kotlin/androidx/compose/ui/hapticfeedback/HapticFeedbackType) · [Wear tiles codelab](https://developer.android.com/codelabs/wear-tiles) · [RfcommServiceProvider](https://msdn.microsoft.com/library/windows/apps/dn263511), [32feet](https://inthehand.com/components/32feet/) · [Emulator networking](https://developer.android.com/studio/run/emulator-networking-address) · [Adaptive layouts (ProAndroidDev)](https://proandroiddev.com/adaptive-compose-layouts-86b7f1e51338).

UX: [Kore F‑Droid](https://f-droid.org/en/packages/org.xbmc.kore/), [Kore 3.0](https://kodi.tv/article/kore-3-0), [Kore issues](https://github.com/xbmc/Kore/issues), [Kore 3.0 feedback](https://forum.kodi.tv/showthread.php?tid=369711), [Kore README](https://github.com/xbmc/Kore) · [Plex Remote control API](https://github.com/plexinc/plex-media-player/wiki/Remote-control-API), [Plex Android 7.14](https://apkmirror.com/apk/plex-inc/plex/plex-7-14-1-9954-release), [Plex companion thread](https://forums.plex.tv/t/feature-request-supported-server-mediated-api-for-controlling-remote-plex-plexamp-sessions/942315) · [Jellyfin remote (Symfonium)](https://support.symfonium.app/t/allow-symfonium-to-control-jellyfin-on-other-devices/7973) · [Google TV M3 Expressive](https://9to5google.com/2025/08/23/google-tv-app-material-3-expressive/), [Android TV remote review](https://www.techhive.com/article/582309/the-android-tv-remote-app-for-ios-is-as-bare-bones-as-the-android-version.html) · [Apple TV remote](https://www.howtogeek.com/720922/how-to-use-your-iphone-or-ipad-as-an-apple-tv-remote/), [Apple HIG remotes](https://developer-mdn.apple.com/design/human-interface-guidelines/inputs/remotes) · [Infuse subtitles](https://support.firecore.com/hc/en-us/articles/215090907), [Infuse subtitle delay](https://community.firecore.com/t/subtitle-delay/14737.md), [Infuse 2x](https://community.firecore.com/t/temporary-2x-speed-on-apple-tv-like-ios-has/57701.md) · [Unified Remote App Store](https://apps.apple.com/us/app/825534179), [Softpedia](https://mobile.softpedia.com/android/Unified-Remote-Android-Review-2859.html) · [KDE Connect bug 390414](https://bugs.kde.org/show_bug.cgi?id=390414), [D12559](https://phabricator.kde.org/D12559) · [Sonos (CMSWire)](https://www.cmswire.com/customer-experience/sonos-app-redesign-fail-customer-outrage-and-falling-stock-prices), [Sonos forum volume](https://en.community.sonos.com/controllers-and-music-services-229131/apps-fiddly-volume-control-6933330), [Sonos 2026 update](https://www.ecoustics.com/news/sonos-app-update-2026/) · [VLC Mobile Remote (App Store)](https://apps.apple.com/us/app/vlc-mobile-remote/id1140931401), [VLC Remote Pro (App Store)](https://apps.apple.com/us/app/id297244048), [VLC Remote reviews (marlvel)](https://marlvel.ai/apps/vlc-remote/reviews), [VideoLAN forum 2013](https://forum.videolan.org/viewtopic.php?p=392423), [VideoLAN forum 2015](https://forum.videolan.org/viewtopic.php?p=425210), [android-vlc-remote](https://github.com/samicemalone/android-vlc-remote) · [Hoober grips (A List Apart)](https://alistapart.com/1-2-phone-grips-jpg/), [Osmani touch design](https://addyosmani.com/blog/touch-friendly-design/) · [Dark theme with MDC](https://medium.com/androiddevelopers/dark-theme-with-mdc-4c6fc357d956).

Метаданные: [VLC HTTP requests README](https://github.com/videolan/vlc/tree/master/share/lua/http/requests) · [guessit properties](https://guessit.readthedocs.io/en/latest/properties.html) · [parse-torrent-title](https://pypi.org/project/parse-torrent-title/2.4/), [jhin](https://pkg.go.dev/github.com/dreulavelle/jhin), [torrent-name-parser (Java)](https://central.sonatype.com/artifact/io.github.mdaubie/torrent-name-parser) · [Emby.Naming VideoResolver](https://dev.emby.media/reference/pluginapi/Emby.Naming.Video.VideoResolver.html), [Jellyfin naming diff](https://gitea.hptrow.me/pt/jellyfin/compare/53d432852c9380e57f36adfebd4432c743028127...84f0c5fbe1a5f0352bba0396abff7ae5e3123ad6), [Emby «se» bug](https://emby.media/community/topic/20947-se-keyword-for-home-videos/) · [TMDB API terms](https://www.themoviedb.org/api-terms-of-use), [TMDB rate limiting](https://developer.themoviedb.org/docs/rate-limiting), [TMDB image basics](https://developer.themoviedb.org/docs/image-basics), [TMDB search movie](https://developer.themoviedb.org/reference/search-movie), [TMDB config sample](https://community.kde.org/Macaw-Movies/TMDB-json-responses), [TMDB sizes talk](https://themoviedb.org/talk/5a6986550e0a260d6400d6c0) · [OpenSubtitles MCP README](https://glama.ai/mcp/servers/@opensubtitles/mcp.opensubtitles.com/blob/607a9eb0ecd1d2e908642f9ecf5dd4335d997db0/README.md), [OpenSubtitles limits (Reddit 2024)](https://redlib.hbubli.cc/r/learnprogramming/comments/1bd0rxx/using_subtitles_api), [VLsub VIP wall](https://forum.videolan.org/viewtopic.php?p=545241), [Mintlify OS guide](https://www.mintlify.com/logscore/plank/integrations/opensubtitles) · [Subdl via cloudstream](https://gitdab.com/recloudstream/cloudstream/commit/469a71236b6e78018f3f72c83f0b051bdab189c3), [Subdl limit (tMM)](https://gitlab.com/tinyMediaManager/tinyMediaManager/-/issues/2631).

Тесты: [Compose testing patterns](https://developer.android.com/develop/ui/compose/testing/common-patterns) · [Compose Preview Screenshot Testing](https://developer.android.com/studio/preview/compose-screenshot-testing) · [Android testing guide 2026 (вторичный)](https://resumegeni.com/blog/android-engineer/testing-and-ci).
