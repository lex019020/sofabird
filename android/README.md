# Sofabird — Android app

A remote for VLC 3.0.x running on a Windows PC at the TV. It works in two modes: directly with VLC's
HTTP interface (levels 0/1) and through the Windows companion (level 2) — over Wi‑Fi (WebSocket) or, if
Wi‑Fi is unavailable, over Bluetooth (RFCOMM).
Protocol — [`../protocol/README.md`](../protocol/README.md) and [`../protocol/vlc-http.md`](../protocol/vlc-http.md).

## Build

Requirements: JDK 21, Android SDK with `platforms;android-35` and `build-tools;35.0.0`, access to Maven Central/Google.

```powershell
cd android
$env:JAVA_HOME = 'C:\Program Files\Android\openjdk\jdk-21.0.8'   # any JDK 17/21
# local.properties (not committed): sdk.dir=D\:\\Android\\android-sdk
.\gradlew.bat :app:assembleDebug      # APK: app\build\outputs\apk\debug\app-debug.apk
.\gradlew.bat test                    # unit tests of both modules (and the screenshot tests)
.\gradlew.bat detekt                  # static analysis + formatting (--auto-correct fixes the formatting)
.\gradlew.bat :app:verifyRoborazziDebug   # compare the screenshots; recordRoborazziDebug updates them
```

**Screenshot tests** (Roborazzi + Robolectric, `app/src/test/.../ui/*ScreenshotTest.kt`, images in
`app/src/test/screenshots`): every screen and the shared controls in English and Russian at font scale 1.0 and 2.0
on a phone-sized screen. Screens are written as a thin stateful `XxxScreen(viewModel)` and a stateless
`XxxContent(ui, actions)` (an `XxxActions` interface with no-op defaults), so the tests render them with fake state.

Stack: Gradle 8.6 (wrapper), AGP 8.4.2, Kotlin 2.0.21, Compose BOM 2024.10.01 (Material 3),
Ktor 3.0.1 (OkHttp 4.12 engine), kotlinx.serialization 1.7.3, Koin 4.0.0, Media3 1.4.1,
DataStore 1.1.1, Coil 3.0.4, security‑crypto 1.1.0‑alpha06. `compileSdk`/`targetSdk` 35, `minSdk` 26.

## Build variants and signing

- `debug` — `app.sofabird.debug`, "Sofabird (dev)", debug key; installs side by side with the main app.
- `release` — `app.sofabird`, signed with the shared key from `android/keystore.properties` (not in git) or the
  variables `SOFABIRD_KEYSTORE_FILE`/`SOFABIRD_KEYSTORE_PASSWORD`/`SOFABIRD_KEY_ALIAS`/`SOFABIRD_KEY_PASSWORD`. This is how test
  builds and releases are built in CI; see CLAUDE.md "APK signing".
- Version: `versionName` from the `VERSION` file + `-Psofabird.versionSuffix=beta0042`, `versionCode` from
  `-Psofabird.versionCode=<number of commits>`. Shown in "Settings → About".

## Structure

