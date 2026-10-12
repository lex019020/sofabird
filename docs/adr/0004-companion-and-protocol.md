# ADR-0004. Компаньон на .NET и протокол NDJSON

Статус: принято (2026-10-12). Основание: [research/transport-and-companion.md](../research/transport-and-companion.md), [research/live-probe.md](../research/live-probe.md).

## Решение

- **Форма**: один WinExe `net9.0-windows10.0.19041.0`, WinForms `NotifyIcon` в трее, Kestrel
  (`CreateSlimBuilder`) для WebSocket и диагностического REST, WinRT RFCOMM, `DnsServiceRegister`
  через P/Invoke, UDP‑маяк, NAudio, CsWin32, Serilog. Установка Velopack в `%LocalAppData%`,
  автозапуск через HKCU\Run, без прав администратора; правила брандмауэра — отдельный
  одноразовый помощник с UAC.
- **Компаньон ↔ VLC**: HTTP‑интерфейс на `127.0.0.1` для состояния и команд, один RC‑сокет на
  `127.0.0.1:4212` для `atrack *`/`adev`/событий/`quit`; компаньон владеет жизненным циклом VLC
  (запуск с `--extraintf http:rc …`), `vlcrc` правит только при остановленном VLC. Lua‑страницы
  из `companion/lua/` копируются в `%APPDATA%\vlc\lua\http\requests\`.
- **Протокол**: NDJSON поверх WS и RFCOMM, типы `hello/welcome`, `cmd`, `sys`, `browse`,
  `library`, `state` (diff, ≤ 4 Гц), `event`, `ping/pong`, `err`; версия протокола в `hello`.
  Схема хранится в `protocol/` как JSON Schema и используется обеими сторонами.
- **Сопряжение**: 6‑значный код/QR на ТВ → долгоживущий токен (на ПК хранится хэш).

## Отвергнуто

- **LibVLCSharp‑плеер внутри компаньона** — полный контроль, но дублирует VLC и теряет
  настройки пользователя; возможно как будущий «встроенный режим».
- **HttpListener / WatsonWebsocket / Fleck** — нужен `netsh http add urlacl` или проект мёртв.
- **Rust/Go для компаньона** — меньший exe, но 2–3× дороже при том же результате;
  WinRT/COM удобнее из C#.
- **MSIX / сервис Windows** — усложняет установку и доступ к сессии пользователя (окна, аудио).
