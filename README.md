# vlc-remote

A mobile remote for VLC on a Windows computer connected to a TV.
The phone (Android) controls playback over the local network directly through
VLC's HTTP interface; with the companion on the PC it also gets auto-discovery, push events,
audio device switching, moving the VLC window to the TV, and Bluetooth when Wi-Fi is unavailable.

[![CI](https://github.com/lex019020/vlc-remote/actions/workflows/ci.yml/badge.svg)](https://github.com/lex019020/vlc-remote/actions/workflows/ci.yml)

## Contents

| Folder | What it is | Status |
|---|---|---|
| [`android/`](android/README.md) | Android app, Kotlin + Jetpack Compose. Modules `:app` and `:core:protocol` | M1 done: "VLC only" (levels 0/1) and "companion" modes |
| [`companion/`](companion/README.md) | Windows companion, .NET 9, tray. WebSocket/UDP/DNS-SD, VLC manager, RC bridge, pairing | M2 (LAN) and M3 (Bluetooth) done |
| [`companion/lua/`](companion/lua) | Lua pages for VLC's HTTP interface (`tracks.json`, `setvar.json`) and an installer | done, verified on VLC 3.0.23 |
| [`protocol/`](protocol/README.md) | Phone ↔ companion protocol (v1), JSON schema, reference for the direct HTTP mode | v1 |
| [`docs/`](docs/PLAN.md) | Plan, ADRs, research (VLC protocols, existing remotes, transport, UX) | |

## How to try it

**Without the companion (level 0).** In VLC: Tools → Preferences → "All" → Interface →
Main interfaces → tick "Web"; set a password under "Lua"; restart VLC; allow it
in the firewall. In the app: "Connection" → find the PC (network scan) or enter the address → password.

**Level 1.** On the PC run `companion\lua\install.ps1` and restart VLC: the app
will show the current tracks and chapter names.

**With the companion (level 2).** Build and run `VlcRemote.Companion.exe` (see
[companion/README.md](companion/README.md)); in the app pick the PC that was found — a window with a
6-digit code pops up on the PC, enter the code in the app (the tray menu "Pair a phone…" shows it too).
The companion starts VLC with the web interface and installs the Lua pages by itself.

## Installing test builds

Every merge to `main` publishes a test build to [Releases](https://github.com/lex019020/vlc-remote/releases)
with notes from the PR. The companion for Windows and the app for the phone are installed by separate scripts:

```powershell
gh release download --repo lex019020/vlc-remote --pattern 'deploy-*' --dir $env:TEMP\vlcremote --clobber
& $env:TEMP\vlcremote\deploy-windows.ps1   # the companion on this PC
& $env:TEMP\vlcremote\deploy-android.ps1   # the app on an allowed phone via adb
```

From a working copy: `scripts\deploy-windows.ps1`, `scripts\deploy-android.ps1` (the `-Local` switch builds from
source). All switches are listed in [CLAUDE.md](CLAUDE.md).

## Building

```powershell
# Companion (requires .NET SDK 9)
dotnet build companion\VlcRemote.sln -c Release
dotnet test  companion\VlcRemote.sln

# Android (JDK 17/21, Android SDK 35)
cd android
.\gradlew.bat :app:assembleDebug test
```

CI checks every PR (build, tests, title format and release notes). The rules for PRs, versions and
releases are in [CLAUDE.md](CLAUDE.md).

## Roadmap

See [docs/PLAN.md](docs/PLAN.md) § 6 and § 10: next come first-run setup and permissions, watch history and
"Continue watching", UI layout polish, library posters and metadata, and a companion installer with auto-update;
later — a Quick Settings tile, widgets, Wear OS, subtitle download, VLC 4.