```
android/
  core/protocol/        pure Kotlin/JVM, no Android dependencies (app.sofabird.protocol)
    model/              PlayerState and its parts (Playback, Tracks, Chapters, Volume…), Capabilities,
                        ConnectionState/ConnectionError, Entry, PlaylistItem
    backend/            PlayerBackend (interface), PlayerCommand, SystemCommand
    vlc/                VlcStatusParser (status/playlist/browse/tracks.json), VlcStateMapper,
                        VlcCommands (request strings), VlcErrorPage (detects "Error loading")
    companion/          CompanionMessage (sealed, discriminator `t`), CompanionCodec,
                        CommandMapping (PlayerCommand → cmd/sys), NdjsonFramer/NdjsonReader (NDJSON
                        lines over the RFCOMM byte stream, ≤ 1 MiB), BtAddress, COMPANION_RFCOMM_UUID
    src/test/           tests on fixtures from docs/research/samples (copied into resources)
  app/                  Android app (app.sofabird)
    data/vlc/           VlcHttpApi, VlcErrorClassifier, VlcHttpBackend (polling, commands, levels 0/1)
    data/companion/     CompanionTransport (+ handshake over any channel), WebSocketTransport,
                        RfcommTransport, BluetoothSupport (permission, radio, bonded devices),
                        AndroidCompanionTransports (WS / RFCOMM / LAN probe), CompanionBackend (level 2,
                        channel choice, return to Wi‑Fi)
    data/discovery/     NsdDiscovery, UdpBeaconDiscovery, SubnetScanner, LocalNetwork, DiscoveryRepository
    data/history/       resume / next-episode prompts shared by the screens and the notification
    data/player/        PlayerController (the single owner of the active backend), ConnectionTester, AppVisibility,
                        PairingFlow (connecting/pairing with the companion) + CompanionPairingLink, DeviceIdentity
    data/servers/       ServerRepository, CompanionServerSync (LAN/Bluetooth addresses from discovery → saved PCs)
    data/settings|secrets|net
    service/            PlaybackService (FGS + MediaSession), RemotePlayer (SimpleBasePlayer), ServiceCoordinator
    domain/permissions/ PermissionRegistry: the single list of runtime permissions (see "Runtime permissions")
    ui/                 AppRoot (navigation, status bar), remote/, connection/, library/, history/, playlist/,
                        settings/, permissions/ (first-run screen, Settings → Permissions, shared card and state),
                        common/ (shared controls: LabelText, RemoteButton, formatters, Bluetooth requests)
    di/AppModule.kt     Koin
```

## Where secrets are stored

