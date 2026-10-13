# vlc-remote — правила проекта

Android‑пульт для VLC на Windows‑ПК у телевизора + Windows‑компаньон. Общение с пользователем,
документация, UI‑строки, заголовки и описания PR — **по‑русски**; идентификаторы и код — по‑английски.

## Где что

- `docs/PLAN.md` — план и этапы (M0–M5), `docs/adr/` — архитектурные решения, `docs/research/` — исследования
  и живые замеры VLC 3.0.23 (`samples/` — реальные ответы, они же фикстуры тестов).
- `protocol/README.md` — протокол телефон ↔ компаньон v1 (источник истины для обеих сторон),
  `protocol/vlc-http.md` — прямой HTTP‑режим VLC и все его причуды.
- `android/` — Kotlin + Compose (`:app`, `:core:protocol`). `companion/` — .NET 9 (`VlcRemote.Core`,
  `VlcRemote.Companion`, тесты), `companion/lua/` — Lua‑страницы для VLC.
- `scripts/deploy.ps1` — установка/обновление сборок на Windows, `scripts/release_notes.py` — проверка PR и
  сборка release notes.

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
- Ветка: `<тип>/<коротко-через-дефис>`, например `feat/bt-transport`, `fix/companion-port`.
- Коммиты внутри ветки — произвольные, но осмысленные; в историю `main` попадает заголовок PR.

## Pull request'ы и release notes

**Заголовок**: `<тип>(<область>): <что сделано>` — по‑русски, с маленькой буквы, без точки, ≤ 72 символов.

| Тип | Когда | Раздел в релизе |
|---|---|---|
| `feat` | новая возможность для пользователя | Новое |
| `fix` | исправление ошибки | Исправления |
| `perf`, `refactor` | ускорение, переработка без смены поведения | Улучшения |
| `docs`, `test`, `build`, `ci`, `chore`, `revert` | всё остальное | Прочее (если есть заметки) |
| `<тип>!` (например `feat(protocol)!:`) | ломающее изменение: несовместимый протокол, нужна переустановка | ⚠️ Важно |

Области: `android`, `companion`, `protocol`, `lua`, `deploy`, `docs`, `ci`, `deps` (несколько — через запятую).

**Описание** — по шаблону `.github/pull_request_template.md`:

- `## Что сделано` — для ревьюера: что и почему поменялось.
- `## Release notes` — **для пользователя пульта**, попадает в релиз дословно: пункты `- …`, по одному на
  заметное изменение, без имён классов и файлов («Пульт показывает дорожки с языками», «Компаньон больше не
  падает, если VLC закрыли во время перемотки»). Если изменение пользователю не видно — одно слово `нет`.
  Для `feat`/`fix` секция обязана быть непустой.
- `## Как проверено` — команды тестов, ручные проверки.
- `## Проверить на устройстве` — что осталось проверить на телефоне / ноутбуке у ТВ, или `—`.

Workflow **PR** (`.github/workflows/pr.yml`) проверяет заголовок и секцию release notes
(`scripts/release_notes.py check`); **CI** собирает и тестирует обе части. Проверить локально:

```powershell
$env:PR_TITLE='feat(android): …'; $env:PR_BODY=Get-Content body.md -Raw; python scripts\release_notes.py check
```

Создание PR из консоли: `gh pr create --title "<заголовок>" --body-file <файл>`; мерж: `gh pr merge --squash`.

## Версии и сборки

- Базовая версия — файл `VERSION` (SemVer `MAJOR.MINOR.PATCH`), из него берут версию компаньон (`VersionPrefix`)
  и Android (`versionName`). `versionCode` = число коммитов в `main` (`git rev-list --count HEAD`).
- **Тестовая сборка** (pre‑release `v<VERSION>-beta.<N>`) создаётся автоматически на каждый мерж в `main`
  (`.github/workflows/release.yml`). Её описание — release notes смёрдженного PR + инструкция по установке.
  Файлы: `VlcRemote.Companion-<версия>-win-x64.zip`, `VlcRemote-<версия>.apk`, `deploy.ps1`, `deploy.cmd`.
- **Релиз** `v<VERSION>`: Actions → Release → Run workflow → `stable` (или
  `gh workflow run release.yml -f channel=stable`). Заметки собираются из всех PR, смёрдженных после
  предыдущего релиза, сгруппированные по разделам. Если тег уже есть — workflow падает.
- **После релиза** первый PR поднимает `VERSION`: `feat` → MINOR, только `fix` → PATCH, `!` → MAJOR
  (пока версия `0.x` — MINOR). Можно поднимать и в PR с самой фичей.

## Развёртывание на Windows

`scripts\deploy.ps1` (или двойной клик по `scripts\deploy.cmd`):

| Команда | Что делает |
|---|---|
| `deploy.ps1` | последняя тестовая сборка компаньона из GitHub Releases (нужен `gh auth login`) |
| `deploy.ps1 -Apk` / `-ApkOnly` | + APK по adb (USB или `adb connect <ip>:<порт>`) — **только на разрешённые устройства** |
| `deploy.ps1 -ListDevices` | подключённые по adb устройства и список разрешённых |
| `deploy.ps1 -AllowDevice <serial>` / `-DisallowDevice <serial>` | добавить/убрать устройство (серийный номер `ro.serialno`) |
| `deploy.ps1 -Channel stable` / `-Tag v0.2.0-beta.12` | последний релиз / конкретная сборка |
| `deploy.ps1 -Local [-Apk]` | собрать из рабочей копии и поставить |
| `deploy.ps1 -CheckOnly` | сравнить установленную версию с последней |
| `deploy.ps1 -Firewall` | правила брандмауэра для компаньона и VLC (частная сеть, один запрос UAC) |
| `deploy.ps1 -Autostart` | автозапуск при входе в Windows |
| `deploy.ps1 -Rollback` | вернуть предыдущую версию (`app.prev`) |
| `deploy.ps1 -Uninstall [-Purge]` | удалить компаньон (с `-Purge` — и настройки, сопряжения, журналы) |

Компаньон ставится в `%LOCALAPPDATA%\VlcRemote\app` без прав администратора; перед заменой файлов старый
экземпляр закрывается штатно (`VlcRemote.Companion.exe --shutdown`). При нехватке .NET 9 Desktop/ASP.NET Core
Runtime скрипт ставит их через winget. На ноутбуке без клона репозитория — команда из описания любого релиза.

APK ставится только на устройства из `%USERPROFILE%\.vlc-remote\devices.json` (вне git): остальные подключённые
по adb устройства — например, велокомпьютер Karoo из соседнего проекта — пропускаются с сообщением. Пустой список =
отказ. Сейчас разрешён один телефон: Samsung Galaxy S24 (`RFCWC0SSKZK`). Wi‑Fi‑отладка: сопряжение один раз
(`adb pair <ip>:<порт> <код>`), после перезагрузки телефона или смены сети — `adb connect <ip>:<новый порт>`.

## Подпись APK

Тестовые сборки и релизы подписываются **одним** ключом, иначе обновление не встанет поверх. Debug‑сборка
(`dev.vlcremote.app.debug`, «VLC Remote (dev)») подписана debug‑ключом и ставится рядом.

- Ключ: `%USERPROFILE%\.vlc-remote\signing\vlcremote.jks` (+ `keystore.properties` рядом). **Сделать резервную
  копию**: потеря ключа = переустановка приложения на телефонах с потерей сопряжений.
- Локально: скопировать `keystore.properties` в `android\` (в git не попадает).
- CI: секреты репозитория `ANDROID_KEYSTORE_BASE64`, `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS`,
  `ANDROID_KEY_PASSWORD`.
