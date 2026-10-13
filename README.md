# vlc-remote

An Android remote for [VLC media player](https://www.videolan.org/vlc/) on a Windows PC connected to a TV, plus a
small Windows companion that makes the PC a proper media station.

[![CI](https://github.com/lex019020/vlc-remote/actions/workflows/ci.yml/badge.svg)](https://github.com/lex019020/vlc-remote/actions/workflows/ci.yml)
[![Release](https://github.com/lex019020/vlc-remote/actions/workflows/release.yml/badge.svg)](https://github.com/lex019020/vlc-remote/releases)

The phone controls VLC over the local network. Without anything installed on the PC it talks to VLC's own web
interface; with the companion it finds the PC by itself, starts VLC on the TV, keeps working over Bluetooth when
Wi‑Fi is gone and remembers where you stopped watching. The apps follow the system language: English and Russian.

## What it can do

**On the phone**
- Play/pause, seek with ±10/30 s buttons (hold to repeat) and a scrub bar with chapter marks, volume, chapters,
  playback speed, aspect ratio, fullscreen; the phone's volume keys control the PC.
- Audio and subtitle tracks with their languages, audio/subtitle delay.
- Browse the PC's drives and network shares, favourite folders, play or enqueue; playlist with swipe to remove.
- "Continue watching" and a watch history: resume where you stopped, mark as watched, the next episode of a series.
- A media notification with lock-screen controls while a movie plays.

**With the companion on the PC**
- Automatic discovery and one-time pairing with a 6-digit code shown on the TV.
- Starts VLC with the right settings and puts its window fullscreen on the TV; choose the TV and the audio device
  (speakers / TV / headphones) from the phone.
- Bluetooth fallback: the remote keeps working with Wi‑Fi off and switches back when it returns.
- PC controls: system volume, turn the screen off, sleep, "keep awake during a movie".
- Media station mode: the laptop keeps working with the lid closed, survives the TV being switched off and on
  (VLC pauses and comes back fullscreen on the TV).
- A setup window for the firewall, autostart and VLC, and quiet automatic updates.

## Install

Everything is on the [Releases](https://github.com/lex019020/vlc-remote/releases) page. Every merge to `main`
publishes a test build; releases are published from time to time.

1. **PC:** download and run `VlcRemote-Companion-Setup.exe`. It installs for the current user (the .NET 9
   runtime is added when missing, with one administrator prompt), opens the setup window — allow the firewall rules
   there — and then keeps itself up to date. VLC media player must be installed (3.0.x).
2. **Phone (Android 8+):** install `VlcRemote-<version>.apk` from the same release.
3. Open the app: it finds the PC, the PC shows a code on the TV, enter it on the phone. Done.

<details>
<summary>Installing with scripts (also installs VLC and puts the APK on a phone via adb)</summary>

```powershell
$d = "$env:TEMP\vlcremote"; New-Item -ItemType Directory -Force $d | Out-Null
$tag = @(Invoke-RestMethod 'https://api.github.com/repos/lex019020/vlc-remote/releases?per_page=1')[0].tag_name
'deploy-lib.ps1', 'deploy-windows.ps1', 'deploy-android.ps1' | ForEach-Object {
    Invoke-WebRequest "https://github.com/lex019020/vlc-remote/releases/download/$tag/$_" -OutFile "$d\$_" -UseBasicParsing }
& $d\deploy-windows.ps1 -Firewall -Autostart   # the companion on this PC
& $d\deploy-android.ps1                         # the app on an allowed phone via adb (see CLAUDE.md)
```

No GitHub account is needed. From a working copy: `scripts\deploy-windows.ps1`, `scripts\deploy-android.ps1`
(`-Local` builds from source); all switches are in [CLAUDE.md](CLAUDE.md).
</details>

### Without the companion

The app also works with plain VLC: in VLC open Tools → Preferences → "All" → Interface → Main interfaces, tick
"Web", set a password under "Lua", restart VLC and allow it in the firewall. In the app choose "Other ways to
connect" → find the PC or enter its address and the password. Running `companion\lua\install.ps1` on the PC adds
the current tracks and chapter names (level 1).

| Mode | On the PC | What you get |
|---|---|---|
| Level 0 | VLC with the web interface | playback, seeking, volume, tracks by number, browsing, playlist |
| Level 1 | + our Lua pages for VLC | + track languages and names, chapter names |
| Level 2 | the companion | + discovery, pairing, push updates, starting VLC on the TV, audio devices, Bluetooth, history, PC controls |

## Repository

| Folder | What it is |
|---|---|
| [`android/`](android/README.md) | Android app: Kotlin, Jetpack Compose, modules `:app` and `:core:protocol` |
| [`companion/`](companion/README.md) | Windows companion: .NET 9 tray app (WebSocket / UDP / DNS-SD / Bluetooth RFCOMM, VLC manager) |
| [`companion/lua/`](companion/lua) | Lua pages for VLC's HTTP interface (`tracks.json`, `setvar.json`) |
| [`protocol/`](protocol/README.md) | The phone ↔ companion protocol (v1) and VLC's HTTP interface with its quirks |
| [`docs/`](docs/PLAN.md) | Plan, architecture decisions (ADRs), research and live VLC measurements |
| [`scripts/`](scripts) | Install / deploy scripts, release notes, remote debugging of the TV laptop |

## Building

```powershell
# Companion (.NET SDK 9)
dotnet build companion\VlcRemote.sln -c Release
dotnet test  companion\VlcRemote.sln

# Android (JDK 17/21, Android SDK 35)
android\gradlew.bat -p android detekt test :app:assembleDebug
android\gradlew.bat -p android :app:verifyRoborazziDebug   # screenshot tests of the screens
```

CI builds and tests every pull request and checks its title and release notes; merges to `main` produce test
builds. Conventions for code, pull requests, versions and releases are in [CLAUDE.md](CLAUDE.md).

## Roadmap

Next: a media library with posters — a setup step for the media folders on the PC, file name parsing, artwork and
descriptions from TMDB, a poster grid on the phone next to the folder view. Later: a Quick Settings tile, widgets,
Wear OS, subtitle download, VLC 4. The full plan is in [docs/PLAN.md](docs/PLAN.md).