- The VLC HTTP interface password and the companion token — `EncryptedSharedPreferences` (`secrets`, AES‑256 via
  the Android Keystore), the key is the id of the saved PC (for companions found on the network this is the
  companion's installation GUID from the beacon/`welcome`). If the Keystore is unavailable on the device, `SecretStore`
  falls back to plain SharedPreferences (`secrets_fallback`) and writes a warning to the log.
- The list of PCs (without secrets) and the settings — DataStore (`servers`, `settings`).
- `local.properties` with the SDK path is not committed.

## How the modes and levels work

| Mode | What runs on the PC | How it is detected | What the remote can do |
|---|---|---|---|
| Direct, level 0 | VLC with the web interface enabled and a password | `GET /requests/tracks.json` → 404 | transport, volume, chapters by number, speed, delays, track list (from `Stream N`), file browsing, playlist, artwork |
| Direct, level 1 | + our Lua pages in `%APPDATA%\vlc\lua\http` | `tracks.json` → 200 | + current track, chapter names |
| Companion, level 2 | tray app on Windows | `welcome.caps` | + discovery, push state, start/close VLC, "keep awake", sleep, display, system volume, audio device |

**Direct mode** (`VlcHttpBackend`): Basic auth with an empty user name; an OkHttp network interceptor
removes the `Connection` header (otherwise VLC drops the connection after every request); polling every 1 s during
playback, 3 s when paused/stopped; polling stops when the app is not visible and there is no
foreground service; one request in flight per type (status/playlist/browse), sequence numbers,
stale responses are discarded; the response to a command is applied at once, a "clean" status follows after 500 ms;
the position is interpolated locally (`PlayerState.interpolatedTime`). Mute is emulated (we remember the
volume and set 0). At level 0 the current track is remembered on the phone and reset when
`currentplid` changes. Errors are told apart: 403 "password not set", 401 "wrong password", refused,
timeout, empty body, a Lua page with `<title>Error loading`.

**Companion** (`CompanionBackend`): `ws://host:47555/ws`, `hello` with the token, `welcome` with `caps`,
push `state`, requests `cmd`/`sys`/`browse`/`getPlaylist` by `id`, `ping` every 30 s (no `pong` within 10 s →
reconnect), exponential backoff, commands are buffered for up to 5 s while reconnecting.
`pairing_required`/`unauthorized` during the handshake are terminal (`ConnectionError.PairingRequired` /
`InvalidPairCode`): the backend does not hammer the PC with retries, and the "Connection" screen asks for a code.

**Bluetooth (M3)** — a fallback channel to the companion when there is no Wi‑Fi or the PC does not answer over it:

- The protocol is the same: RFCOMM service `47fe5bd6-98ad-4d9f-9812-174b3394037d`, NDJSON (one UTF‑8 line per message,
  separator `\n`, ≤ 1 MiB), the same `hello` with the token. The socket is a *secure* `createRfcommSocketToServiceRecord`
  (the PC must be paired, the channel is encrypted), the RFCOMM channel is looked up via SDP. `read() == -1` (Android 17)
  and `IOException` mean the link is broken. Blocking `connect()`/`read()` run on `Dispatchers.IO`; cancelling the
  coroutine closes the socket so that they return.
- The PC's Bluetooth address (`bt` from `welcome`, the beacon and `/info`) is stored in `SavedServer.btAddress` and
  updated on every `welcome`; the beacon/`/info` of the same companion (by GUID) adds the LAN or Bluetooth address to
  the saved PC (`CompanionServerSync`). Addresses are never erased: a beacon without `bt` (Bluetooth is off on the PC)
  does not remove a known address.
- Channel choice on every (re)connect (`CompanionBackend`): WebSocket first (timeout ~3 s); if that fails, the
  Bluetooth address is known, the setting "Use Bluetooth when Wi‑Fi is unavailable" is on (the default), Bluetooth is
  on, the permission is granted and the PC is among the bonded devices → RFCOMM (up to 12 s). A pairing refusal or a
  different protocol over the LAN is a final answer; it is not re-checked over Bluetooth. A PC known only via
  Bluetooth (no LAN address yet) is connected over RFCOMM right away regardless of the setting.
- On RFCOMM `ping` is sent every 15 s (on WebSocket — 30 s), no `pong` within 10 s → reconnect. Every ~20 s the phone
  checks the LAN: `GET /info` at the known address (2 s), otherwise a short UDP beacon round looking for the GUID/`bt`
  (this is how the phone learns the IP of a PC paired via Bluetooth). If it answers, a WebSocket is opened, `hello` with the same
  token and `getState` are sent, and only then RFCOMM is closed (responses to requests "in flight" are awaited for up
  to 1 s): the state on screen is not reset and the status never goes to "Connecting…".
- The remote's header shows a "Wi‑Fi" / "Bluetooth" icon next to the PC name, the status line reads
  "Companion · Bluetooth"; a channel switch shows the snackbar "No Wi‑Fi connection to the PC — switched to
  Bluetooth" / "Back on Wi‑Fi".
- Permissions: `BLUETOOTH_CONNECT` (Android 12+, "Nearby devices") is requested on the first-run permissions screen
  (see "Runtime permissions"). In-context requests remain as a fallback and do nothing when it is already granted:
  turning the setting on, the remote's "… is unavailable over Wi‑Fi" card that appears when the LAN is down and
  Bluetooth could help (the card text is the rationale), and "Connect via Bluetooth" (a rationale dialog first).
  A refusal → a dialog linking to the app settings. Radio off → the system `ACTION_REQUEST_ENABLE` dialog ("Turn on").
  After the permission is granted / the radio is on, the reconnect happens at once, without waiting for the backoff.
  `BLUETOOTH`/`BLUETOOTH_ADMIN` are declared only up to Android 11 (`maxSdkVersion="30"`). Neither `BLUETOOTH_SCAN` nor
  location is needed: only bonded devices are used (hence `cancelDiscovery()` is not called on Android 12+ — it needs
  `BLUETOOTH_SCAN`).
- The first connection over Bluetooth: "Other ways to connect" → "Connect via Bluetooth" → the list of bonded
  devices (name + address, computers — `BluetoothClass.Device.Major.COMPUTER` — on top; a "Bluetooth settings"
  button, the list is re-read on return) → `hello` over RFCOMM → the same `PairingFlow`: a code on the PC screen, the
  code dialog on the phone, a token. The saved PC is created with id = the companion GUID, `btAddress` and an empty
  `host`; as soon as this companion's beacon/`/info` is seen on the LAN, the address is added to the same record (the
  same token). If the PC was already paired over Wi‑Fi, `welcome.id` finds the existing record — no second one
  appears. An RFCOMM error — "The companion is not answering over Bluetooth: check that the PC is paired with the
  phone and the companion is running".
