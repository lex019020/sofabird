# vlc-remote — правила проекта

Android‑пульт для VLC на Windows‑ПК у телевизора + Windows‑компаньон.

**Языки.** Общение с пользователем, документация в `docs/`, README, UI‑строки, сообщения скриптов — по‑русски.
**Коммиты, заголовки и описания PR (включая release notes) — по‑английски.** Идентификаторы и код — по‑английски.

## Где что

- `docs/PLAN.md` — план и этапы (M0–M5), `docs/adr/` — архитектурные решения, `docs/research/` — исследования
  и живые замеры VLC 3.0.23 (`samples/` — реальные ответы, они же фикстуры тестов).
- `protocol/README.md` — протокол телефон ↔ компаньон v1 (источник истины для обеих сторон),
  `protocol/vlc-http.md` — прямой HTTP‑режим VLC и все его причуды.
- `android/` — Kotlin + Compose (`:app`, `:core:protocol`). `companion/` — .NET 9 (`VlcRemote.Core`,
  `VlcRemote.Companion`, тесты), `companion/lua/` — Lua‑страницы для VLC.
- `scripts/deploy-windows.ps1` — компаньон на Windows, `scripts/deploy-android.ps1` — приложение на телефон,
  `scripts/deploy-lib.ps1` — их общий код; `scripts/release_notes.py` — проверка PR и сборка release notes.

## Сборка и тесты

```powershell
dotnet build companion\VlcRemote.sln -c Release
dotnet test  companion\VlcRemote.sln
$env:JAVA_HOME = 'C:\Program Files\Android\openjdk\jdk-21.0.8'   # на этом ПК; подойдёт любой JDK 17/21
android\gradlew.bat -p android test :app:assembleDebug
```

Gradle 8.6 / AGP 8.4.2 / Kotlin 2.0.21 / compileSdk 35 — привязаны к установленному SDK (без папки licenses
ничего нового не скачать); не поднимать без необходимости. Компаньон — SDK из `companion/global.json`.

Изменения протокола делать одновременно в `protocol/`, `android/core/protocol` и `companion/VlcRemote.Core`
(+ тесты на обеих сторонах). Живые проверки с VLC — только отдельным экземпляром на `127.0.0.1`
(`--no-one-instance -I dummy … --http-host 127.0.0.1`), не трогая `%APPDATA%\vlc`, звук, экраны и питание ПК.

## Ветки и коммиты

- В `main` — только через PR со **squash‑мержем** (настроено в репозитории: заголовок коммита = заголовок PR,
  тело = описание PR, ветка удаляется после мержа). Прямые пуши в `main` не делать.
- Ветка: `<type>/<short-kebab-case>`, например `feat/bt-transport`, `fix/companion-port`.
- Сообщения коммитов — по‑английски, в стиле заголовка PR (`fix(android): handle empty playlist`);
  в историю `main` попадает заголовок PR.

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

## Версии и сборки

- Базовая версия — файл `VERSION` (SemVer `MAJOR.MINOR.PATCH`), из него берут версию компаньон (`VersionPrefix`)
  и Android (`versionName`). `versionCode` = число коммитов в `main` (`git rev-list --count HEAD`).
- **Тестовая сборка** (pre‑release `v<VERSION>-beta.<N>`) создаётся автоматически на каждый мерж в `main`
  (`.github/workflows/release.yml`). Описание — release notes смёрдженного PR + инструкция по установке.
  Файлы: `VlcRemote.Companion-<версия>-win-x64.zip`, `VlcRemote-<версия>.apk`, `deploy-*.ps1/.cmd`.
- **Релиз** `v<VERSION>`: Actions → Release → Run workflow → `stable` (или
  `gh workflow run release.yml -f channel=stable`). Заметки собираются из всех PR, смёрдженных после
  предыдущего релиза, по разделам. Если тег уже есть — workflow падает.
- **После релиза** первый PR поднимает `VERSION`: `feat` → MINOR, только `fix` → PATCH, `!` → MAJOR
  (пока версия `0.x` — MINOR). Можно поднимать и в PR с самой фичей.

## Развёртывание: Windows

`scripts\deploy-windows.ps1` (или двойной клик по `deploy-windows.cmd`):

| Команда | Что делает |
|---|---|
| `deploy-windows.ps1` | последняя тестовая сборка компаньона из GitHub Releases (нужен `gh auth login`) |
| `-Channel stable` / `-Tag v0.2.0-beta.12` | последний релиз / конкретная сборка |
| `-Local` | собрать из рабочей копии и поставить |
| `-CheckOnly` | сравнить установленную версию с последней |
| `-Firewall` / `-Autostart` | правила брандмауэра для компаньона и VLC (частная сеть, один UAC) / автозапуск |
| `-Rollback` | вернуть предыдущую версию (`app.prev`) |
| `-Uninstall [-Purge]` | удалить (с `-Purge` — и настройки, сопряжения, журналы) |

