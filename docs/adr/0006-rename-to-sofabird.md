# ADR-0006. Rename to Sofabird

Status: accepted (2026-10-14). Basis: research on free alternatives and positioning, a name check (stores, GitHub,
USPTO/EUIPO marks, domains, meanings in major languages) and the owner's decision.

## Context

- VideoLAN's legal page allows the "VLC" mark only for GPL-compliant redistributions of VLC; any other use needs its
  prior permission. A remote that merely controls an installed VLC is not a redistribution, and Google Play suspends
  apps whose use of a mark may cause confusion.
- The product is outgrowing VLC: the plan adds YouTube in a browser, game streaming (Moonlight) and other players, all
  driven by the same companion. The value lives in the PC side (discovery, pairing, starting things on the TV,
  audio output, power, Bluetooth, history), not in VLC's buttons.
- The audience is international. The name had to be free in the app stores, on GitHub and as a domain, and
  pronounceable worldwide.

## Decision

- The product is **Sofabird**; the PC part is **Sofabird for Windows**. VLC is named only as what it controls
  ("remote for VLC media player"), with the "VLC is a trademark of VideoLAN; not affiliated" notice and no cone.
- **Everything is renamed, identifiers included** (owner's choice over keeping the old ones invisible):
  - Android `applicationId` and packages `app.sofabird` (debug `app.sofabird.debug`), the domain `sofabird.app` is the
    owner's; the GitHub repository `lex019020/sofabird` (GitHub redirects the old URLs), the site `sofabird.app`.
  - .NET projects `Sofabird.Core`, `Sofabird.Companion`, `Sofabird.Core.Tests`; the executable `Sofabird.exe`.
  - Velopack `packId` `Sofabird` (installed to `%LOCALAPPDATA%\Sofabird`) on its own update channel `win-x64`, so the
    old companion, which reads `releases.win.json`, never takes a Sofabird package for its own update.
  - Data in `%APPDATA%\Sofabird` (the installation folder belongs to Velopack and is removed on uninstall).
  - Discovery `_sofabird._tcp`, `SOFABIRD_DISCOVER`, the pairing link `sofabird://`; the protocol messages are
    unchanged (still v1). The Bluetooth service keeps its UUID; its SDP name is `Sofabird`.
- **Migration** (the rename is a breaking change: the phone app is a new app and pairs again):
  - The first start of Sofabird takes `settings.json`, `history.json` and `library-metadata.json` over from
    `%LOCALAPPDATA%\VlcRemote` (not the pairings), stops and uninstalls VLC Remote Companion, moves its autostart and
    renames its marks in VLC's folders (the vlcrc backup, the Lua pages' version file).
  - The old firewall rules no longer count as access; the setup window (item set 4) replaces them with one UAC prompt.
  - `deploy-windows.ps1` uninstalls the old companion before installing; `deploy-android.ps1` carries
    `%USERPROFILE%\.vlc-remote\devices.json` over and points at the old phone app, which the user removes.

## Consequences

- One-time reinstall on the phone and new pairing; history and library settings survive on the PC.
- Old links keep working through GitHub's redirects, except the old GitHub Pages address.
- Research documents in `docs/research/` keep the old name: they are a record of what was found at the time.