- Tapping a saved PC while the LAN is unavailable: `hello` over the LAN, then (if Bluetooth is ready) over RFCOMM.
- Background: the `connectedDevice` foreground service also fits Bluetooth. While a reconnect is in progress (Wi‑Fi
  lost → RFCOMM), `ServiceCoordinator` keeps the service alive for up to 2 minutes: a service stopped in the background
  cannot be started again, and the switch to Bluetooth would be lost with it.

**Connecting and pairing** (`PairingFlow`, the "Connection" screen):

1. On top — "Computers nearby": large cards of the discovered companions with the PC name, the VLC state
   (`/info` → `vlc.status`: running / not running / without control / starting / not installed) and
   a "paired" badge if there is a token for this PC.
2. Tapping a card → `hello` with the saved token. Token accepted → straight to the remote. A
   `pairing_required` response (no token, or the PC has forgotten it; an old companion answers `unauthorized`) → the
   token is deleted, **the companion shows a code on the TV/laptop screen by itself**, and the phone shows the "Enter
   the code from the computer screen" dialog: six large cells, the numeric keyboard opens by itself, the code is sent
   on the sixth digit (`hello` with `pairCode`). Success → the token goes to `EncryptedSharedPreferences`, the PC is
   saved, connected, the app switches to the remote, the snackbar "Paired with …". `unauthorized` → "Wrong or
   expired code", the field is cleared, the dialog stays. Cancel closes the dialog.
3. First launch (no saved PCs) and exactly one companion on the network (we wait 1.5 s after the first answer)
   → connecting starts by itself: a "Found computer … — connecting…" card with a "Cancel" button, then the
   code dialog. If PCs are already saved, there is no auto-start: on app launch it auto-connects to the
   last PC (`PlayerController.autoConnectLast`), as before.
