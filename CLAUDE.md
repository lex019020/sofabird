# Sofabird — project rules

Sofabird (formerly VLC Remote): an Android remote for VLC on a Windows PC at the TV + a Windows companion. The audience
is international.

**Name.** The product is *Sofabird* (the PC part: *Sofabird for Windows*); identifiers use `sofabird` / `Sofabird` /
`app.sofabird`, the site is `sofabird.app`. VLC is VideoLAN's trademark: describe the relation as "for VLC" / "remote
for VLC media player", never use the VLC cone, keep the "not affiliated with VideoLAN" notice ([ADR-0006](docs/adr/0006-rename-to-sofabird.md)).

**Languages.** Repository documentation, code comments, logs, commit messages, PR titles/descriptions and release
notes are in English. The apps' UI follows the system language with English as the default; Russian is an
additional UI translation. Talk to the user in Russian. Identifiers and code are in English too, and so are the
messages printed by the scripts in `scripts/`.

**The UI of the app and the companion follows the system language, the fallback language is English; a Russian
translation is available.** No UI literals in code:
- Android: `res/values/strings.xml` — English (default), `res/values-ru/strings.xml` — Russian, the key sets
  are identical; languages for the Android 13+ "app language" setting — `res/xml/locales_config.xml`.
- Companion: strings in the tables of `companion/tools/gen_localization.py` (en + ru) → `python companion/tools/gen_localization.py`
  generates `Strings*.resx` + `UiText` (the PC UI, Windows language) and `ProtocolText*.resx` + `ProtocolText` (the `err.msg`
  texts that the session renders in the phone's language from `hello.device.locale`). Do not edit generated files by hand.
- New language: a column in the generator + `values-<lang>/strings.xml` + a line in `locales_config.xml`.

## Where things are

- `docs/PLAN.md` — plan and milestones (M0–M5), `docs/adr/` — architecture decisions, `docs/research/` — research
  and live measurements of VLC 3.0.23 (`samples/` — real responses, also used as test fixtures).
- `protocol/README.md` — the phone ↔ companion protocol v1 (the source of truth for both sides),
  `protocol/vlc-http.md` — VLC's direct HTTP mode and all its quirks.
- `android/` — Kotlin + Compose (`:app`, `:core:protocol`). `companion/` — .NET 10 (`Sofabird.Core`,
  `Sofabird.Companion`, tests), `companion/lua/` — Lua pages for VLC.
- `scripts/deploy-windows.ps1` — the companion on Windows, `scripts/deploy-android.ps1` — the app on the phone,
  `scripts/deploy-lib.ps1` — their shared code; `scripts/release_notes.py` — PR checks and release notes assembly.

## Build and tests

```powershell
dotnet build companion\Sofabird.sln -c Release
dotnet test  companion\Sofabird.sln
$env:JAVA_HOME = 'C:\Program Files\Android\openjdk\jdk-21.0.8'   # on this PC; any JDK 17/21 will do
android\gradlew.bat -p android detekt test :app:assembleDebug   # detekt: --auto-correct fixes formatting
android\gradlew.bat -p android :app:verifyRoborazziDebug   # layout screenshots; recordRoborazziDebug updates them
```

UI texts must not break inside a word at any font scale: labels in tabs, buttons, chips and badges use
`LabelText` (one line, ellipsis, limited growth), groups of chips/buttons go into `FlowRow`, file names are shown
with `withNameBreaks()`. Check new screens in both languages at font scale 2.0.

**The remote screen never scrolls** (owner's rule), in any orientation, language, font scale or state: everything
fits on one screen. When space runs short the cover gives way first (it takes only the height left after the other
blocks), then elements shrink; no `verticalScroll`/lazy lists on the remote. Options that do not fit go into sheets
("More", tracks, display).

Gradle 8.6 / AGP 8.4.2 / Kotlin 2.0.21 / compileSdk 35 are pinned to the installed SDK (without the licenses folder
nothing new can be downloaded); do not bump them unless necessary. Companion — the SDK from `companion/global.json`.

Make protocol changes simultaneously in `protocol/`, `android/core/protocol` and `companion/Sofabird.Core`
(+ tests on both sides). Live checks with VLC — only with a separate instance on `127.0.0.1`
(`--no-one-instance -I dummy … --http-host 127.0.0.1`), without touching `%APPDATA%\vlc`, sound, displays or the PC's power.

## Branches and commits

- Into `main` only via a PR with a **squash merge** (configured in the repository: commit title = PR title,
  body = PR description, the branch is deleted after the merge). No direct pushes to `main`.
- Branch: `<type>/<short-kebab-case>`, e.g. `feat/bt-transport`, `fix/companion-port`.
- Commit messages are in English, in the style of a PR title (`fix(android): handle empty playlist`);
  the PR title is what ends up in the history of `main`.

## Pull requests and release notes

**Title** (English): `<type>(<scope>): <summary>` — lower case, imperative mood, no trailing period, ≤ 72 chars.

| Type | When | Release section |
|---|---|---|
| `feat` | new user-facing capability | New |
| `fix` | bug fix | Fixes |
| `perf`, `refactor` | faster / reworked without behaviour change | Improvements |
| `docs`, `test`, `build`, `ci`, `chore`, `revert` | everything else | Other (only if it has notes) |
| `<type>!` (e.g. `feat(protocol)!:`) | breaking: incompatible protocol, reinstall needed | ⚠️ Breaking changes |

Scopes: `android`, `companion`, `protocol`, `lua`, `deploy`, `docs`, `ci`, `deps` (comma-separated if several).

**Description** (English), following `.github/pull_request_template.md`:

- `## Summary` — for the reviewer: what changed and why.
- `## Release notes` — **for the remote's user**, copied verbatim into the release: `- …` bullets, one per
  visible change, no class or file names ("The remote shows audio tracks with their languages", "The companion
  no longer crashes when VLC is closed during seeking"). If users won't notice anything — the single word `none`.
  Must not be `none` for `feat`/`fix`.
- `## Testing` — test commands, manual checks.
- `## Device checks pending` — what still needs checking on the phone / the TV laptop, or `—`.

The **PR** workflow (`.github/workflows/pr.yml`) validates the title format, English language and the release
notes section (`scripts/release_notes.py check`); **CI** builds and tests both parts. Local check:

```powershell
$env:PR_TITLE='feat(android): …'; $env:PR_BODY=Get-Content body.md -Raw; python scripts\release_notes.py check
```

Create from the console: `gh pr create --title "<title>" --body-file <file>`; merge: `gh pr merge --squash`.

## Versions and builds

- The base version is the `VERSION` file (SemVer `MAJOR.MINOR.PATCH`); the companion (`VersionPrefix`) and
  Android (`versionName`) take their version from it. `versionCode` = number of commits in `main` × 10 (CI); for local
  `deploy-android -Local` builds — commits in `origin/main` × 10 + 5: newer than the current test build, older than the next one.
- A **test build** (pre-release `v<VERSION>-beta<NNNN>`, e.g. `v0.2.0-beta0040`, so that GitHub lists the newest on top) is created automatically on every merge to `main`
  (`.github/workflows/release.yml`). Description — the release notes of the merged PR + installation instructions.
  Files: `Sofabird-Setup.exe` (linked from the notes) + the Velopack update feed (`*-full.nupkg`,
  `*-delta.nupkg`, `releases.win-x64.json`), `Sofabird-<version>.apk`, `deploy-*.ps1/.cmd`.
- **The companion updates itself** (Velopack, `UpdateService`): a few minutes after the start and every 6 h it checks
  GitHub Releases, downloads in the background and restarts into the new version when VLC is not playing. Test builds
  stay on test builds, releases on releases (tray: "Get test builds"); `-local` builds never update themselves.
- **Release** `v<VERSION>`: Actions → Release → Run workflow → `stable` (or
  `gh workflow run release.yml -f channel=stable`). The notes are collected from all PRs merged since the
  previous release, by section. If the tag already exists, the workflow fails.
- **After a release** the first PR bumps `VERSION`: `feat` → MINOR, only `fix` → PATCH, `!` → MAJOR
  (while the version is `0.x` — MINOR). It can also be bumped in the PR with the feature itself.

## Deployment: Windows

`scripts\deploy-windows.ps1` (or double-click `deploy-windows.cmd`); like all scripts in `scripts/`, it prints its
messages in English:

| Command | What it does |
|---|---|
| `deploy-windows.ps1` | the latest test build of the companion from GitHub Releases (anonymous; `gh` is used only as a fallback) |
| `-Channel stable` / `-Tag v0.2.0-beta0040` | the latest release / a specific build (also the way back to an earlier one) |
| `-Local` | build the installer from the working copy and install it |
| `-Package <Setup.exe>` | install a ready-made installer (used by `dev-laptop.ps1 -Deploy`) |
| `-CheckOnly` | compare the installed version with the latest one |
| `-Firewall` / `-Autostart` | firewall rules for the companion and VLC (private network, one UAC prompt) / autostart |
| `-Uninstall [-Purge]` | uninstall (with `-Purge` — also settings, pairings, logs and the certificate of the encrypted connection) |

The companion is installed by its Velopack installer (`Sofabird-Setup.exe`, which a user can also just
run) into `%LOCALAPPDATA%\Sofabird` (`current\Sofabird.exe` — a stable path for the firewall
rules and autostart) for the current user; the .NET 10 runtime, when missing, is installed by it with one administrator
prompt. The script shuts the running instance down gracefully (`--shutdown`), runs the installer quietly, starts the
companion and replaces the companion under its earlier name, VLC Remote Companion (`%LOCALAPPDATA%\VlcRemoteCompanion`;
Sofabird takes its settings and history over at the first start). Settings, pairings, history and logs stay in
`%APPDATA%\Sofabird`. If VLC media player is not installed, the script installs it via winget
(`VideoLAN.VLC`, one administrator prompt; not possible over SSH — then it only warns); `-SkipVlc` disables this.
Packing (`dotnet vpk`, pinned in `dotnet-tools.json`) is `New-CompanionPackage` in `deploy-lib.ps1`, shared by the
release workflow and `-Local`.

## Deployment: Android

`scripts\deploy-android.ps1` (or `deploy-android.cmd`) installs the APK via adb **only on allowed devices**
from `%USERPROFILE%\.sofabird\devices.json` (outside git, by the serial number `ro.serialno`). Other connected
devices — for example, the Karoo bike computer from a neighbouring project — are skipped with a message; an empty list =
refusal. The list is kept per developer PC, so it is not in the repository.

| Command | What it does |
|---|---|
| `deploy-android.ps1` | the latest test build on the allowed connected phones (same version — skipped) |
| `-Connect <ip>:<port>` | specify the address explicitly; usually not needed — the phone is found automatically (see below) |
| `-Channel stable` / `-Tag …` / `-Local` | a release / a specific build / a build from source (requires `keystore.properties`) |
| `-CheckOnly` / `-Force` | show the versions on the phones / reinstall the same version |
| `-ListDevices` | connected and allowed devices |
| `-AllowDevice <serial>` / `-DisallowDevice <serial>` | add / remove a device |

Wi-Fi debugging (Android 11+): "Developer options" → "Wireless debugging". Pair once
(`adb pair <ip>:<pairing port> <code>`). The debugging port changes every time it is turned on, so the script finds the
phone by itself: the last successful address → adb mDNS (`ADB_MDNS_OPENSCREEN=1`; on this network the ISP router does not
pass it) → the IP by MAC from ARP (after pinging the /24) or the previous IP → a scan of ports 30000–49999 (~10 s). The
successful address, port and MAC are remembered in `devices.json`. The phone must be awake and on the same network
(`-NoAutoConnect` disables the search).
If no allowed phone is present, the APK is saved to `%APPDATA%\Sofabird\apk`.

## APK signing

Test builds and releases are signed with **one** key, otherwise an update will not install over the previous version.
The debug build (`app.sofabird.debug`, "Sofabird (dev)") is signed with the debug key and installs side by side.

- Key: `%USERPROFILE%\.sofabird\signing\sofabird.jks` (+ `keystore.properties` next to it). **Make a backup**:
  losing the key = reinstalling the app on the phones and losing the pairings.
- Locally: copy `keystore.properties` into `android\` (it is not committed to git).
- CI: repository secrets `ANDROID_KEYSTORE_BASE64`, `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS`,
  `ANDROID_KEY_PASSWORD`.

## Debugging on the TV laptop (SSH)

The companion is tested on a separate laptop connected to the TV. From the developer PC we reach it over SSH
(the built-in Windows OpenSSH Server, key-only login, port 22 open only on the private network):

| Command (on the developer PC) | What it does |
|---|---|
| `scripts\dev-laptop.ps1 -Init` | the key `~\.ssh\id_ed25519_sofabird` + setup files in `D:\shared\sofabird-setup` |
| on the laptop: `\\<dev PC>\shared\sofabird-setup\laptop-setup.cmd` | one-time sshd setup (asks for administrator rights), prints the IP and user |
| `dev-laptop.ps1 -Connect <IP> -User <name>` | writes `Host sofabird-laptop` into `~\.ssh\config` and checks the connection |
| `dev-laptop.ps1` (`-Status`) | companion, `/info`, VLC processes with their command lines, `vlcrc` keys, network profiles |
| `dev-laptop.ps1 -Deploy -Local` / `-Deploy [-Tag …]` | build here (or take from a release) and install on the laptop |
| `dev-laptop.ps1 -Logs [-Tail N] [-Follow]` | the companion's log |
| `dev-laptop.ps1 -Exec '<PowerShell>'` / `-Shell` | an arbitrary command / an interactive session |

Over SSH processes start outside the user's desktop, so `deploy-windows.ps1` in an SSH session starts and
stops the companion through a scheduled task in the interactive session (the user must be logged in).
To disable access: on the laptop, `laptop-setup.ps1 -Remove`.
