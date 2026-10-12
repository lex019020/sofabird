# ADR-0003. Мобильный стек: Kotlin + Jetpack Compose

Статус: принято (2026-10-12). Основание: [research/mobile-stack-and-ux.md](../research/mobile-stack-and-ux.md).

## Решение

Android‑приложение пишется на Kotlin + Jetpack Compose (Material 3), как уже существующий
Android‑проект автора. Протокол, модели и парсер имён файлов выносятся в чистые Kotlin‑модули
(`:core:protocol`, `:core:naming`) без Android‑зависимостей, чтобы позже их мог использовать
Compose Multiplatform или Wear OS.

Ключевые библиотеки: Navigation 3, Media3 Session (уведомление, экран блокировки, «удалённая
громкость» через `DeviceInfo.PLAYBACK_TYPE_REMOTE`), Ktor‑client на движке OkHttp (один клиент
для HTTP‑опроса VLC, WebSocket компаньона и TMDB; `MockEngine` в тестах), kotlinx.serialization,
Koin (много точек входа вне Activity: FGS, TileService), Room, DataStore, Coil 3 (общий
`OkHttpClient`, заголовок `Authorization` для `/art`). targetSdk 36; переход на 37 — после
проверки runtime‑разрешения `ACCESS_LOCAL_NETWORK`.

## Отвергнуто

- **Flutter / React Native** — всё рискованное (RFCOMM, NsdManager, MediaSession‑громкость,
  TileService, FGS‑типы) — платформенные API; плагины добавляют слой именно там.
- **.NET MAUI** — несмотря на знание C# автором и установленные воркloads: те же обёртки над
  платформой, слабее экосистема для Compose‑подобного UI, EOL MAUI 10 в 2027.
- **Compose Multiplatform для UI** — iOS не в планах, Material 3 в CMP отстаёт.
