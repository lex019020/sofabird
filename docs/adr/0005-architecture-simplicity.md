# ADR-0005. Architecture review: keep it simple

Status: accepted (2026-10-13). Basis: a review of both code bases against current mainstream practice (Google's
"Guide to app architecture" for Android, the generic host / DI / async guidance for .NET), with a strong bias for
fewer layers and less code.

## Findings

Both apps already follow the essentials: one source of truth for the player state (`PlayerController` on the phone,
`VlcManager` on the PC), unidirectional data flow to the UI (immutable `UiState` via `StateFlow`,
`collectAsStateWithLifecycle`), a platform-neutral core with unit tests (`:core:protocol`, `Sofabird.Core`), no
global scopes. The problems are in the details: a few real bugs around shared mutable state and cancellation, logic
that lives in UI classes, duplicated helpers, dead code and inconsistent screen wiring.

## Decisions

**Not adopted** (they would add indirection without removing code or bugs):

- **Companion: generic host, `Microsoft.Extensions.DependencyInjection`, `IOptions`.** The manual composition in
  `Program.cs` is ~25 lines; a container would need about as many registrations plus `IHostedService` adapters and
  glue for the WinForms STA thread, and the "setup window before the listeners" order would have to be rebuilt.
  `Settings` is runtime state the user changes and saves, not configuration — `IOptions` does not fit it.
- **Android: a domain layer with use cases, more modules, moving the backends to `:core`.** The ViewModels call one
  repository-like controller; use cases would be one-line pass-throughs. PLAN § 4.6 is updated to the code.
- **Splitting `CompanionBackend` / `PlayerController` further.** They are cohesive and covered by tests.

**Adopted** (each in a small PR, no behaviour change unless it fixes a bug):

- **Immutable sharing.** An object published to several consumers (a state broadcast, `/info`) is never modified
  afterwards; replies get copies (`StateMessage.WithId`, `VlcInfo.Clone`).
- **One owner per mutable state, one lock per invariant.** VLC launch/restart/quit are serialised; settings are
  changed and saved under one lock; behaviour (keep-awake following playback, protocol events) lives in the host,
  not in the tray UI.
- **Cancellation is never swallowed.** Kotlin: no `runCatching` around suspend calls — a `resultOf` helper rethrows
  `CancellationException`. .NET: fire-and-forget work gets the shutdown token and its own error handling.
- **Screens: one pattern everywhere.** `XxxScreen` (collects the ViewModel's single `UiState`, routes `XxxActions`,
  hosts sheets) + stateless `XxxContent(ui, actions)`; screens and sheets do not inject singletons or take the
  ViewModel; each ViewModel keeps its local state in one `MutableStateFlow` (as `HistoryViewModel` does).
- **Hot paths stay cheap.** No file reads per request (paired devices are cached), no per-poll COM/Win32 enumeration
  that is not needed, high-frequency UI updates (the 4 Hz position) stay inside the composable that shows them.
- **Dead code is deleted** rather than kept "for later"; the analysers (ADR in CLAUDE.md "Build and tests") keep it
  from coming back.
