# Mobile stack and UX for an Android VLC remote (HTPC on Windows)

Date: 2026-10-12. Status: research, not a specification.

Related documents:
- [live-probe.md](live-probe.md) — a live check of the VLC 3.0.23 HTTP/RC interfaces on this PC (volume scale 0..512, track IDs `Stream N`, `browse.json`, `/art`, custom Lua pages). We rely on it here and do not duplicate it.
- `vlc-protocols.md` — planned (VLC and companion protocols).

Notation: **✅** — verified against a primary source (link next to it); **"not verified"** — from memory, secondary sources, or requires an experiment on a device.

Scenario: a Windows 11 laptop is connected to a TV, the movies are in `D:\shared`, the viewer sits on the couch with a phone. The remote must work in a dim room, one-handed, without looking at the phone screen for more than a second.

---

## 0. Summary (TL;DR)

1. **Stack: Kotlin + Jetpack Compose** (what the project already has). All capabilities critical for a remote — RFCOMM, `NsdManager`, `MediaSession` with "remote volume", `TileService`, a Glance widget, a foreground service, volume key interception — are platform APIs without wrappers. Flutter / React Native / .NET MAUI add a plugin layer exactly where our riskiest part is. Keep Compose Multiplatform in mind structurally (a pure Kotlin module for the protocol and the file-name parser without Android dependencies), but do not move the UI to CMP: iOS is not planned, and Material 3 in CMP lags behind Jetpack.
2. **Versions (October 2026, verified):** Kotlin 2.4.21, AGP 9.4.0 (Gradle 9.6, JDK 17), Compose BOM 2026.09.00 (ui 1.12.1, material3 1.4.0), Navigation 3 1.2.0, Media3 1.11.1, Ktor 3.6.0, OkHttp 5.5.0, kotlinx.serialization 1.11.0, Koin 4.2.2 / Hilt 2.60.1, Room 2.8.5, DataStore 1.2.1, Coil 3.6.3, Glance 1.2.0. Material 3 Expressive is only in `material3:1.5.0-beta01` under `@ExperimentalMaterial3ExpressiveApi`; for the MVP we stay on 1.4.0.
3. **Platform:** volume keys in the foreground — `dispatchKeyEvent` in the Activity; with the screen off — via `MediaSession` + a `Player` with `DeviceInfo.PLAYBACK_TYPE_REMOTE` (Media3 wires up `VolumeProviderCompat` itself). This is a documented mechanism (that is how Cast works), but there was a regression on Android 12 and there are OEM nuances — check on a real phone. Keep the connection in a foreground service of type `connectedDevice` (prerequisite: `BLUETOOTH_CONNECT` or the manifest permission `CHANGE_WIFI_MULTICAST_STATE`), **not** `mediaPlayback`. Android 17 (API 37) introduces the runtime permission `ACCESS_LOCAL_NETWORK` — without it neither mDNS nor HTTP to 192.168.x.x works with targetSdk 37.
4. **UX:** one main "Remote" tab = Now Playing + transport + volume; a large play/pause at the bottom in the thumb zone; ±10/±30 s buttons; a scrub bar with chapters; quick "Subtitles/Audio" sheets with languages and fine delay adjustment; "Continue watching"; a dark theme with a true-black option; haptics on every "tick". The main complaints about existing VLC remotes: *the connection drops*, *painful HTTP interface password setup*, *intrusive ads and fragmented purchases*, *overly sensitive seeking*. This is exactly our field for differentiation.
5. **Metadata:** parse file names following the guessit/PTN rules (regex slices: year, SxxEyy, quality, codecs, group → the remainder = the title). Since the developer knows C#, it makes sense to do the parser and indexing **in the PC companion** (along with `Emby.Naming`-like rules and ffmpeg thumbnails) and give the phone ready JSON; a Kotlin port is a light fallback for the "no companion" mode. Posters — TMDB (free for non-commercial use, attribution required, cache ≤ 6 months, a limit of ~40 rps without a hard figure); cache — Coil 3; fallback — VLC's `/art` and thumbnails from the companion. Subtitles (OpenSubtitles/Subdl) — **not in v1**.
6. **IA:** 3 tabs (Remote · Library · Playlist) + a "Connection" screen + "Settings". States: `Disconnected → Connecting → Connected{VlcNotRunning | Idle | Playing/Paused}` + `Error(reason)`. Onboarding: "Find PC → Password/PIN → Test (pause on the TV)".
7. **Tests:** Compose tests + Robolectric/Roborazzi, Preview Screenshot Testing; a fake VLC on Ktor reproducing the quirks from `live-probe.md`; the emulator reaches the host via `10.0.2.2` (mDNS does not work in the emulator); Bluetooth — only on a real device.

---

## 1. Choosing the phone stack

### 1.1. Criteria and comparison

The criteria come from the remote's requirements: Bluetooth Classic (RFCOMM/SPP) as a fallback channel, PC discovery via mDNS (`NsdManager`), WebSocket to the companion / HTTP to VLC, connection survival in the background, hardware volume key interception, haptics, a widget + a Quick Settings tile, Wear OS later, build pain, APK size.

