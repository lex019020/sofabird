# ADR-0002. Transports and discovery

Status: accepted (2026-10-12). Basis: [research/transport-and-companion.md](../research/transport-and-companion.md), [research/prior-art.md](../research/prior-art.md).

## Decision

1. **LAN without the companion** — HTTP to VLC (`/requests/*.json`, Basic auth with an empty user name). Discovery:
   the saved address + host name, a parallel HTTP sweep of the `/24` outward from our own IP with a ~1 s timeout
   (VLC is recognized by a `401` with the realm `VLC stream`), manual entry of `host[:port]`, `pc-name.local`.
   mDNS is useless without the companion: VLC does not announce itself.
2. **LAN with the companion** — WebSocket `ws://pc:47555`, NDJSON, token. Discovery: DNS-SD
   `_vlcremote._tcp` (on Windows — the built-in responder via `DnsServiceRegister`; on Android —
   `NsdManager`) plus a UDP beacon on `:47556` as a fallback, plus a QR code on the TV.
3. **Bluetooth** — Classic RFCOMM (SPP) to the companion: Windows `RfcommServiceProvider` (works from
   a regular .NET application without administrator rights — verified), Android
   `createRfcommSocketToServiceRecord` + `BLUETOOTH_CONNECT`, device selection via
   `CompanionDeviceManager`. The same NDJSON, the same `Session` on the PC.
4. **Bluetooth PAN** (phone tethering → Windows "Join PAN") — a documented fallback
   without the companion: a regular IP route, HTTP to VLC. In the app — a hint, not automation.
5. **Channel selection**: WS if the PC is found on the LAN within ~3 s; otherwise RFCOMM if there is a paired PC;
   otherwise the "Connection" screen. Switching without losing state, commands are buffered for 5 s.

## Rejected

- **A BLE GATT peripheral on Windows** — works, but messages are 20–512 bytes, needs its own fragmentation,
  no disconnect API; kept as plan B.
- **Keyboard/hotkey emulation as a transport** — requires window focus, provides no state.
- **mDNS libraries for .NET** (Makaretu, Zeroconf, Tmds.MDns, Mono.Zeroconf) — dead or browse-only.
- **TLS in v1** — home network, a token is enough; the `fp` field in the QR code is reserved for pinning.