4. If the active companion rejected the token, the code dialog appears as soon as the screen opens (and the
   current connection's card has an "Enter code" button).
5. Everything technical — direct VLC mode, address/port, web interface password, subnet scan, web interface
   instructions — is under the collapsed "Other ways to connect" block. It expands by itself only
   if no companion is found within ~6 s of searching. Manually entering a companion goes the same way (the code
   appears on the PC screen).

A PC is matched to a saved one by id, then by the installation GUID (`pcId`; the address is updated if DHCP
gave the PC a different IP), then by address and port.

**VLC state on the remote (companion)**: `vlc.status` (absent in old companions → derived from
`running`). While VLC cannot be controlled, a card replaces the seek bar, the volume and the transport buttons:
`notRunning` — "VLC is not running" + "Launch VLC" (`cmd launch`, only with `caps.launchVlc`);
`starting` — "VLC is starting…"; `noControl` (VLC was opened by hand before the companion configured it) —
"VLC is open without phone control" + "Restart with control" (`cmd launch`, the position in the movie
is not kept); `notInstalled` — "VLC was not found on the computer". After a tap the button shows
progress until the companion reports a new state (up to 20 s). The "More" sheet (Windows volume, sleep,
etc.) is available in these states too. In direct mode the behaviour is unchanged.

**TV screen (companion, `caps.windowControl`)** — one monitor for VLC, YouTube and Moonlight: on the remote, next to the "Audio"/"Subtitles" chips, there is
a "Display: <name>" chip (a TV icon for an external monitor, a laptop icon for the built-in panel). It shows the monitor
where the VLC window is now, or, if VLC is closed, the monitor VLC will open on; monitor names are read (`sys listDisplays`)
right after connecting. The chip works while VLC is closed too. A tap opens the "TV screen" sheet (the "TV screen"
item in "More" leads there as well): the list of monitors — icon, friendly name from EDID, resolution, the "primary" and
"VLC is here" labels; a radio button marks the preferred monitor. Tapping a monitor → `sys moveToDisplay {displayId}`
(haptics, progress in the row): VLC, an open YouTube and a Moonlight stream move there now, and open there from then
on; the chosen monitor is captioned "VLC, YouTube and Moonlight open here". The last item "Do not manage" →
`sys preferredDisplay {displayId: null}`: the companion does not touch the window. The preferred monitor is not connected
(TV off) — an explanation under the list. The list is re-read on opening and after every action; an error is shown as
text in the sheet and as a snackbar. After a successful command the choice is marked at once (optimistically) until the
companion sends a new `state.system` (`displayId`, `preferredDisplayId`); after that it is trusted. The choice logic is
a pure function `displayChoice` (`ui/remote/DisplayChoice.kt`), covered by tests.
Old companions (monitors without `id`): the radio button shows the monitor with VLC, moving is `moveToDisplay {display: N}`
by index and only while VLC is open, there is no "Do not manage" item.

**Discovery**: NSD `_sofabird._tcp` (one discovery session, sequential resolve,
MulticastLock, "loss" with a 3 s delay), UDP beacon `SOFABIRD_DISCOVER 1` → `255.255.255.255:47556`
(plus the subnet's directed broadcast), an optional `/24` scan on port 8080 (48 in parallel,
1 s timeout, addresses closest to our own IP first; 200/401/403 = "VLC is there"), manual entry.
For discovered companions `GET /info` is read as well (VLC state, whether pairing is needed) —
right away and then at most once every 8 s while the screen is open.

**Connection in the UI**: the status is always in the header (dot + level + latency), reconnecting is quiet,
with a snackbar; there are no blocking dialogs (except the pairing code dialog on the "Connection" screen).
Authorization errors (VLC 401/403, the companion's `pairing_required`/`unauthorized`) are terminal: the password
has to be fixed on the "Connection" screen or a code entered.

**Background**: `PlaybackService` is a foreground service of type `connectedDevice` (prerequisite —
`CHANGE_WIFI_MULTICAST_STATE` in the manifest) with a media notification (prev / −10 / play‑pause / +30 / next)
and a Media3 `MediaSession` over `RemotePlayer : SimpleBasePlayer` (volume declared as
`PLAYBACK_TYPE_REMOTE`, 0..100). The service starts when there is a connection and (playback is on
or the app is visible) with the setting "Keep the connection in the background" enabled; it stops
10 minutes after stop/pause, on disconnect or when the setting is turned off. The service is a plain
`LifecycleService`, not a `MediaSessionService`: the latter calls `startForeground` with the
`mediaPlayback` type by itself, which conflicts with the declared `connectedDevice`.

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
  `adb shell am start -S -n <package>/app.sofabird.ui.MainActivity --ez show_permissions true` shows the screen
  again; `adb shell pm revoke <package> android.permission.BLUETOOTH_CONNECT` (and `POST_NOTIFICATIONS`) to start
  from scratch; `adb shell pm clear <package>` resets everything, including pairings.

**Hardware volume keys**: `MainActivity.dispatchKeyEvent`, with the setting enabled and a live
connection, sends `volume&val=` to the PC and shows the result in a snackbar; otherwise the default behaviour applies.

## Wake-on-LAN

The companion names the adapters that will wake it in `welcome.macs`; they are kept with the saved PC
(`SavedServer.wakeMacs`, merged like the Bluetooth address: an absent `macs` keeps them, `[]` — the PC cannot be
woken — clears them, and the remote offers no waking). When the PC does not answer (`RemoteUiState.offline`: a final
error, or a reconnect that failed at least once), the remote's "No connection" card offers **Wake up the PC**
(`PcWaker`) as its first button. A companion going to sleep says so (`event sleeping`): `CompanionBackend` ends the
link at once, keeps `ConnectionError.PcAsleep` as the reason until the PC answers again ("<PC> is asleep"), and
retries after ~8 s, then with its backoff (at most 15 s), in case the PC wakes by itself. Waking sends the magic packet (`MagicPacket`, `:core:protocol`, tested) for every MAC as a
UDP broadcast (`255.255.255.255` and the subnet's, ports 9 and 7), then reconnecting every 4 s and sending again every
15 s, for up to 90 s; the card says "Waking up …" meanwhile and "The PC did not wake up" when it did not. The PC's
side is an item of the companion's setup window.

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

## Known limitations

- Chapter ticks on the seek bar are evenly spaced: VLC 3's HTTP interface does not report chapter offsets,
  only their count and index (names — at level ≥ 1).
- `pl_delete` in VLC is unreliable — the list is re-read after removing an item from the playlist.
- At level 0 the selected track is known only to the phone; if it is switched on the PC, the remote will not see it.
- VLC's `/art` serves artwork only for the current item; the URL carries `?item=<plid>` as the Coil cache key.
- Remote volume through the MediaSession with the screen off depends on the firmware (see research §2.1).
- Android 17 / targetSdk 37 will require the `ACCESS_LOCAL_NETWORK` runtime permission — targetSdk is 35 for now.
- Auto-connect on startup goes to the last PC: LAN, then Bluetooth (if the address is known and the permission is granted).
- Bluetooth pairing (phone ↔ PC) is done in the Android/Windows settings; the app does not initiate it and
  does not search for new devices (no `BLUETOOTH_SCAN`). `CompanionDeviceManager` is not used.
- Artwork and thumbnails are not transferred over Bluetooth (`/art` is HTTP over the LAN).

## Deferred

- `CompanionDeviceManager` (system PC picker, presence events) instead of our own list of bonded devices.
- Wake-on-LAN: a "Wake up the PC" action when the saved PC does not answer (next).
- Sleep timer (next).
- A Quick Settings tile, a Glance widget.
- Landscape and tablet layouts (postponed).

## To check on a device

Automated: unit tests (parsers, commands, companion messages, `VlcHttpBackend` on the Ktor `MockEngine`) and the
screenshot tests. Everything below has been checked on a Galaxy S24 with the TV laptop; repeat the relevant items
after changing that area:

1. NSD: whether `NsdManager` finds the companion service (`_sofabird._tcp`) and resolve works on the target firmware; the UDP beacon as the fallback.
2. The `connectedDevice` foreground service on Android 14/15: start from `ServiceCoordinator`, the notification with buttons, stopping after 10 minutes; the `POST_NOTIFICATIONS` request on Android 13+.
3. MediaSession: headset buttons and the lock screen; volume keys with the screen off (`RemotePlayer`, `PLAYBACK_TYPE_REMOTE`).
4. Volume keys in the foreground (`dispatchKeyEvent`), haptics on buttons.
5. Coil and `/art` with Basic auth (the `ArtAuth` interceptor), artwork in the notification.
6. `EncryptedSharedPreferences` on the target device (the fallback must not kick in).
7. Predictive back in sheets.
8. Pairing: first launch with one companion on the network → auto-start, the code appears on the TV, the code dialog
   on the phone (autofocus, Samsung numeric keyboard, sending on the 6th digit); a wrong code → an error and
   the field cleared; "Cancel"; the token is saved, after an app restart — connecting without a code;
   reconnect after the laptop sleeps; "Forget this computer" → a code again on the next tap.
9. The "More" sheet with the companion: the list of audio devices (`getAudioDevices` → `sys audioDevice`), the "TV screen" item opens the "TV screen" sheet (item 14), "keep awake"/display/sleep (`sys keepAwake|displayPower|sleep`), the Windows volume slider (`sys systemVolume`), "Another PC's screen" with "Show on the TV" / "End streaming" (`sys moonlightStart|moonlightStop`, cap `moonlight`), "YouTube on the TV" with a link field and "Paste" (`sys youtubeOpen`, cap `youtube`) and the "Block ads (uBlock Origin)" and "Skip sponsor segments (SponsorBlock)" switches (`sys youtubeAdBlock|youtubeSponsorBlock`, `state.youtube.adBlock|sponsorBlock`); "Share → Sofabird" from the YouTube app sends the shared text the same way (it waits up to 15 s for the PC); while YouTube is open the remote shows its thumbnail, title, channel, position, the transport buttons and the PC's volume, with "Close YouTube" in the thumbnail's corner, "An ad plays…" over the title during an ad and a "Skip" tile while it can be skipped (`youtube.ad` / `adSkippable`, `skipAd`), "Fullscreen" and the TV screen; the media notification, the lock screen, headset buttons and the volume keys then follow YouTube and the PC's volume (`onTv()` / `routeToYouTube()` in `:core:protocol`) — the sections appear only with the corresponding `caps`.
10. The phone name in the pairing window on the PC (`Settings.Global.DEVICE_NAME`, e.g. "Galaxy S24").
11. VLC states on the remote: VLC closed → "Launch VLC"; VLC opened by hand → "without control" →
    "Restart with control" (the same movie on the TV); the status on the PC card updates (~8–10 s).
12. "Other ways to connect" expands by itself if no companion is found within ~6 s.
13. **Bluetooth** (Galaxy S24 ↔ the TV laptop with Intel BT, already paired):
    1. Connect over Wi‑Fi → `welcome` contains `bt`; in "Saved PCs" the PC row shows "Bluetooth".
    2. Turn off Wi‑Fi on the phone with the remote open → the snackbar "switched to Bluetooth" and the "Bluetooth" icon
       in the header within ~3–5 s; pause, volume, seeking work, the latency is acceptable to the eye.
    3. Without the "Nearby devices" permission: the "… is not reachable over Wi‑Fi" card → "Allow" → the system dialog →
       connecting over Bluetooth at once. Refusal → the "App settings" dialog.
    4. Turn Wi‑Fi back on → within ~20 s the "Wi‑Fi" icon, the snackbar "Back on Wi‑Fi"; the status never went to
       "Connecting…", the title and position did not flicker.
    5. The phone's Wi‑Fi and Bluetooth are off → the "turn Bluetooth on" card → "Turn on" → the system dialog → connection.
    6. "Other ways to connect" → "Connect via Bluetooth": the laptop is at the top of the list (computer icon), the address
       matches the companion's `bt`; select → a code on the TV screen → enter it → the remote. Then turn Wi‑Fi on → the saved
       PC gets an IP (the same record, no second code).
    7. Laptop not paired / companion closed → "The companion is not answering over Bluetooth…" within ~12 s,
       the app does not hang, "Cancel" during connecting works.
    8. Background: a movie is playing, the app is minimized, the notification is shown → turn off Wi‑Fi → a minute later
       the notification buttons work (the service survived the switch to RFCOMM). Screen off for 10+ minutes — the
       connection is alive (ping 15 s).
    9. The laptop sleeps during a Bluetooth session → reconnect after it wakes up.
    10. The companion closes during a Bluetooth session → the break is noticed at once (`read() == -1`/`IOException`),
        then the usual reconnect attempts.
14. **TV screen** (Galaxy S24 ↔ the TV laptop: built-in panel + TV over HDMI, a companion with monitor `id`s):
    1. On the remote, the chip "Display: <TV name>" with a TV icon (or "<panel>" with a laptop icon); the name comes from
       EDID, as in the Windows display settings; on a narrow screen the chips wrap to a second line without cutting "More".
    2. VLC open on the laptop → sheet: "VLC is here" on the panel, the radio button on the preferred monitor, resolutions
       correct, "primary" on the primary one. Tap the TV → haptics, progress, the VLC window on the TV (fullscreen kept),
       "VLC is here" and the radio button moved to the TV, chip "Display: <TV>".
    3. Close VLC → the chip shows the preferred monitor; the sheet shows the caption "VLC will open on this display";
       choose the laptop panel → start VLC from the phone and by double-clicking a file on the PC → both times the window
       is on the panel.
    4. "Do not manage" → the radio button is on it; VLC opens wherever Windows decides, the companion does not move the window.
    5. Turn the TV off while the TV is chosen → no TV in the sheet, the explanation "The chosen display is not connected
       right now"; turn the TV on → the "Refresh" button (or reopen the sheet) → the TV is back in the list and chosen.
    6. Drag the VLC window with the mouse to another monitor → the chip and "VLC is here" update (push `state`), the
       choice is not reset.
    7. Old companion (no `id`): the list by index, radio button = where VLC is, no "Do not manage", while VLC is closed
       the rows are disabled with a hint to update the companion.
    8. Remote over Bluetooth: moving works the same way.
15. **First-run permissions** (Galaxy S24, Android 16). Reset: `adb shell pm clear app.sofabird` (forgets
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
