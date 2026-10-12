# vlc-remote

Мобильный пульт для VLC на Windows‑компьютере, подключённом к телевизору.
Телефон (Android) управляет воспроизведением по локальной сети напрямую через
HTTP‑интерфейс VLC, а с компаньоном на ПК получает автообнаружение, push‑события,
переключение аудиоустройства, перенос окна VLC на телевизор и (этап M3) Bluetooth.

[![CI](https://github.com/lex019020/vlc-remote/actions/workflows/ci.yml/badge.svg)](https://github.com/lex019020/vlc-remote/actions/workflows/ci.yml)

## Состав

| Папка | Что это | Статус |
|---|---|---|
| [`android/`](android/README.md) | Android‑приложение, Kotlin + Jetpack Compose. Модули `:app` и `:core:protocol` | M1 готов: режимы «только VLC» (уровни 0/1) и «компаньон» |
| [`companion/`](companion/README.md) | Windows‑компаньон, .NET 9, трей. WebSocket/UDP/DNS‑SD, менеджер VLC, RC‑мост, сопряжение | M2 готов (LAN). Bluetooth — M3 |
| [`companion/lua/`](companion/lua) | Lua‑страницы для HTTP‑интерфейса VLC (`tracks.json`, `setvar.json`) и установщик | готово, проверено на VLC 3.0.23 |
| [`protocol/`](protocol/README.md) | Протокол телефон ↔ компаньон (v1), JSON‑схема, справочник прямого HTTP‑режима | v1 |
| [`docs/`](docs/PLAN.md) | План, ADR, исследования (протоколы VLC, существующие пульты, транспорт, UX) | |

## Как попробовать

**Без компаньона (уровень 0).** В VLC: Инструменты → Настройки → «Все» → Интерфейс →
Основные интерфейсы → галочка «Web»; в «Lua» задать пароль; перезапустить VLC; разрешить
в брандмауэре. В приложении: «Подключение» → найти ПК (скан сети) или ввести адрес → пароль.

**Уровень 1.** На ПК выполнить `companion\lua\install.ps1` и перезапустить VLC: приложение
покажет текущие дорожки и названия глав.

**С компаньоном (уровень 2).** Собрать и запустить `VlcRemote.Companion.exe` (см.
[companion/README.md](companion/README.md)); в трее «Сопряжение телефона…» — на экране
появится 6‑значный код; в приложении выбрать найденный ПК и ввести код. Компаньон сам
запускает VLC с веб‑интерфейсом и ставит Lua‑страницы.

## Сборка

```powershell
# Компаньон (нужен .NET SDK 9)
dotnet build companion\VlcRemote.sln -c Release
dotnet test  companion\VlcRemote.sln

# Android (JDK 17/21, Android SDK 35)
cd android
.\gradlew.bat :app:assembleDebug test
```

CI на GitHub Actions собирает обе части и публикует артефакты (`app-debug-apk`, `companion-win-x64`).

## Дорожная карта

См. [docs/PLAN.md](docs/PLAN.md) § 6: M3 — Bluetooth RFCOMM, M4 — библиотека с постерами,
«продолжить просмотр», плитка быстрых настроек, M5 — виджеты, Wear OS, субтитры, VLC 4.