| Criterion | Kotlin + Jetpack Compose | Compose Multiplatform / KMP | Flutter | React Native | .NET MAUI |
|---|---|---|---|---|---|
| Bluetooth RFCOMM | Native: `BluetoothSocket`/`createRfcommSocketToServiceRecord`, runtime `BLUETOOTH_CONNECT` (API 31+) ✅ [[BluetoothSocket]](https://developer.android.com/reference/android/bluetooth/BluetoothSocket) | The same, in `androidMain` (expect/actual) | Plugins: `flutter_bluetooth_serial` is abandoned (last push Jan 2025, 102 issues, Android-only); `flutter_classic_bluetooth` 0.1.x is young ✅ [[pub.dev]](https://pub.dev/packages/flutter_classic_bluetooth/versions/0.1.4) | `react-native-bluetooth-classic` 1.73.0-rc — New Architecture status not confirmed ✅ [[jsdelivr]](https://www.jsdelivr.com/package/npm/react-native-bluetooth-classic) | No first-party option; `Plugin.BLE` is BLE only ✅ [[nuget]](https://www.nuget.org/packages/plugin.ble). RFCOMM — write by hand via Android bindings |
| mDNS / `NsdManager` | Native; `MulticastLock` up to T-ext-7; from Android 17 — `ACCESS_LOCAL_NETWORK` (see §3.7) | The same | Plugins (`nsd`, `bonsoir`) — not verified | `react-native-zeroconf` — New Arch status not confirmed | Platform code |
| WebSocket / HTTP | OkHttp 5.5 or Ktor 3.6 (both with WebSocket) | Ktor (multiplatform) | `web_socket_channel`, `dio` — mature | `ws` built in | `ClientWebSocket` in .NET — mature |
| Background connection (FGS, types) | Native (`connectedDevice`) | The same | Plugin/native code for Android 14+ FGS types | Native module | Platform code |
| Volume keys | `Activity.dispatchKeyEvent`; `MediaSession` + `VolumeProvider` | The same in `androidMain` | Plugin (`hardware_buttons`-style, often abandoned); MediaSession — via `audio_service` | Native module | `OnKeyDown` override in `MainActivity` — simple (it is an Android class) |
| Haptics | `LocalHapticFeedback` + `HapticFeedbackType.Confirm/Reject/SegmentTick/GestureEnd` ✅ [[API]](https://developer.android.com/reference/kotlin/androidx/compose/ui/hapticfeedback/HapticFeedbackType) | The same | `HapticFeedback.*` — basic | `Vibration`/`expo-haptics` | `HapticFeedback.Perform` — basic |
| Widget / QS tile | Glance 1.2.0 + `TileService` | Android part only | `home_widget` + native `TileService` | Native | Platform code, RemoteViews by hand |
| Wear OS | Wear Compose 1.7.1 + ProtoLayout/Tiles, Data Layer | Android part | Separate native module | Native | Practically none |
| Build/tooling | One Gradle project, Studio, Compose Preview, Hot Reload (via the CMP plugin) | + iOS toolchain, Xcode; Hot Reload "out of the box" | Separate SDK, Dart; stable DevTools; Flutter 3.47 (Aug 2026) ✅ [[docs]](https://docs.flutter.dev/release/whats-new.html) | Node + Gradle, New Architecture mandatory since RN 0.82 ✅ [[Expo]](https://docs.expo.dev/guides/new-architecture.md) | MAUI 10 (patch 10.0.90, July 2026; EOL May 2027) ✅ [[policy]](https://dotnet.microsoft.com/ru-ru/platform/support/policy/maui) |
| APK size | Minimal: Compose uses the system Skia; a 2023 test: ~1.5 MB vs Flutter ~×5 ✅ [[Medium]](https://medium.com/@jacobras/android-ios-native-vs-flutter-vs-compose-multiplatform-7ef3d5ec2a56) (an old benchmark, not hello-world) | On Android = same as native | Engine in the bundle, + several MB | Hermes + JS bundle, + several MB | .NET runtime, usually 10+ MB (not verified) |

Takeaway from the table: **every "hard" row (BT, mDNS, FGS, volume, widgets) is an Android API**, and in cross-platform stacks it turns either into someone else's plugin with unclear maintenance or into the same Kotlin code wrapped in a binding. The cross-platform gain is zero when demand for iOS is zero.

### 1.2. The state of Jetpack Compose in October 2026

- **Compose BOM 2026.09.00** → `ui/foundation/runtime/animation 1.12.1`, `material3 1.4.0` ✅ [[BOM mapping]](https://developer.android.com/develop/ui/compose/bom/bom-mapping).
- **Material 3 Expressive.** In the stable `material3:1.4.0` only `WideNavigationRail` and `ShortNavigationBar` are public; `MaterialExpressiveTheme`, `MotionScheme`, `ButtonGroup`, `FloatingToolbar`, `SplitButton`, `LoadingIndicator`, FAB menu, etc. are in the **1.5.0** line (beta01 of 07.10.2026) under `@ExperimentalMaterial3ExpressiveApi` ✅ [[release notes]](https://developer.android.com/jetpack/androidx/releases/compose-material3), [[composables.com]](https://composables.com/material3/materialexpressivetheme). For the MVP: 1.4.0; Expressive components (especially `ButtonGroup` for ±10/±30 and `FloatingToolbar`) — after 1.5.0 stable is out. 1.5.0-alpha29 has a source-breaking change to `Slider` (`onValueChange` is mandatory, a move to `SliderState`) — take it into account when migrating.
- **Adaptive layouts:** `material3-adaptive 1.3.0` (12.08.2026) — `ListDetailPaneScaffold`, `SupportingPaneScaffold`, `NavigationSuiteScaffold`, `adaptive-navigation3` ✅ [[releases]](https://developer.android.com/jetpack/androidx/releases/compose-material3-adaptive). Important for the remote: a phone in landscape = *medium* width, *compact* height → a two-pane layout based on width alone does not work, height must be taken into account too ✅ [[ProAndroidDev]](https://proandroiddev.com/adaptive-compose-layouts-86b7f1e51338).
- **Navigation 3 1.2.0** (23.09.2026): your own back stack as state, `NavDisplay`, Result API, predictive back via `NavigationBackHandler` ✅ [[releases]](https://developer.android.com/jetpack/androidx/releases/navigation3). For 3 tabs + sheets it is more than enough; there is no need to keep `navigation-compose` 2.x.
- **Compose Preview Screenshot Testing** requires AGP ≥ 9.0, Kotlin ≥ 2.2.10, JDK 17 ✅ [[docs]](https://developer.android.com/studio/preview/compose-screenshot-testing).
- The Compose compiler is the `org.jetbrains.kotlin.plugin.compose` plugin at the Kotlin version (since 2.0), we already have it applied.

### 1.3. Compose Multiplatform / KMP: iOS maturity in 2026

- iOS in CMP has been stable since **1.8.0 (May 2025)**: native scroll physics, text selection, RTL, drag-and-drop; JetBrains claims parity with SwiftUI in startup and scrolling and ~+9 MB in size ✅ [[JetBrains blog]](https://blog.jetbrains.com/kotlin/2025/05/compose-multiplatform-1-8-0-released-compose-multiplatform-for-ios-is-stable-and-production-ready/).
- The current version is **1.12.1 (22.09.2026)**; Material3 in CMP is `1.12.0-alpha03` based on Jetpack `material3 1.5.0-alpha22`, i.e. **Material 3 in CMP follows the alpha branch**; Navigation3 1.1.2; Compose Hot Reload 1.2.0 + an MCP server ✅ [[releases]](https://github.com/JetBrains/compose-multiplatform/releases).
- In practice: isolated reports of `TextField` problems with third-party IMEs on iOS (2025, not reproduced by others) ✅ [[Zenn]](https://zenn.dev/bcc/articles/45170a8518ef78) — not critical for a remote, but it shows that edge cases are still alive.
- **Decision:** the UI stays Jetpack Compose. But the **VLC/companion protocol, the player state model, and the file-name parser** layers should be packaged as a pure Kotlin module (`commonMain`-compatible: no `android.*`, on Ktor + kotlinx.serialization + coroutines). This is free, simplifies JVM tests and keeps the door to CMP open.

### 1.4. Hilt vs Koin

| | Hilt (Dagger 2.60.1) | Koin 4.2.2 |
|---|---|---|
| Graph verification | Compile-time (KSP 2.3.12) | Runtime (there is `koin-annotations` with compile-time verification, not verified on 4.2) |
| Requirements | AGP ≥ 9.0, minSdk 23 (multidex removed in 2.60) ✅ [[releases]](https://github.com/google/dagger/releases) | None; there is `koin-compose-navigation3` for Navigation 3 ✅ [[releases]](https://github.com/InsertKoinIO/koin/releases) |
| KMP | No (JVM/Android) | Yes |
| Boilerplate | `@HiltViewModel`, `@AndroidEntryPoint`, modules | `module { viewModelOf(::X) }`, `koinViewModel()` |
| Services/TileService/Glance | `@AndroidEntryPoint` for services; for Glance — `EntryPointAccessors` | `KoinComponent` / `get()` everywhere |

Recommendation: **Koin** — the app is small, with many entry points outside an Activity (FGS, `TileService`, Glance callbacks, `MediaSessionService`), where Hilt requires workarounds; plus KMP compatibility of the pure module. If compile-time verification matters, Hilt is fine too, but then at least AGP 9 and a Kotlin version that Dagger already supports (2.60 is built with Kotlin 2.3.21; with 2.4.x — to be checked).

### 1.5. Library versions (verified 2026-10-12)

| Component | Stable version | Date | Source |
|---|---|---|---|
| Kotlin | **2.4.21** | 08.10.2026 | ✅ [github](https://github.com/JetBrains/kotlin/releases) |
| Android Gradle Plugin | **9.4.0**; Gradle 9.6.0, JDK 17, Build-Tools 36.0.0, maxSdk 37 | Sep 2026 | ✅ [dev.android](https://developer.android.com/build/releases/gradle-plugin) |
| Compose BOM | **2026.09.00** (ui 1.12.1, material3 1.4.0) | Sep 2026 | ✅ [mapping](https://developer.android.com/develop/ui/compose/bom/bom-mapping) |
| Material 3 | 1.4.0; 1.5.0-beta01 (Expressive) | 07.10.2026 | ✅ [releases](https://developer.android.com/jetpack/androidx/releases/compose-material3) |
| material3-adaptive | **1.3.0** | 12.08.2026 | ✅ [releases](https://developer.android.com/jetpack/androidx/releases/compose-material3-adaptive) |
| Navigation 3 | **1.2.0** | 23.09.2026 | ✅ [releases](https://developer.android.com/jetpack/androidx/releases/navigation3) |
| Media3 (session) | **1.11.1** | 10.09.2026 | ✅ [releases](https://developer.android.com/jetpack/androidx/releases/media3) |
| Ktor | **3.6.0** | 16.09.2026 | ✅ [github](https://github.com/ktorio/ktor/releases) |
| OkHttp | **5.5.0** | 16.08.2026 | ✅ [changelog](https://github.com/square/okhttp/blob/master/CHANGELOG.md) |
| kotlinx.serialization | **1.11.0** (1.12.0-RC 04.09) | 09.04.2026 | ✅ [github](https://github.com/Kotlin/kotlinx.serialization/releases) |
| kotlinx.coroutines | **1.11.0** | 08.05.2026 | ✅ [github](https://github.com/Kotlin/kotlinx.coroutines/releases) |
| KSP | **2.3.12** (the version is no longer tied to Kotlin) | 09.09.2026 | ✅ [github](https://github.com/google/ksp/releases), [PR](https://github.com/google/ksp/pull/3107) |
| Dagger/Hilt | **2.60.1**; androidx.hilt 1.4.0 | 06.07.2026 | ✅ [github](https://github.com/google/dagger/releases) |
| Koin | **4.2.2** | 15.06.2026 | ✅ [github](https://github.com/InsertKoinIO/koin/releases) |
| Room | **2.8.5**; Room 3.0.1 (`androidx.room3`, KMP-oriented) | 09.09.2026 / 29.07.2026 | ✅ [room](https://developer.android.com/jetpack/androidx/releases/room), [room3](https://developer.android.com/jetpack/androidx/releases/room3) |
| DataStore | **1.2.1** (1.3.0-alpha11) | 11.03.2026 | ✅ [releases](https://developer.android.com/jetpack/androidx/releases/datastore) |
| Coil | **3.6.3** (`coil-network-okhttp` / `coil-network-ktor3`) | 18.09.2026 | ✅ [github](https://github.com/coil-kt/coil/releases) |
| Glance | **1.2.0** (1.3.0-alpha02) | 26.08.2026 | ✅ [releases](https://developer.android.com/jetpack/androidx/releases/glance) |
| Wear Compose | **1.7.1** (Material 3 for Wear is stable) | 07.10.2026 | ✅ [releases](https://developer.android.com/jetpack/androidx/releases/wear-compose) |
| Compose Multiplatform | **1.12.1** | 22.09.2026 | ✅ [github](https://github.com/JetBrains/compose-multiplatform/releases) |
| Android 17 (API 37) | released | June 2026 | ✅ [OkHttp changelog](https://github.com/square/okhttp/blob/master/CHANGELOG.md) (mention) |

Notes:
- Room 3 (`androidx.room3`) is a new package focused on KMP; for an Android-only app Room 2.8.x remains current (the Room 2 page has no deprecation notice). For our DB (watch positions, metadata cache, the list of PCs) Room 2.8.5 or even DataStore + a JSON file is enough; we take Room for the "continue watching" queries.
- Media3 1.10 removed the commands for changing *local* volume from `MediaController`; remote volume (`DeviceInfo.PLAYBACK_TYPE_REMOTE`) works (see §3.1).
- Ktor 3.5 fixed WebSocket ping in the OkHttp engine, 3.4.x fixed looping on broken frames; take ≥ 3.6.0.

### 1.6. Stack recommendation

**Kotlin 2.4.21 + Jetpack Compose (BOM 2026.09.00, Material 3 1.4.0) + Navigation 3 + Media3 Session + Ktor client 3.6 (OkHttp engine) + kotlinx.serialization + Koin + Room 2.8 + DataStore + Coil 3 + Glance.** Split into modules: `:core:protocol` (pure Kotlin), `:core:naming` (the name parser, pure Kotlin), `:app`. Why Ktor rather than "bare" OkHttp: one client for VLC HTTP polling, WebSocket to the companion and (later) TMDB; `MockEngine` for tests; the engine is OkHttp anyway. Wire Coil through `coil-network-okhttp` on the same `OkHttpClient` as Ktor (a shared connection pool, the `Authorization` header for `/art`).

---

## 2. Android platform specifics for a remote

### 2.1. Hardware volume keys

**App in the foreground.** Override `dispatchKeyEvent` in the `ComponentActivity`: on `KEYCODE_VOLUME_UP/DOWN` with `ACTION_DOWN`, send a command to VLC and return `true` (the system slider will not appear). This is the standard mechanism: unhandled events fall through to `PhoneFallbackEventHandler`, where the volume is handled ✅ [[Tuts+]](https://code.tutsplus.com/android-sdk-intercepting-physical-key-events--mobile-10379t). `Modifier.onKeyEvent` in Compose is unreliable for these keys (it needs focus) — not verified, but the Activity level is better. Auto-repeat on hold: `event.repeatCount`. Make this a **setting** ("Volume keys control: PC / phone"), since sometimes you need to adjust the phone's notification volume.

**Screen off / app in the background (the main question).** The mechanism is a `MediaSession` with "remote" volume; this is how Cast works:

- Legacy: `MediaSessionCompat.setPlaybackToRemote(VolumeProviderCompat)`; the provider receives `onAdjustVolume(direction)` / `onSetVolumeTo(volume)` and **must** call `setCurrentVolume()` after every change ✅ [[VolumeProvider]](https://developer.android.com/reference/android/media/VolumeProvider.html). The session must be `active` and have a `PlaybackState` ✅ [[MediaSession]](https://developer.android.com/reference/android/media/session/MediaSession).
- Media3: we write our own `Player` (a `SimpleBasePlayer` subclass mirroring the VLC state), return `DeviceInfo(PLAYBACK_TYPE_REMOTE, min=0, max=100)` in `State`, and the commands `COMMAND_ADJUST_DEVICE_VOLUME_WITH_FLAGS` + `COMMAND_SET_DEVICE_VOLUME_WITH_FLAGS`. `MediaSessionLegacyStub` itself creates a `VolumeProviderCompat` for any `playbackType != PLAYBACK_TYPE_LOCAL`, calls `player.increaseDeviceVolume/decreaseDeviceVolume/setDeviceVolume` and forwards `setCurrentVolume` ✅ [[MediaSessionLegacyStub.java source]](https://github.com/androidx/media/blob/release/libraries/session/src/main/java/androidx/media3/session/MediaSessionLegacyStub.java). Control type: relative with `ADJUST`, absolute with `ADJUST`+`SET`.
- Known pitfalls: in Android 12 (beta 5) volume keys stopped reaching remote sessions; fixed in 12.0.0_r26 via `canHandleVolumeKey` — allowed if playback is local, **or** the app has a single route, **or** `config_volumeAdjustmentForRemoteGroupSessions` is enabled (disabled for groups on Pixel) ✅ [[esper.io]](https://esper.io/blog/android-12-cast-volume-changes-explained). For us (a single route) it should pass. OEM firmwares with "track control when the screen is off" (LineageOS) may intercept ✅ [[Poweramp forum]](https://forum.powerampapp.com/topic/21716-volume-keys-control-does-not-work-when-the-screen-is-off/). Chromecast users report that the buttons work on the lock screen with "Media Session usage" enabled and broke on some QPR betas ✅ [[Nest community]](https://www.googlenestcommunity.com/t5/Streaming/Enhancing-Chromecast-Experience-Simplifying-Media-Volume-Control-during/m-p/462235).
- Conditions for the session to remain "top" and active with the screen off: `MediaSessionService` + a foreground notification, `PlaybackState = PLAYING` (we mirror the VLC state), `isActive = true`. Since our `Player` plays nothing locally, audio focus is not needed.
- **Android 17 (all apps):** "background audio hardening" restricts background calls for *playback, audio focus, and volume changes*; with targetSdk 37 background audio access requires an FGS with while-in-use ✅ [[behavior-changes-all]](https://developer.android.com/about/versions/17/behavior-changes-all), [[behavior-changes-17]](https://developer.android.com/about/versions/17/behavior-changes-17). Whether this applies to the `VolumeProvider` of a remote session (we do not touch `AudioManager`) is **not verified**; the risk is that with targetSdk 37 we would have to hold an FGS with the WIU capability. Keep targetSdk 36 until verified.
- **Doze.** A phone on the couch is stationary → after ~30+ minutes with the screen off it enters Doze, the network is suspended (except for maintenance windows) ✅ [[Doze]](https://developer.android.com/training/monitoring-device-state/doze-standby). Whether this applies to a process with an FGS — sources disagree (third parties say "yes, to everyone") — **not verified**. In practice: a volume key press calls `onAdjustVolume` in our process; if the socket has gone to sleep, send a one-off HTTP request with a short timeout; if that is blocked too, offer "Don't restrict battery" in the settings (`ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS`; acceptable for a personal app, for Play it requires justification).

**Feasibility verdict:** controlling the PC volume with the screen off is **feasible and documented** (the Cast path), but depends on the Android version/firmware and Doze. In the MVP — foreground interception (guaranteed) + the MediaSession path as "works where it works", with a test on a real device and a checklist in §6.4.

### 2.2. Keeping the connection alive: the foreground service and its type

Since Android 14 (API 34) the FGS type is mandatory, plus the corresponding `FOREGROUND_SERVICE_*` permission and a declaration in the Play Console ✅ [[FGS types]](https://developer.android.com/develop/background-work/services/fgs/service-types).

| Type | Permission | Prerequisites at start time | Fits? |
|---|---|---|---|
| `connectedDevice` | `FOREGROUND_SERVICE_CONNECTED_DEVICE` | At least one of: manifest `CHANGE_NETWORK_STATE` / `CHANGE_WIFI_STATE` / **`CHANGE_WIFI_MULTICAST_STATE`** / `NFC` / `TRANSMIT_IR`, **or** granted runtime `BLUETOOTH_CONNECT`/`SCAN`/`ADVERTISE`, or `UsbManager.requestPermission()` | **Yes.** We need `CHANGE_WIFI_MULTICAST_STATE` anyway for the `MulticastLock` (mDNS); `BLUETOOTH_CONNECT` — for RFCOMM. Type description: "interaction with external devices over Bluetooth/NFC/IR/USB **or the network**" |
| `mediaPlayback` | `FOREGROUND_SERVICE_MEDIA_PLAYBACK` | None | Formally "continuing audio/video in the background"; we play nothing; with targetSdk 15 it cannot be started from `BOOT_COMPLETED`. Use it **only if** the MediaSession volume path requires exactly this type (not verified) |
| `dataSync` | `FOREGROUND_SERVICE_DATA_SYNC` | None | No: a limit of 6 h/day with targetSdk 35 ✅ [[Android 15]](https://developer.android.com/about/versions/15/behavior-changes-15) |
| `specialUse` | `FOREGROUND_SERVICE_SPECIAL_USE` | `<property PROPERTY_SPECIAL_USE_FGS_SUBTYPE>` with an explanation; review in Play | Fallback option |

Strategy:
- The FGS **does not run permanently**. It starts on connecting from the UI (`startForeground` is allowed while the app is visible) and stays up while: the app is on screen **or** playback is running on the PC and the user has not left the session (a MediaSession notification with buttons). It stops N minutes after `stopped`/VLC disconnecting or via the "Disconnect" button in the notification.
- Android 16: JobScheduler jobs running in parallel with an FGS are now also subject to quota ✅ [[Android 16 all]](https://developer.android.com/about/versions/16/behavior-changes-all) — does not affect us (we do not use WorkManager for the connection).
- Android 17: `AbandonedJob`, lock-free `MessageQueue`, a memory limit on `RemoteViews` in widgets of `1.5×W×H×4` bytes ✅ [[Android 17]](https://developer.android.com/about/versions/17/behavior-changes-17) — important for a Glance widget with a poster (shrink to ≤ 300 px).

### 2.3. Notification and lock screen like a media player

`MediaSessionService` (Media3) + our `SimpleBasePlayer` wrapper over the VLC state gives: a card in the notification shade with artwork (from `/art` or TMDB), prev/±10/play-pause/+30/next buttons, progress (we know `time/length`), control from the lock screen and from Android Auto/Wear for free. Media3 1.11 tightened `MediaSession` threading and made `onConnect` read-only for untrusted controllers ✅ [[Media3 1.11]](https://developer.android.com/jetpack/androidx/releases/media3). Kore 3.0 did exactly this ("integration with Android media notifications") ✅ [[Kore 3.0]](https://kodi.tv/article/kore-3-0), and one of the requests to Kore is "behave like a media player, accept Media Buttons" ✅ [[issues]](https://github.com/xbmc/Kore/issues) — i.e. Bluetooth headsets/watches need it too.

### 2.4. Quick Settings tile

A `TileService` with `BIND_QUICK_SETTINGS_TILE` and a `QS_TILE` filter; on API 33+ offer to add the tile via `StatusBarManager.requestAddTileService()` from a visible Activity ✅ [[API diff 33]](https://developer.android.com/sdk/api_diff/33/changes/android.app.StatusBarManager), [[docs]](https://developer.android.com/develop/ui/views/quicksettings-tiles). `META_DATA_ACTIVE_TILE` mode + `requestListeningState()` when the VLC state changes, so the tile shows ▶/❚❚ without binding every time the shade opens. `onClick` → the `pl_pause` command (through our repository, not through `MediaSessionManager` — then no `NotificationListener` is needed). If there is no connection — `Tile.STATE_UNAVAILABLE` with the label "PC not found".

### 2.5. Glance widget

Glance 1.2.0: lambda actions are stable, `GlanceTheme`/`glance-material3`, `SizeMode.Responsive`, `providePreview` ✅ [[Glance]](https://developer.android.com/jetpack/androidx/releases/glance). Content: a poster (small, ≤ 300 px — the `RemoteViews` limit on Android 17), title, ▶/❚❚, −/+ volume, "+30". State — from `DataStore` via `GlanceStateDefinition`, updates — from the FGS on every status change. Taps go through `actionRunCallback` → repository → HTTP. Without a connection — a single "Connect" button (opens the app).

### 2.6. Wear OS (later)

Wear Compose 1.7.1; `compose-material` for Wear is declared superseded in favour of `compose-material3` ✅ [[Wear Compose]](https://developer.android.com/jetpack/androidx/releases/wear-compose). Tiles — via ProtoLayout + `protolayout-material3` (Expressive) ✅ [[codelab]](https://developer.android.com/codelabs/wear-tiles). The cheapest path is no watch app at all: the MediaSession from §2.3 gives standard media controls on the watch. A separate Tile with play/pause/volume — once the companion and a stable protocol exist. `glance-wear-tiles` is deprecated, do not use ✅.

### 2.7. Local network and mDNS

- **Android 17 / targetSdk 37:** a new runtime permission **`ACCESS_LOCAL_NETWORK`** (the `NEARBY_DEVICES` group), mandatory for any traffic to local addresses: mDNS/SSDP over raw sockets, `NsdManager`, **OkHttp/Cronet to 192.168.0.0/16, 10/8, 172.16/12, link-local, multicast/broadcast**. The only exceptions are DNS:53 and the Output Switcher. On Android 16 this is opt-in via the temporary `NEARBY_WIFI_DEVICES`; test: `adb shell am compat enable RESTRICT_LOCAL_NETWORK <pkg>`; when blocked — `EPERM`/`ECONNABORTED` ✅ [[Android 16 targeting]](https://developer.android.com/about/versions/16/behavior-changes-16), [[Android 17 targeting]](https://developer.android.com/about/versions/17/behavior-changes-17), [[local network permission]](https://developer.android.com/privacy-and-security/local-network-permission). Do not request it before moving to targetSdk 37; explain why during onboarding ("to see your PC on the home network").
- **`NsdManager`:** before SDK extension T-7 the app takes a `WifiManager.MulticastLock` itself, even in the foreground; from T-ext-7 the system does it itself for foreground apps; check `SdkExtensions`, not `Build.VERSION` ✅ [[NsdManager]](https://developer.android.com/reference/kotlin/android/net/nsd/NsdManager). Historically "capricious" (one discovery session at a time, stuck listeners after a crash) ✅ [[CommonsWare]](https://commonsware.com/office-hours/2021-04-27); Kore uses JmDNS ✅ [[Kore README]](https://github.com/xbmc/Kore). For us: `NsdManager` + manual IP entry + "last successful address" as a fallback; the companion publishes `_vlc-remote._tcp` with TXT (`vlc=3.0.23`, `companion=1`, `http=8080`).
- The emulator is isolated behind a virtual router — multicast does not pass, mDNS in the emulator **does not work**; the host = `10.0.2.2` ✅ [[emulator networking]](https://developer.android.com/studio/run/emulator-networking-address).

### 2.8. Bluetooth Classic (RFCOMM)

- API 31+: runtime `BLUETOOTH_CONNECT` for `createRfcommSocketToServiceRecord()`; `BLUETOOTH_SCAN` only if we search for devices (`bondedDevices` is enough for us) ✅ [[BluetoothSocket]](https://developer.android.com/reference/android/bluetooth/BluetoothSocket).
- **Android 17:** `InputStream.read()` on RFCOMM returns `-1` on close/disconnect instead of an `IOException` — the read loop must check both ✅ [[Android 17]](https://developer.android.com/about/versions/17/behavior-changes-17). Android 16: `ACTION_KEY_MISSING` when the bond is lost; Android 17 — autonomous re-pairing with a dialog for the user ✅.
- Windows side (the C# companion): `RfcommServiceProvider.CreateAsync(RfcommServiceId.FromUuid(...))` + `StreamSocketListener` (WinRT, works from desktop .NET) or 32feet.NET ✅ [[MS docs]](https://msdn.microsoft.com/library/windows/apps/dn263511), [[32feet]](https://inthehand.com/components/32feet/). Pairing is done in the Windows/Android settings, the app only connects to the SPP UUID. Bluetooth is a **fallback channel** (Wi-Fi went down, the router is rebooting) and for "wake the laptop" — not the main one.

### 2.9. Battery and when to disconnect

Rules:
1. The app screen is visible → keep the connection (WebSocket/polling `status.json` every 500–1000 ms, see `live-probe.md`: ~21 ms on localhost).
2. The app went to the background, playback is running on the PC → FGS + MediaSession, poll less often (2–5 s) or only push from the companion.
3. The PC is `stopped` > 10 min **or** the phone lost Wi-Fi → close the socket, stop the FGS; there is no need to leave a "quiet" notification. Resume on opening the app, the tile, the widget, or a volume key (via MediaSession, if the session is still alive).
4. Doze: do not fight it; reconnect with exponential backoff + jitter on return; test with `adb shell dumpsys deviceidle force-idle`.

### 2.10. Other platform changes affecting the UI

- **Edge-to-edge is mandatory** with targetSdk 36 on Android 16 (the opt-out is disabled) ✅ — in Compose this is `enableEdgeToEdge()` + `WindowInsets`; for a remote it is a plus: content under the status bar, buttons above the navigation bar.
- **Predictive back** is on by default with targetSdk 36; `onBackPressed`/`KEYCODE_BACK` are not delivered ✅ — use `BackHandler`/Navigation 3.
- **Orientation:** on screens ≥ sw600dp (tablets, Chromebooks) `screenOrientation` is ignored with targetSdk 36, the temporary opt-out does not work for 37 ✅ — a landscape layout is needed in any case (§4.2).

---

## 3. UX research: what we take from the best remotes

### 3.1. App overview

| App | What is done well (patterns) | What is criticized / missing |
|---|---|---|
| **Kore** (Kodi, open source, Apache-2.0, v3.1.0 Jan 2024) ✅ [[F-Droid]](https://f-droid.org/en/packages/org.xbmc.kore/), [[Kore 3.0]](https://kodi.tv/article/kore-3-0) | A remote with *the current playback state right on the remote screen*; Now Playing with movie/series details; switching/syncing/downloading subtitles and audio tracks; playlist; library with posters; WoL; "Send to Kodi" (YouTube/URL via the share sheet + text input); Material 3 + dynamic color; Android media notifications | Requests: "Resume playing" (6 👍), "Playback speed" (2), "show Kodi dialogs in Kore", "behave like a media player (media buttons)", "the volume control is too small"; on the 3.0 beta people asked for **large rectangular buttons instead of small icons** ✅ [[issues]](https://github.com/xbmc/Kore/issues), [[forum]](https://forum.kodi.tv/showthread.php?tid=369711) |
| **Plex** (Companion/remote specification) ✅ [[wiki]](https://github.com/plexinc/plex-media-player/wiki/Remote-control-API), [[Android 7.14]](https://apkmirror.com/apk/plex-inc/plex/plex-7-14-1-9954-release) | The Now Playing button is *grey when nothing is playing*; when playback stops on the TV we stay on the Now Playing screen in the "stopped" state, we do not kick the user out; a command error → toast + treat the player as disconnected; remote control of subtitle size/color/position/offset | Companion is effectively "almost deprecated", there is no public API ✅ [[forum]](https://forums.plex.tv/t/feature-request-supported-server-mediated-api-for-controlling-remote-plex-plexamp-sessions/942315) |
| **Jellyfin** (Android) ✅ [[Symfonium]](https://support.symfonium.app/t/allow-symfonium-to-control-jellyfin-on-other-devices/7973) | "Cast to session": the remote is just another item in the Cast menu; a list of active sessions | Depends on the target client's capabilities; commands may be silently ignored |
| **Google TV Remote** (M3 Expressive, v4.39, Aug 2025) ✅ [[9to5google]](https://9to5google.com/2025/08/23/google-tv-app-material-3-expressive/), [[TechHive]](https://www.techhive.com/article/582309/the-android-tv-remote-app-for-ios-is-as-bare-bones-as-the-android-version.html) | The remote is a full-screen page, not a slide-out panel; a **D-pad ↔ swipe touchpad** toggle; voice; device selection at the bottom with large buttons | Regression: long-press on "select" broke after the redesign — lesson: test long presses |
| **Apple TV Remote** (Control Center) ✅ [[HowToGeek]](https://www.howtogeek.com/720922/how-to-use-your-iphone-or-ipad-as-an-apple-tv-remote/), [[HIG]](https://developer-mdn.apple.com/design/human-interface-guidelines/inputs/remotes) | A large gesture surface; **the phone's physical volume buttons control the TV**; HIG: do not react to an accidental tap during playback, do not override standard gestures | The D-pad is sometimes available only from Control Center (a bug) |
| **Infuse** ✅ [[support]](https://support.firecore.com/hc/en-us/articles/215090907), [[community]](https://community.firecore.com/t/subtitle-delay/14737.md), [[2x]](https://community.firecore.com/t/temporary-2x-speed-on-apple-tv-like-ios-has/57701.md) | A Playback menu with a Subtitles tab: language/file, appearance, position, **offset**; temporary 2× on hold (8.3.2) | The subtitle offset has to be tuned for every file — people ask for it to be remembered; holding > 10 s is tiring — people ask for a toggle; the OSD covers subtitles — hide controls quickly; people ask for a list of speed presets |
| **Unified Remote** ✅ [[App Store]](https://apps.apple.com/us/app/825534179), [[Softpedia]](https://mobile.softpedia.com/android/Unified-Remote-Android-Review-2859.html) | A media remote with "play/pause/volume/tracks always one tap away"; haptics (configurable); no login; list/grid; dark theme | Trackpad jitter; drag gets released; the server side glitches on the Windows lock screen |
| **KDE Connect** (MPRIS) ✅ [[bug 390414]](https://bugs.kde.org/show_bug.cgi?id=390414), [[D12559]](https://phabricator.kde.org/D12559) | A volume slider per audio output (sink) + mute via the icon; position slider = seeking | Refused to add FF/RW buttons "to keep maintenance simple" — users asked for them; VLC's MPRIS is "kinda broken" |
| **Sonos** (2024 redesign → 2026 fix) ✅ [[CMSWire]](https://www.cmswire.com/customer-experience/sonos-app-redesign-fail-customer-outrage-and-falling-stock-prices), [[forum]](https://en.community.sonos.com/controllers-and-music-services-229131/apps-fiddly-volume-control-6933330), [[eCoustics]](https://www.ecoustics.com/news/sonos-app-update-2026/) | A "how not to do it" lesson: volume lag/jumps; a "shooting range" slider: you have to hit the exact point, people want a **relative** gesture; accidental mute at low volume; **volume numbers** removed; features removed without explanation; "everything in your face at once"; in 2026 they brought back the bottom tabs and reworked volume, released as an opt-in beta | — |

### 3.2. Complaints about existing VLC remotes (Android/iOS)

Sources: App Store pages (the Play Store does not serve content without JS; the Android versions of the same apps have the same authors and features), the VideoLAN forum, a review aggregator.

- **The connection drops** "after short periods of use, the app needs a restart" — the main complaint about VLC Remote (aggregate: 84 of 563 reviews) ✅ [[marlvel.ai]](https://marlvel.ai/apps/vlc-remote/reviews). Lesson: auto-reconnect invisible to the user, a status indicator, never lose the screen.
- **The HTTP interface password.** Since VLC 2.1 the interface does not respond without a password; clients ask for a *user name*, which must be empty — people do not get it ✅ [[forum 2013]](https://forum.videolan.org/viewtopic.php?p=392423), [[forum 2015]](https://forum.videolan.org/viewtopic.php?p=425210). Both commercial remotes solved this with a **PC helper**: "Setup Wizard" + "VMR Connect" from Adarsh Urs; "free setup helper configures VLC" from Hobbyist ✅ [[App Store VMR]](https://apps.apple.com/us/app/vlc-mobile-remote/id1140931401), [[App Store Hobbyist]](https://apps.apple.com/us/app/id297244048). Our companion must do this itself (enable `http` in `vlcrc`, set a password, show a QR code).
- **Ads and fragmented purchases.** VMR has 10 different IAPs ($0.99–$7.99), "incredibly intrusive advertising" after an update; VLC Remote has "a subscription for what was already bought" ✅. Not relevant for us (a personal app), but it explains why people look for open source.
- **Seeking is too sensitive:** "-10s sometimes throws you back to the start" ✅ [[Hobbyist reviews]](https://apps.apple.com/us/app/id297244048) — in VLC `seek&val=-10` is relative, but if the command went out twice or the state got out of sync, we get a jump. Lesson: debounce, optimistic update with confirmation via `status.json`, never send a `seek` with an absolute position from stale state.
- **Several PCs on the network:** "sees only one server, settings get overwritten" ✅ — a list of hosts with names, as in Remote for VLC (nickname, test connection) ✅ [[android-vlc-remote]](https://github.com/samicemalone/android-vlc-remote).
- **The playlist goes back to the first item, the volume disappears after loading new files** ✅ — a consequence of re-requesting the whole state; keep stable `id`s from `playlist.json`.
- What the open-source Remote for VLC (GPL-3, Java, abandoned) can already do: a notification with controls, crop/subtitle/fullscreen/audio/aspect buttons in the action bar, a browser with a "library" from several folders, chapters, **audio/subtitle delay presets**, parsing file names into movies/episodes ✅ — a good parity checklist.

### 3.3. Extracted patterns and our decisions

**The surface for seeking and volume.**
- A *slider* (Plex/Kore/KDE) is precise for an absolute position, bad for "10 seconds back without looking". A *swipe touchpad* (Apple TV/Google TV) is good for menu navigation, but unpredictable for seeking without visual feedback on the phone. *Skip buttons* are the only thing that works in the dark without looking.
- Decision: **three layers.** (1) ⟲10 / ⟳30 buttons (configurable: 10/30/60/90 s; for series 90 s = "skip intro"), auto-repeat on hold with acceleration; (2) a scrub bar with chapters (tick marks from `information.chapters`, `SegmentTick` haptics when crossing a chapter), while dragging — a large time label above the finger; "speed zones" as in iOS: the further the finger moves down from the bar, the slower the scale (1×, ½×, ¼×) — not verified in Compose, implemented via `pointerInput`; (3) an optional gesture surface over the poster: horizontal swipe = ±10 s per N dp, vertical = volume, tap = play/pause, double tap left/right = ±10 s (the mobile VLC/YouTube convention). Mandatory: ignore an accidental tap during the first 300 ms after the screen appears (Apple HIG).
- Volume: **relative** gestures and ± buttons, a slider with a **number** (the Sonos lesson), protection against mute (mute is a separate button, the slider does not go down to 0). Show the VLC scale 0..512 as 0–200 % (256 = 100 %) with a soft stop at 100 %; see `live-probe.md`.

**Play/Pause and reachability.**
- Hoober: 49 % hold the phone with one hand, ⅔ of them with the right hand; the "thumb zone" is a model, not a template; users pull content towards the centre themselves ✅ [[A List Apart]](https://alistapart.com/1-2-phone-grips-jpg/), [[Osmani]](https://addyosmani.com/blog/touch-friendly-design/).
- Decision: the transport row in the **bottom third** of the screen, play/pause ≥ 88 dp in the centre, neighbours 64 dp; the top of the screen is informational (poster, title), nothing critical requires reaching down. **Left-handed mode** — mirroring the quick-action row and the position of the volume slider (a setting, not auto-detection). For tablet/landscape — `SupportingPaneScaffold`: Now Playing on the left, transport on the right; account for the *height class compact* of a phone in landscape — there, two rows of buttons instead of the poster.

**Now Playing.** A poster/backdrop (TMDB) with a dim overlay, title + year, chips "1080p · AC3 5.1 RU · SRT RU" (tapping a chip = the corresponding sheet), progress "00:42:17 / 2:58:25 · 2:16 left", the chapter name (with the companion or a Lua page — `live-probe.md`). In the `stopped` state the screen **does not disappear** (Plex): we show the last movie and a "Resume from 00:42" button.

**Quick track switchers.** A sheet with a radio list; row format: `Language · Description (codec, channels)`; sorting: the user's preferred languages first; "Off" at the top for subtitles. Data comes from `information.category` (`Stream N` → ID), the current one — from a Lua page/the companion or optimistically (see `live-probe.md`).

**Fine delay adjustment.** ±50 ms and ±500 ms buttons + "reset" + display of the current value; a "remember for this file" button (the Infuse pain point) — store in Room by file path; apply the next time the same file is started. The same for audio.

**Sleep timer.** 15/30/45/60 min and "at the end of the movie/episode"; when it expires — `pl_pause` (not `stop`, to keep the position) and, with the companion, turn off the monitor/put the PC to sleep.

**Chapters.** A list of chapters with names (if any) and times; the current one highlighted; tap = `chapter&val=N`.

**Speed.** Presets 0.75/1/1.25/1.5/2 + "hold ▶▶ = temporary 2×" (Infuse), remembering that `rate` is quantized (compare with a tolerance, `live-probe.md`).

**Aspect/zoom/crop.** The list from the README (`16:9`, `4:3`, `221:100`, `235:100`, `239:100`, …) ✅ [[README]](https://github.com/videolan/vlc/tree/master/share/lua/http/requests); "default" first; crop — separately; a preview is impossible — just labels like "Remove black bars (2.35:1 → 16:9)".

**Continue watching.** VLC does not store per-file positions over HTTP; we do it ourselves: every 10 s write `path → time` to Room, on `stopped` — the final one; a "Continue" card at the top of the library and on the remote; start = `in_play&input=<uri>` + `seek&val=<sec>` after `currentplid` changes. If < 5 % or > 95 % — treat as "not started" / "watched".

**Library.** Two views: *folders* (`browse.json?uri=file:///D:/shared/`, sorted "folders first", breadcrumbs) and *posters* (a 3× grid, 2:3, year, a progress bar). Series = a folder → a list of episodes with SxxEyy. Fast scroll with a letter; search by title (and by file name); a "not watched" filter. Files without metadata — an icon + the file name, do not break the grid.

**Dark theme.** Material: background `#121212`, not pure black; text not pure white (87 % white) ✅ [[Android Devs]](https://medium.com/androiddevelopers/dark-theme-with-mdc-4c6fc357d956). For OLED in a dark room — a **"True black" option** (background `#000000`, surfaces `#0A0A0A`) and muted accents; the claim about black "smearing" is from secondary sources, not verified. Dimming the buttons based on the light sensor — not verified, optional. Dynamic color (Material You) — yes, but with a contrast check on dark.

**Haptics.** `Confirm` on play/pause, `SegmentTick` on every 10 s of holding and at a chapter boundary, `Reject` on a command error (the PC did not respond), `GestureEnd` on releasing the scrub bar. All of it can be turned off.

**Connection state** — always visible as one line at the top (PC name · ping · icon), tap → the "Connection" screen. A command error → a snackbar with a "Retry" action, not a modal.

**Anti-patterns (Sonos):** do not hide the numbers, do not remove features silently, do not load everything at once, do not break what used to work (Google TV long-press).

---

## 4. Metadata, artwork, subtitles

### 4.1. Parsing file names

The reference is the Python library **guessit** (title, year, season/episode, screen_size `720p/1080p/2160p`, source `Blu-ray/Web/HDTV/DVD`, video_codec `H.264/H.265`, audio_codec `AAC/Dolby Digital/DTS`, audio_channels, release_group, container, edition `Director's Cut/Extended`, other `Proper/Remux/HDR10/Dolby Vision`, language, subtitle_language, country) ✅ [[guessit properties]](https://guessit.readthedocs.io/en/latest/properties.html). There is no JVM port of guessit (there is a TypeScript/WASM `guessit-js` from OpenSubtitles) ✅.

Options for Kotlin:
1. **The PTN approach** (parse-torrent-name): a chain of regexes, each match is *cut out* of the string, the remainder after normalizing separators is the title ✅ [[parse-torrent-title]](https://pypi.org/project/parse-torrent-title/2.4/). The Go port `jhin` extracts 46 fields and matches PTT byte-for-byte on a golden corpus ✅ [[pkg.go.dev]](https://pkg.go.dev/github.com/dreulavelle/jhin) — a handy source of test cases.
2. **A Java library** `io.github.mdaubie:torrent-name-parser` (MIT, Java 17, Maven Central) ✅ [[Sonatype]](https://central.sonatype.com/artifact/io.github.mdaubie/torrent-name-parser) — field completeness not verified.
3. **Emby.Naming (C#, Jellyfin)** — the developer knows C#. The key "date cleaning" rule: `(.+[^_\,\.\(\)\[\]\-])[_\,\.\(\)\[\]\-](19[0-9]{2}|20[0-9]{2})(?![0-9]+|\W[0-9]{2}\W[0-9]{2})([ _\,\.\(\)\[\]\-][^0-9]|$)` + lazy variants + `VideoVersionExpressions` (everything up to the last ` - `) ✅ [[Emby.Naming]](https://dev.emby.media/reference/pluginapi/Emby.Naming.Video.VideoResolver.html), [[diff]](https://gitea.hptrow.me/pt/jellyfin/compare/53d432852c9380e57f36adfebd4432c743028127...84f0c5fbe1a5f0352bba0396abff7ae5e3123ad6). A known trap: a CleanStrings regex with "se" truncated titles ✅ [[Emby forum]](https://emby.media/community/topic/20947-se-keyword-for-home-videos/).

**Where to parse.** The recommendation is **in the PC companion (C#)**: it has all of `D:\shared`, can hash files, cache TMDB responses for all phones, generate ffmpeg thumbnails and give the phone a ready `library.json` (`path, title, year, s/e, quality, tmdbId, poster, backdrop, duration`). On the phone — a light Kotlin parser for the "no companion" mode (only `browse.json`) — ~60 lines of regex following the PTN scheme:

```kotlin
// :core:naming — a sketch, order matters (first cut out the "noise", then look for the year/SxxEyy)
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
        val yr = year.findAll(s).lastOrNull()            // the last year: "2001 Space Odyssey (1968)"
        val tags = listOf(res, src, vcod, acod, other).mapNotNull { it.find(s)?.value }
        // Title = everything before the first "anchor" (SxxEyy, the year, or the first quality tag)
        val cut = listOfNotNull(se?.range?.first, yr?.range?.first,
            tags.firstOrNull()?.let { s.indexOf(it) }).minOrNull() ?: s.length
        var title = s.substring(0, cut).replace(Regex("""[.\[\](){}]"""), " ").trim(' ', '-', '.')
        if (title.isBlank()) title = s.substringBefore(" -").trim()   // "Title - 1080p.mkv"
        return Parsed(title, yr?.value?.toInt(), se?.let { seasonOf(it) }, se?.let { episodeOf(it) }, tags)
    }
}
```

Take golden tests from the PTT/jhin corpus + our own names from `D:\shared`. For series, a `Series/Season 01/` folder — the season comes from the folder if the name only has `E05`.

### 4.2. TMDB: terms, limits, images

- **Terms (API Terms of Use):** free only for **non-commercial** use; commercial use needs a separate agreement. Attribution is mandatory: the TMDB logo must be *less prominent* than yours, and the text "This [application] uses TMDB and the TMDB APIs but is not endorsed, certified, or otherwise approved by TMDB". **Cache data for no longer than 6 months.** Use in AI/ML applications is prohibited. Do not use TMDB as image hosting for advertising ✅ [[Terms]](https://www.themoviedb.org/api-terms-of-use).
- **Limits:** there has been no hard limit since December 2019; "somewhere around 40 requests per second", respect `429` ✅ [[rate limiting]](https://developer.themoviedb.org/docs/rate-limiting). For a home library of 500 movies — one pass with a 100 ms pause.
- **Authentication:** `Authorization: Bearer <API Read Access Token>` (a v4 token), or the `api_key` query parameter (legacy) ✅ [[search-movie]](https://developer.themoviedb.org/reference/search-movie). The key lives in the companion (not in the APK), the phone already receives image URLs. For the no-companion mode — a key in `local.properties`/BuildConfig, understanding that it can be extracted.
- **Search:** `GET /3/search/movie?query=&year=&primary_release_year=&language=ru-RU&include_adult=false&page=1`; `GET /3/search/tv`; then `/3/movie/{id}?append_to_response=images&include_image_language=ru,null` for posters in the desired language; `/3/configuration` for `base_url` and sizes.
- **Images:** `https://image.tmdb.org/t/p/{size}{file_path}`; `poster_sizes: w92 w154 w185 w342 w500 w780 original`; `backdrop_sizes: w300 w780 w1280 original`; `still_sizes: w92 w185 w300 original` ✅ [[image basics]](https://developer.themoviedb.org/docs/image-basics), [[sample config]](https://community.kde.org/Macaw-Movies/TMDB-json-responses) (read the sizes from `/configuration` at runtime; a non-existent size → an error ✅ [[TMDB talk]](https://themoviedb.org/talk/5a6986550e0a260d6400d6c0)). For a 3× grid on the phone — `w342`; for Now Playing — a `w780` poster or a `w1280` backdrop; for the widget — `w185`.
- **Fallback chain:** TMDB → `GET /art` (artwork from the VLC cache, `image/jpeg`; `/art?item=<id>` for a playlist item — `live-probe.md`, README) → a thumbnail from the companion (`ffmpeg -ss 00:05:00 -i f.mkv -frames:v 1 -vf scale=480:-1 thumb.jpg` or VLC `--video-filter=scene`) → a placeholder with the first letter of the title.

### 4.3. Caching on the phone (Coil 3)

`coil-network-okhttp:3.6.3` on the shared `OkHttpClient`; by default Coil **writes everything to the disk cache, ignoring `Cache-Control`** — for TMDB (immutable paths) this is ideal; `coil-network-cache-control` is not needed ✅ [[Coil network]](https://coil-kt.github.io/coil/network/). The cache key is the TMDB `file_path` (stable); for `/art` the key is `host + currentplid + filename`. Disk cache size 100–250 MB. Memory: `crossfade`, a `placeholder` in a colour from the previous image's palette (not verified, `Palette` API). Respect the TMDB requirement: **6 months** — set `maxAge` on the records in Room and invalidate.

### 4.4. Subtitles: OpenSubtitles and Subdl — v1 or later

- **OpenSubtitles REST API v1** (`api.opensubtitles.com/api/v1`): needs a consumer app API key, the `Api-Key` and `User-Agent` headers, login via JWT, a login limit of 1/s, 10/min, 30/h (third-party sources) ✅ [[glama]](https://glama.ai/mcp/servers/@opensubtitles/mcp.opensubtitles.com/blob/607a9eb0ecd1d2e908642f9ecf5dd4335d997db0/README.md), [[termo]](https://termo.ai/skills/opensubtitles). **Download quotas differ between sources:** 5/day per IP anonymously, 10/day for registered users, 1000 VIP ✅ [[Reddit 2024]](https://redlib.hbubli.cc/r/learnprogramming/comments/1bd0rxx/using_subtitles_api), [[VideoLAN forum]](https://forum.videolan.org/viewtopic.php?p=545241) versus "200/day free" ✅ [[Mintlify]](https://www.mintlify.com/logscore/plank/integrations/opensubtitles) — the official page (`opensubtitles.stoplight.io`, `/consumers`) is not served without JS/login — **not verified**. In 2024 VLsub hit a VIP wall ✅. Conclusion: the API is usable only with a **user account** (login in the app) and careful quota UX.
- **Subdl:** `GET https://api.subdl.com/api/v1/subtitles?api_key=&film_name=|tmdb_id=|imdb_id=&season_number=&episode_number=&year=&languages=ru,en`, the response is links to zips ✅ [[cloudstream commit]](https://gitdab.com/recloudstream/cloudstream/commit/469a71236b6e78018f3f72c83f0b051bdab189c3); a limit of ~2000 calls/day per key — from a secondary source ✅ [[tMM issue]](https://gitlab.com/tinyMediaManager/tinyMediaManager/-/issues/2631), **not verified**; the official `subdl.com/api-doc` returns 403 to a bot.
- **Decision: not in v1.** In v1 — choosing among existing tracks + `addsubtitle&val=<uri>` for files next to the video (VLC picks up a `.srt` with the same name itself). Downloading subtitles — **in the companion (v1.x):** it knows the path, can compute the OpenSubtitles hash of the file, put the `.srt` next to it and call `addsubtitle`; the phone only shows the list of results and presses "download". This way the keys and quotas live on the PC, not in the APK.

---

## 5. Information architecture

### 5.1. Connection states

```
                 ┌──────────────┐   find/pick PC      ┌──────────────┐
   start ───────▶│ Disconnected │────────────────────▶│  Connecting  │
                 └──────▲───────┘                     └──────┬───────┘
                        │ "Disconnect" / Wi-Fi lost          │ 401 → PasswordRequired
                        │                                    │ timeout/refused → Error(reason)
                        │                                    ▼ 200 status.json
                 ┌──────┴────────────────────────────────────────────────┐
                 │ Connected                                             │
                 │  ├─ VlcNotRunning   (companion up, no VLC → "Start VLC")│
                 │  ├─ Idle(stopped)   (last movie + "Resume")           │
                 │  ├─ Playing / Paused (Now Playing, transport)         │
                 │  └─ Degraded       (3 polls unanswered → spinner in the status line, commands queued ≤ 5 s)│
                 └───────────────────────────────────────────────────────┘
```

Rules: `Degraded` does not kick the user off the screen (Plex); three failed polls in a row → `Connecting` with a 1-2-4-8 s backoff + jitter, no modals; on `401` — a password field right in the status line. A command in `Degraded` is queued for 5 s, otherwise a snackbar "PC is not responding · Retry".

### 5.2. Screens and navigation

```
NavigationBar (3): [▣ Remote] [▤ Library] [≡ Playlist]
   Remote ── sheet: Subtitles/Audio · Chapters · Speed · Aspect/Crop · Sleep timer · ⋯(More: fullscreen, audio output*, display*, VLC quit*)
   Library ── Folder/Grid → Movie details (poster, description, "Play", "From the start", "Queue") → sheet
   Playlist ── reorder/remove/clear, mini player
Separate screens: Connection (list of PCs, manual entry, Bluetooth), Settings, Onboarding (3 steps)
* — only with the companion
```

Navigation — Navigation 3: back stack `[Remote]` by default; the tabs are three root keys with state saving; sheets are a `ModalBottomSheet` without a back-stack entry (closed with predictive back). Deep links: `vlcremote://play?uri=…` (from the share sheet: "Send to VLC on the TV" for YouTube links — the Kore pattern; VLC supports `in_play&input=<url>`).

### 5.3. Onboarding: "Find PC → Pair → Verify"

1. **Find.** Automatic mDNS discovery (the companion's `_vlc-remote._tcp` and/or `_http._tcp` with a `vlc` TXT); show the PCs found with name and IP; "Enter the address manually". On Android 17, before this — request `ACCESS_LOCAL_NETWORK` with an explanation.
2. **Pair.** If there is a companion: it shows a 6-digit PIN/QR on the TV (the QR contains `host, port, token`), the phone scans/enters it → the token is saved in `EncryptedSharedPreferences`/DataStore. Without a companion: a "VLC HTTP interface password" field + a hint with a screenshot of where to enable it (Preferences → All → Main interfaces → Lua → HTTP), and a "Copy the VLC launch command with `--extraintf http --http-password …`" button.
3. **Verify.** A "Pause on the TV" button → we wait for `state` to change → "Works ✓". Then offer: add the tile (`requestAddTileService`), the widget, allow "volume keys control the PC".

### 5.4. Settings

Connection (PC, port, password, companion, Bluetooth fallback, auto-connect on joining Wi-Fi), Controls (volume keys: PC/phone; seek steps 10/30/60/90; gesture surface on/off; left-handed; haptics), Library (root folders, metadata language, "hide watched", reindex), Appearance (theme: system/dark/true-black; dynamic color; keep the screen on in Remote), Background (FGS during playback; "don't restrict battery"), About (TMDB attribution with the logo — mandatory under the terms).

### 5.5. Tone of error messages

Short, human, with an action: "PC is not responding · Retry", "VLC is not running on HTPC-Lounge · Start", "Wrong HTTP interface password · Change", "No Wi-Fi · Check network / Bluetooth". No codes or stack traces in the main text (they go under "Details"). A command error → a snackbar, not a dialog; loss of connection → the status line, not a modal; success — haptics, not a toast.

### 5.6. Low-fidelity mockups (ASCII)

**Remote / Now Playing (portrait)**

```
┌──────────────────────────────────┐
│ ● HTPC-Lounge · 24 ms         ⋮ │ ← status line; tap → Connection
│ ┌──────────────────────────────┐ │
│ │        backdrop (TMDB)       │ │ ← dimmed; gesture surface (opt.)
│ │        dimmed 60 %           │ │
│ └──────────────────────────────┘ │
│ Lord of the Rings: Fellow… 2001  │
│ [1080p] [AC3 5.1 RU] [SRT RU]    │ ← chips → track sheet
│ 00:42:17 ──────●───────── 2:58:25│ ← scrub bar, chapter ticks
│  Ch. 12 "Moria"       −2:16:08   │
│                                  │
│  ⟲10     ◀◀      ▶❚❚     ▶▶  ⟳30 │ ← 64 / 64 / 88 / 64 / 64 dp
│                                  │
│ 🔈 −  ──────────●────── +  52 %  │ ← PC volume, the number is mandatory
│                                  │
│ [CC Subtitles] [♪ Audio] [⏱ 1.0×]│ ← quick actions → sheets
│ [▭ 16:9]  [⏾ Timer]  [⋯ More]    │
│  ▣ Remote  ▤ Library  ≡ Playlist │
└──────────────────────────────────┘
```

`Idle/stopped` state: the same screen, the poster of the last movie, instead of the transport — "▶ Resume from 00:42" and "From the start". `Disconnected` state: the screen stays, on top of the content — a card "No connection to HTPC-Lounge · Connect".

**Remote (landscape, phone; height = compact)**

```
┌───────────────────────────────────────────────────────────────┐
│ ● HTPC-Lounge        Lord of the Rings: Fellow… 2001        ⋮ │
│ ┌──────────┐  00:42:17 ─────●──────────── 2:58:25  Ch. 12     │
│ │  poster  │   ⟲10    ◀◀    ▶❚❚    ▶▶   ⟳30                   │
│ │          │  🔈 − ────────●──────── + 52 %  [CC] [♪] [⏱] [▭]  │
│ └──────────┘                                                  │
└───────────────────────────────────────────────────────────────┘
```

**Library (grid + folders)**

```
┌──────────────────────────────────┐
│ ← D:\shared › Movies      🔍  ⊞☰ │ ← breadcrumbs; grid/list toggle
│ ┌────────────────────────────┐   │
│ │ ▶ Continue: Dune (2021)    │   │ ← resume card, 37 %
│ │   00:58 of 2:35       ▬▬▬░ │   │
│ └────────────────────────────┘   │
│ ┌──────┐ ┌──────┐ ┌──────┐       │
│ │poster│ │poster│ │poster│       │ ← 2:3, w342
│ │      │ │      │ │ ▬▬░░ │       │ ← progress bar
│ └──────┘ └──────┘ └──────┘       │
│  Dune     Matrix    Interst…     │
│  2021     1999      2014         │
│ ┌──────┐ ┌──────┐ ┌──────┐       │
│ │ 📁   │ │poster│ │  ▶   │       │ ← "Series" folder; a file without metadata
│ │ 12   │ │      │ │ .mp4 │       │
│ └──────┘ └──────┘ └──────┘       │
│  Series   Oppenh…   IMG_2041.mp4 │
│                              [D] │ ← fast scroll with a letter
│  ▣ Remote  ▤ Library  ≡ Playlist │
└──────────────────────────────────┘
```

Long-press on a card → sheet: ▶ Play · ▶ From the start · + Queue · ⓘ Details · 👁 Mark as watched.

**Playlist**

```
┌──────────────────────────────────┐
│ Playlist · 3            🔀 🔁 🗑 │
│ ▶ 1  Dune (2021)            2:35 │ ← current: accent bar on the left
│ ≡ 2  Dune: Part Two         2:46 │ ← ≡ drag handle; swipe ← to remove
│ ≡ 3  Making-of.mkv          0:48 │
│                                  │
│          (empty → "Queue empty.  │
│           Pick a movie in the    │
│           Library")              │
│                                  │
│ ┌────────────────────────────┐   │
│ │ ▶❚❚ Dune · 00:42 / 2:35  ⟳30│   │ ← mini player; tap → Remote
│ └────────────────────────────┘   │
│  ▣ Remote  ▤ Library  ≡ Playlist │
└──────────────────────────────────┘
```

**"Subtitles and audio" sheet**

```
┌──────────────────────────────────┐
│ ━━━━                              │
│ [ Subtitles ]   [ Audio ]         │ ← two tabs (or two sections)
│ ─────────────────────────────────│
│ ○ Off                             │
│ ● Russian · Mosfilm (SRT)         │ ← language first, codec in grey
│ ○ Russian · forced (PGS)          │
│ ○ English (PGS)                   │
│ ○ 3 more…                         │
│ ─────────────────────────────────│
│ Subtitle delay           −0.50 s  │
│ [ −500 ] [ −50 ]  [ +50 ] [ +500 ]│ ← ms; SegmentTick haptics
│ [ Reset ]   [ Remember for file ] │
│ ─────────────────────────────────│
│ + Add .srt file next to the video │
│ + Find subtitles online   (later) │
└──────────────────────────────────┘
   "Audio" tab: ● Dub Mosfilm · AC3 5.1 · RU / ○ Original · DTS-HD · EN / …
   Audio delay [−50][+50]; "Audio output: HDMI (TV) ▾" — only with the companion (RC adev)
```

**Connection / pairing**

```
┌──────────────────────────────────┐
│ ← Connection                     │
│ On Wi-Fi network "Home":         │
│ ┌────────────────────────────┐   │
│ │ 🖥 HTPC-Lounge 192.168.1.20│ ▶ │ ← found via mDNS
│ │    VLC 3.0.23 · companion ✓│   │
│ └────────────────────────────┘   │
│ ┌────────────────────────────┐   │
│ │ 🖥 DESKTOP-7F  192.168.1.31│ ▶ │
│ │    VLC not running         │   │
│ └────────────────────────────┘   │
│   ⟳ Searching…                    │
│ ─────────────────────────────────│
│ Add manually                     │
│  Host [192.168.1.20]  Port [8080]│
│  HTTP password [••••••] [Verify] │
│ ─────────────────────────────────│
│ Bluetooth (fallback)         Pair│
│  Paired: HTPC-Lounge             │
│ ─────────────────────────────────│
│ ① Find  ② Password/PIN  ③ Test ✓ │ ← onboarding progress
└──────────────────────────────────┘
```

---

## 6. Testing and tooling

### 6.1. Compose tests

- `createComposeRule()` / `createAndroidComposeRule<MainActivity>()`, the semantic matchers `onNodeWithText/Tag`, `useUnmergedTree` for composite buttons; `ui-test-manifest` in debug ✅ [[docs]](https://developer.android.com/develop/ui/compose/testing/common-patterns). Test: button state in `Disconnected/Degraded`, the time format, track chips, the delay sheet (±50 → the `subdelay&val=…` command into a fake repository).
- **JVM level:** Robolectric + **Roborazzi** for Compose screenshots in the dark/true-black theme and in landscape; Paparazzi is an alternative without Robolectric ✅ [[resumegeni guide]](https://resumegeni.com/blog/android-engineer/testing-and-ci) (a secondary source).
- **Compose Preview Screenshot Testing** (`@PreviewTest`, an HTML diff report): AGP ≥ 9.0, Kotlin ≥ 2.2.10, the plugin is in alpha ✅ [[docs]](https://developer.android.com/studio/preview/compose-screenshot-testing) — handy for "golden" mockups from §5.6.
- Logic: `:core:protocol` and `:core:naming` — plain JUnit/kotlin-test without Android; Ktor's `MockEngine` for the `status.json` parser (including `fullscreen` as `true/false/0`, an empty `equalizer: []`, missing fields in `stopped` — see `live-probe.md`).

### 6.2. A fake VLC for development

The goal is to start it locally in a second and reproduce the **real quirks of VLC 3.0.23**, not an idealized API. A Ktor server variant (the same stack, it can be embedded into the debug build as a "demo mode"):

```kotlin
// tools/fake-vlc/src/main/kotlin/Main.kt — a sketch
fun main() = embeddedServer(CIO, port = 8080) {
    install(Authentication) { basic("vlc") { realm = "VLC stream"
        validate { if (it.name == "" && it.password == "probe") UserIdPrincipal("vlc") else null } } }
    val state = FakeVlcState()                       // volume 0..512, time/length, playlist, tracks "Stream N"
    routing { authenticate("vlc") {
        get("/requests/status.json") {
            call.parameters["command"]?.let { state.apply(it, call.parameters) }   // pl_pause, seek, volume, rate, subdelay…
            call.respondText(state.statusJson(), ContentType.Text.Plain)           // yes, text/plain — just like VLC
        }
        get("/requests/playlist.json") { call.respondText(state.playlistJson(), ContentType.Text.Plain) }
        get("/requests/browse.json")   { call.respondText(state.browseJson(call.parameters["uri"]!!), ContentType.Text.Plain) }
        get("/art")                    { call.respondFile(File("tools/fake-vlc/art.jpg")) }
    } }
}.start(wait = true)
```

What `FakeVlcState` must emulate (from `live-probe.md`): `volume&val=+10` → 266; `seek&val=+30`/`-10`/`1000` (percentages do not work, as in real life); return `rate` with "quantization" `1.5015…`; `fullscreen` — a boolean while playing, `0` in `stopped`; in `stopped` remove `information/stats`, `currentplid=-1`; `audio_track/subtitle_track&val=<StreamN>`; `pl_pause` from `stopped` starts the last item; an unknown command → `200` without an error; for the first 500 ms after start — an empty response (for testing retries); `401` with `WWW-Authenticate: Basic realm="VLC stream"`. Response samples — `samples/status-playing.json`, `samples/status-stopped.json`, `samples/playlist.json`. Parameterize the response delay (0/50/500 ms) and "losing" every Nth request to test `Degraded`. An alternative in Python (`http.server`, ~80 lines) — if it needs to run on a PC without a JDK; but the Ktor variant reuses our own DTOs.

For the companion — a second fake: a WebSocket echo with push events (`{"type":"status","state":"playing",...}`), to debug the reactive UI without a PC.

### 6.3. Emulator networking

- Host = `10.0.2.2`; `localhost` inside the emulator is the emulator itself ✅ [[emulator]](https://developer.android.com/studio/run/emulator-networking-address). A fake VLC on the host at `:8080` → in the app `http://10.0.2.2:8080`. Alternatively `adb reverse tcp:8080 tcp:8080` → `127.0.0.1:8080` (not verified in this context, a standard command).
- **Cleartext HTTP** to VLC (it cannot do TLS) → a `network_security_config` with `cleartextTrafficPermitted="true"` for local ranges, not globally.
- mDNS does not work in the emulator (isolation) — in the debug build, a "demo PC" with a fixed address.
- The Windows firewall may block incoming connections to the Ktor fake from the emulator — allow the port on first launch.

### 6.4. Real device: Bluetooth, volume, background

The emulator does not support Bluetooth (`BluetoothAdapter` is null). Checklist on a real phone (at least two: a Pixel/stock Android 15–17 and a Samsung/One UI):
1. RFCOMM: connecting to the companion's SPP server, a disconnect (turn off BT on the PC) → `read() == -1`/`IOException`, reconnect.
2. Volume in the foreground: `dispatchKeyEvent`, auto-repeat, the "PC/phone" toggle.
3. Volume with the screen off: right after turning the screen off; after 5 min; after 40 min (Doze); with `force-idle`; after backgrounding another media app (who is the "top" session).
4. The MediaSession notification on the lock screen: buttons, progress, artwork; Bluetooth headset play/pause.
5. FGS `connectedDevice` on Android 14/15/16/17: start from the UI, stop on timeout, behaviour when the process is killed.
6. Android 17 with targetSdk 37 on a test build: the `ACCESS_LOCAL_NETWORK` dialog, mDNS, HTTP to 192.168.x.x; `am compat enable RESTRICT_LOCAL_NETWORK` on Android 16.
7. The Glance widget on Android 17: the `RemoteViews` limit with a poster.
8. Landscape on a phone and a tablet (sw600dp with `screenOrientation` ignored).

### 6.5. CI

GitHub Actions: `./gradlew :core:*:test :app:testDebugUnitTest :app:verifyRoborazziDebug`; screenshot diffs as artifacts; `lint`; an APK build with R8 and a size report (`apkanalyzer apk file-size`). Instrumented tests — manually on a device (Bluetooth/volume cannot be automated).

---

## 7. Recommendations

### 7.1. Stack decision
Stay on **Kotlin + Jetpack Compose**, move the protocol and the name parser into pure Kotlin modules (CMP-ready), do **not** move to Flutter/RN/MAUI/CMP UI. targetSdk 36 now; prepare a branch with 37 to check `ACCESS_LOCAL_NETWORK` and the audio restrictions.

### 7.2. Libraries and versions (October 2026)
`kotlin 2.4.21` · `agp 9.4.0` · `compose-bom 2026.09.00` (material3 1.4.0) · `material3-adaptive 1.3.0` · `navigation3 1.2.0` · `media3 1.11.1` (session) · `ktor 3.6.0` (client-okhttp, websockets, content-negotiation, serialization-kotlinx-json) · `okhttp 5.5.0` · `kotlinx-serialization-json 1.11.0` · `kotlinx-coroutines 1.11.0` · `ksp 2.3.12` · `koin 4.2.2` (koin-androidx-compose, koin-compose-navigation3) · `room 2.8.5` · `datastore 1.2.1` · `coil 3.6.3` (coil-compose, coil-network-okhttp) · `glance 1.2.0` (glance-appwidget, glance-material3) · tests: `roborazzi` (version — to be checked), Robolectric, `ui-test-junit4`, `ui-test-manifest`. Material 3 Expressive — after 1.5.0 stable.

### 7.3. UX principles
1. The remote works **without looking**: a large play/pause at the bottom, ±10/±30 buttons with auto-repeat, haptics on every "tick".
2. Volume — relative gestures + a number; mute is separate; the phone keys control the PC (configurable), including with the screen off, where the platform allows it.
3. The connection **never kicks the user** off the screen: a status line, a quiet reconnect, a 5 s command queue, a snackbar instead of a dialog.
4. Now Playing stays even after `stop` ("Resume from 00:42").
5. Tracks and delays — one sheet, language first, ±50/±500 ms, "remember for this file".
6. A library with posters and "Continue watching"; files without metadata do not break the grid; fast scroll and search.
7. Dark theme by default, a true-black option; dynamic color with a contrast check.
8. Landscape and tablet — a separate layout (the platform will force it anyway).
9. Onboarding in 3 steps, the companion enables the HTTP interface itself and shows a PIN/QR; without the companion — honest instructions with the password.
10. Remove nothing silently and do not hide numbers (Sonos); test long-press and auto-repeat (Google TV).
11. TMDB attribution — in "About" and on the movie details screen; metadata cache ≤ 6 months.

### 7.4. MVP screen set
1. **Remote / Now Playing** (portrait + landscape) with transport, volume, a scrub bar, track chips.
2. **"Subtitles and audio" sheet** (selection + delays).
3. **"More" sheet**: chapters, speed, aspect/crop, fullscreen, sleep timer.
4. **Library**: folders (`browse.json`) + a poster grid (TMDB via the companion or on the phone) + "Continue".
5. **Playlist** with a mini player.
6. **Connection** (mDNS + manual entry + password) and 3-step **onboarding**.
7. **Settings** (minimum: volume keys, seek steps, theme, FGS).
8. **Background:** `MediaSessionService` + FGS `connectedDevice`, a notification with controls; a play/pause **QS tile**.
Deferred: the Glance widget (v1.1), the Bluetooth fallback (v1.1, after the companion), a Wear OS tile (v2), subtitle download via the companion (v1.x), the gesture surface (an experiment behind a flag).

---

## 8. Sources

Versions: [Kotlin releases](https://github.com/JetBrains/kotlin/releases) · [AGP release notes](https://developer.android.com/build/releases/gradle-plugin) · [Compose BOM mapping](https://developer.android.com/develop/ui/compose/bom/bom-mapping) · [Compose Material 3 releases](https://developer.android.com/jetpack/androidx/releases/compose-material3) · [material3-adaptive](https://developer.android.com/jetpack/androidx/releases/compose-material3-adaptive) · [Navigation 3](https://developer.android.com/jetpack/androidx/releases/navigation3) · [Media3](https://developer.android.com/jetpack/androidx/releases/media3) · [Ktor releases](https://github.com/ktorio/ktor/releases) · [OkHttp CHANGELOG](https://github.com/square/okhttp/blob/master/CHANGELOG.md) · [kotlinx.serialization](https://github.com/Kotlin/kotlinx.serialization/releases) · [kotlinx.coroutines](https://github.com/Kotlin/kotlinx.coroutines/releases) · [KSP releases](https://github.com/google/ksp/releases), [KSP PR #3107](https://github.com/google/ksp/pull/3107) · [Dagger releases](https://github.com/google/dagger/releases) · [Koin releases](https://github.com/InsertKoinIO/koin/releases) · [Room](https://developer.android.com/jetpack/androidx/releases/room), [Room 3](https://developer.android.com/jetpack/androidx/releases/room3) · [DataStore](https://developer.android.com/jetpack/androidx/releases/datastore) · [Coil releases](https://github.com/coil-kt/coil/releases), [Coil network docs](https://coil-kt.github.io/coil/network/) · [Glance](https://developer.android.com/jetpack/androidx/releases/glance) · [Wear Compose](https://developer.android.com/jetpack/androidx/releases/wear-compose) · [Compose Multiplatform releases](https://github.com/JetBrains/compose-multiplatform/releases), [CMP 1.8.0 blog](https://blog.jetbrains.com/kotlin/2025/05/compose-multiplatform-1-8-0-released-compose-multiplatform-for-ios-is-stable-and-production-ready/) · [MaterialExpressiveTheme](https://composables.com/material3/materialexpressivetheme) · [Flutter what's new](https://docs.flutter.dev/release/whats-new.html) · [Expo New Architecture](https://docs.expo.dev/guides/new-architecture.md) · [MAUI support policy](https://dotnet.microsoft.com/ru-ru/platform/support/policy/maui) · [Plugin.BLE](https://www.nuget.org/packages/plugin.ble) · [flutter_classic_bluetooth](https://pub.dev/packages/flutter_classic_bluetooth/versions/0.1.4) · [flutter_bluetooth_serial](https://github.com/edufolly/flutter_bluetooth_serial) · [react-native-bluetooth-classic](https://www.jsdelivr.com/package/npm/react-native-bluetooth-classic) · [APK size comparison (Medium, 2023)](https://medium.com/@jacobras/android-ios-native-vs-flutter-vs-compose-multiplatform-7ef3d5ec2a56).

Platform: [FGS types](https://developer.android.com/develop/background-work/services/fgs/service-types) · [Android 14 FGS types required](https://developer.android.com/about/versions/14/changes/fgs-types-required) · [Android 15 behavior changes](https://developer.android.com/about/versions/15/behavior-changes-15) · [Android 16 all apps](https://developer.android.com/about/versions/16/behavior-changes-all), [Android 16 targeting](https://developer.android.com/about/versions/16/behavior-changes-16) · [Android 17 all apps](https://developer.android.com/about/versions/17/behavior-changes-all), [Android 17 targeting](https://developer.android.com/about/versions/17/behavior-changes-17) · [Local network permission](https://developer.android.com/privacy-and-security/local-network-permission) · [NsdManager](https://developer.android.com/reference/kotlin/android/net/nsd/NsdManager) · [BluetoothSocket](https://developer.android.com/reference/android/bluetooth/BluetoothSocket) · [VolumeProvider](https://developer.android.com/reference/android/media/VolumeProvider.html) · [MediaSession](https://developer.android.com/reference/android/media/session/MediaSession) · [MediaSessionLegacyStub.java](https://github.com/androidx/media/blob/release/libraries/session/src/main/java/androidx/media3/session/MediaSessionLegacyStub.java) · [Android 12 cast volume regression (esper.io)](https://esper.io/blog/android-12-cast-volume-changes-explained) · [Poweramp screen-off volume](https://forum.powerampapp.com/topic/21716-volume-keys-control-does-not-work-when-the-screen-is-off/) · [Nest community: lock screen volume](https://www.googlenestcommunity.com/t5/Streaming/Enhancing-Chromecast-Experience-Simplifying-Media-Volume-Control-during/m-p/462235) · [Intercepting key events](https://code.tutsplus.com/android-sdk-intercepting-physical-key-events--mobile-10379t) · [Quick Settings tiles](https://developer.android.com/develop/ui/views/quicksettings-tiles), [API 33 StatusBarManager](https://developer.android.com/sdk/api_diff/33/changes/android.app.StatusBarManager) · [Doze](https://developer.android.com/training/monitoring-device-state/doze-standby) · [HapticFeedbackType](https://developer.android.com/reference/kotlin/androidx/compose/ui/hapticfeedback/HapticFeedbackType) · [Wear tiles codelab](https://developer.android.com/codelabs/wear-tiles) · [RfcommServiceProvider](https://msdn.microsoft.com/library/windows/apps/dn263511), [32feet](https://inthehand.com/components/32feet/) · [Emulator networking](https://developer.android.com/studio/run/emulator-networking-address) · [Adaptive layouts (ProAndroidDev)](https://proandroiddev.com/adaptive-compose-layouts-86b7f1e51338).

UX: [Kore F-Droid](https://f-droid.org/en/packages/org.xbmc.kore/), [Kore 3.0](https://kodi.tv/article/kore-3-0), [Kore issues](https://github.com/xbmc/Kore/issues), [Kore 3.0 feedback](https://forum.kodi.tv/showthread.php?tid=369711), [Kore README](https://github.com/xbmc/Kore) · [Plex Remote control API](https://github.com/plexinc/plex-media-player/wiki/Remote-control-API), [Plex Android 7.14](https://apkmirror.com/apk/plex-inc/plex/plex-7-14-1-9954-release), [Plex companion thread](https://forums.plex.tv/t/feature-request-supported-server-mediated-api-for-controlling-remote-plex-plexamp-sessions/942315) · [Jellyfin remote (Symfonium)](https://support.symfonium.app/t/allow-symfonium-to-control-jellyfin-on-other-devices/7973) · [Google TV M3 Expressive](https://9to5google.com/2025/08/23/google-tv-app-material-3-expressive/), [Android TV remote review](https://www.techhive.com/article/582309/the-android-tv-remote-app-for-ios-is-as-bare-bones-as-the-android-version.html) · [Apple TV remote](https://www.howtogeek.com/720922/how-to-use-your-iphone-or-ipad-as-an-apple-tv-remote/), [Apple HIG remotes](https://developer-mdn.apple.com/design/human-interface-guidelines/inputs/remotes) · [Infuse subtitles](https://support.firecore.com/hc/en-us/articles/215090907), [Infuse subtitle delay](https://community.firecore.com/t/subtitle-delay/14737.md), [Infuse 2x](https://community.firecore.com/t/temporary-2x-speed-on-apple-tv-like-ios-has/57701.md) · [Unified Remote App Store](https://apps.apple.com/us/app/825534179), [Softpedia](https://mobile.softpedia.com/android/Unified-Remote-Android-Review-2859.html) · [KDE Connect bug 390414](https://bugs.kde.org/show_bug.cgi?id=390414), [D12559](https://phabricator.kde.org/D12559) · [Sonos (CMSWire)](https://www.cmswire.com/customer-experience/sonos-app-redesign-fail-customer-outrage-and-falling-stock-prices), [Sonos forum volume](https://en.community.sonos.com/controllers-and-music-services-229131/apps-fiddly-volume-control-6933330), [Sonos 2026 update](https://www.ecoustics.com/news/sonos-app-update-2026/) · [VLC Mobile Remote (App Store)](https://apps.apple.com/us/app/vlc-mobile-remote/id1140931401), [VLC Remote Pro (App Store)](https://apps.apple.com/us/app/id297244048), [VLC Remote reviews (marlvel)](https://marlvel.ai/apps/vlc-remote/reviews), [VideoLAN forum 2013](https://forum.videolan.org/viewtopic.php?p=392423), [VideoLAN forum 2015](https://forum.videolan.org/viewtopic.php?p=425210), [android-vlc-remote](https://github.com/samicemalone/android-vlc-remote) · [Hoober grips (A List Apart)](https://alistapart.com/1-2-phone-grips-jpg/), [Osmani touch design](https://addyosmani.com/blog/touch-friendly-design/) · [Dark theme with MDC](https://medium.com/androiddevelopers/dark-theme-with-mdc-4c6fc357d956).

Metadata: [VLC HTTP requests README](https://github.com/videolan/vlc/tree/master/share/lua/http/requests) · [guessit properties](https://guessit.readthedocs.io/en/latest/properties.html) · [parse-torrent-title](https://pypi.org/project/parse-torrent-title/2.4/), [jhin](https://pkg.go.dev/github.com/dreulavelle/jhin), [torrent-name-parser (Java)](https://central.sonatype.com/artifact/io.github.mdaubie/torrent-name-parser) · [Emby.Naming VideoResolver](https://dev.emby.media/reference/pluginapi/Emby.Naming.Video.VideoResolver.html), [Jellyfin naming diff](https://gitea.hptrow.me/pt/jellyfin/compare/53d432852c9380e57f36adfebd4432c743028127...84f0c5fbe1a5f0352bba0396abff7ae5e3123ad6), [Emby "se" bug](https://emby.media/community/topic/20947-se-keyword-for-home-videos/) · [TMDB API terms](https://www.themoviedb.org/api-terms-of-use), [TMDB rate limiting](https://developer.themoviedb.org/docs/rate-limiting), [TMDB image basics](https://developer.themoviedb.org/docs/image-basics), [TMDB search movie](https://developer.themoviedb.org/reference/search-movie), [TMDB config sample](https://community.kde.org/Macaw-Movies/TMDB-json-responses), [TMDB sizes talk](https://themoviedb.org/talk/5a6986550e0a260d6400d6c0) · [OpenSubtitles MCP README](https://glama.ai/mcp/servers/@opensubtitles/mcp.opensubtitles.com/blob/607a9eb0ecd1d2e908642f9ecf5dd4335d997db0/README.md), [OpenSubtitles limits (Reddit 2024)](https://redlib.hbubli.cc/r/learnprogramming/comments/1bd0rxx/using_subtitles_api), [VLsub VIP wall](https://forum.videolan.org/viewtopic.php?p=545241), [Mintlify OS guide](https://www.mintlify.com/logscore/plank/integrations/opensubtitles) · [Subdl via cloudstream](https://gitdab.com/recloudstream/cloudstream/commit/469a71236b6e78018f3f72c83f0b051bdab189c3), [Subdl limit (tMM)](https://gitlab.com/tinyMediaManager/tinyMediaManager/-/issues/2631).

Tests: [Compose testing patterns](https://developer.android.com/develop/ui/compose/testing/common-patterns) · [Compose Preview Screenshot Testing](https://developer.android.com/studio/preview/compose-screenshot-testing) · [Android testing guide 2026 (secondary)](https://resumegeni.com/blog/android-engineer/testing-and-ci).