Компаньон ставится в `%LOCALAPPDATA%\VlcRemote\app` без прав администратора; перед заменой файлов старый
экземпляр закрывается штатно (`VlcRemote.Companion.exe --shutdown`). При нехватке .NET 9 Desktop/ASP.NET Core
Runtime скрипт ставит их через winget.

## Развёртывание: Android

`scripts\deploy-android.ps1` (или `deploy-android.cmd`) ставит APK по adb **только на разрешённые устройства**
из `%USERPROFILE%\.vlc-remote\devices.json` (вне git, по серийному номеру `ro.serialno`). Остальные подключённые
устройства — например, велокомпьютер Karoo из соседнего проекта — пропускаются с сообщением; пустой список =
отказ. Сейчас разрешён Samsung Galaxy S24 (`RFCWC0SSKZK`).

| Команда | Что делает |
|---|---|
| `deploy-android.ps1` | последняя тестовая сборка на разрешённые подключённые телефоны (та же версия — пропуск) |
| `-Connect 192.168.0.16:36457` | сначала `adb connect` (Wi‑Fi‑отладка), потом установка |
| `-Channel stable` / `-Tag …` / `-Local` | релиз / конкретная сборка / сборка из исходников (нужен `keystore.properties`) |
| `-CheckOnly` / `-Force` | показать версии на телефонах / переустановить ту же версию |
| `-ListDevices` | подключённые и разрешённые устройства |
| `-AllowDevice <serial>` / `-DisallowDevice <serial>` | добавить / убрать устройство |

Wi‑Fi‑отладка (Android 11+): «Для разработчиков» → «Отладка по Wi‑Fi». Сопряжение один раз
(`adb pair <ip>:<порт сопряжения> <код>`), после перезагрузки телефона или смены сети — `-Connect <ip>:<новый порт>`.
Если разрешённого телефона нет, APK сохраняется в `%LOCALAPPDATA%\VlcRemote\apk`.

## Подпись APK

Тестовые сборки и релизы подписываются **одним** ключом, иначе обновление не встанет поверх. Debug‑сборка
(`dev.vlcremote.app.debug`, «VLC Remote (dev)») подписана debug‑ключом и ставится рядом.

- Ключ: `%USERPROFILE%\.vlc-remote\signing\vlcremote.jks` (+ `keystore.properties` рядом). **Сделать резервную
  копию**: потеря ключа = переустановка приложения на телефонах с потерей сопряжений.
- Локально: скопировать `keystore.properties` в `android\` (в git не попадает).
- CI: секреты репозитория `ANDROID_KEYSTORE_BASE64`, `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS`,
  `ANDROID_KEY_PASSWORD`.

## Отладка на ноутбуке у ТВ (SSH)

Компаньон тестируется на отдельном ноутбуке, подключённом к телевизору. С ПК разработчика к нему ходим по SSH
(встроенный OpenSSH Server Windows, вход только по ключу, порт 22 открыт только в частной сети):

| Команда (на ПК разработчика) | Что делает |
|---|---|
| `scripts\dev-laptop.ps1 -Init` | ключ `~\.ssh\id_ed25519_vlcremote` + файлы настройки в `D:\shared\vlc-remote-setup` |
| на ноутбуке: `\DESKTOP-9SO2EON\shared\vlc-remote-setup\laptop-setup.cmd` | разовая настройка sshd (запросит права администратора), печатает IP и пользователя |
| `dev-laptop.ps1 -Connect <IP> -User <имя>` | запись `Host vlcremote-laptop` в `~\.ssh\config` и проверка связи |
| `dev-laptop.ps1` (`-Status`) | компаньон, `/info`, процессы VLC с командной строкой, ключи `vlcrc`, профили сети |
| `dev-laptop.ps1 -Deploy -Local` / `-Deploy [-Tag …]` | собрать здесь (или взять из релиза) и поставить на ноутбук |
| `dev-laptop.ps1 -Logs [-Tail N] [-Follow]` | журнал компаньона |
| `dev-laptop.ps1 -Exec '<PowerShell>'` / `-Shell` | произвольная команда / интерактивная сессия |

По SSH процессы запускаются вне рабочего стола пользователя, поэтому `deploy-windows.ps1` в SSH‑сеансе запускает и
останавливает компаньон через задачу планировщика в интерактивном сеансе (пользователь должен быть залогинен).
Отключить доступ: на ноутбуке `laptop-setup.ps1 -Remove`.
