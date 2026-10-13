# ADR-0004. A .NET companion and the NDJSON protocol

Status: accepted (2026-10-12). Basis: [research/transport-and-companion.md](../research/transport-and-companion.md), [research/live-probe.md](../research/live-probe.md).

## Decision

- **Form**: a single WinExe `net9.0-windows10.0.19041.0`, WinForms `NotifyIcon` in the tray, Kestrel
  (`CreateSlimBuilder`) for WebSocket and diagnostic REST, WinRT RFCOMM, `DnsServiceRegister`
  via P/Invoke, UDP beacon, NAudio, CsWin32, Serilog. Velopack installation into `%LocalAppData%`,
  autostart via HKCU\Run, without administrator rights; firewall rules — a separate
  one-time helper with UAC.
- **Companion ↔ VLC**: the HTTP interface on `127.0.0.1` for state and commands, one RC socket on
  `127.0.0.1:4212` for `atrack *`/`adev`/events/`quit`; the companion owns VLC's lifecycle
  (starting it with `--extraintf http:rc …`), and edits `vlcrc` only while VLC is stopped. The Lua pages
  from `companion/lua/` are copied into `%APPDATA%\vlc\lua\http\requests\`.
- **Protocol**: NDJSON over WS and RFCOMM, types `hello/welcome`, `cmd`, `sys`, `browse`,
  `library`, `state` (diff, ≤ 4 Hz), `event`, `ping/pong`, `err`; the protocol version in `hello`.
  The schema is kept in `protocol/` as JSON Schema and is used by both sides.
- **Pairing**: a 6-digit code/QR on the TV → a long-lived token (the PC stores its hash).

## Rejected

- **A LibVLCSharp player inside the companion** — full control, but duplicates VLC and loses the
  user's settings; possible as a future "embedded mode".
- **HttpListener / WatsonWebsocket / Fleck** — require `netsh http add urlacl`, or the project is dead.
- **Rust/Go for the companion** — a smaller exe, but 2–3× more expensive for the same result;
  WinRT/COM is more convenient from C#.
- **MSIX / a Windows service** — complicates installation and access to the user session (windows, audio).
