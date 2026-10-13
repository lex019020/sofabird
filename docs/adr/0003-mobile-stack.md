# ADR-0003. Mobile stack: Kotlin + Jetpack Compose

Status: accepted (2026-10-12). Basis: [research/mobile-stack-and-ux.md](../research/mobile-stack-and-ux.md).

## Decision

The Android app is written in Kotlin + Jetpack Compose (Material 3), like the author's existing
Android project. The protocol, models and file name parser go into pure Kotlin modules
(`:core:protocol`, `:core:naming`) without Android dependencies, so that Compose Multiplatform or Wear OS
can use them later.

Key libraries: Navigation 3, Media3 Session (notification, lock screen, "remote
volume" via `DeviceInfo.PLAYBACK_TYPE_REMOTE`), Ktor client on the OkHttp engine (one client
for VLC HTTP polling, the companion WebSocket and TMDB; `MockEngine` in tests), kotlinx.serialization,
Koin (many entry points outside the Activity: FGS, TileService), Room, DataStore, Coil 3 (a shared
`OkHttpClient`, the `Authorization` header for `/art`). targetSdk 36; moving to 37 — after
checking the runtime permission `ACCESS_LOCAL_NETWORK`.

## Rejected

- **Flutter / React Native** — everything risky (RFCOMM, NsdManager, MediaSession volume,
  TileService, FGS types) is platform API; plugins add a layer exactly there.
- **.NET MAUI** — despite the author's C# knowledge and the installed workloads: the same wrappers over the
  platform, a weaker ecosystem for Compose-like UI, MAUI 10 EOL in 2027.
- **Compose Multiplatform for the UI** — iOS is not planned, Material 3 in CMP lags behind.
