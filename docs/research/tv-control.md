# TV control: power, volume, input

Research: can the companion (a .NET 9 tray app on the laptop by the TV) control the TV itself — turn it off
and on, change the volume, and if possible switch the input. The user's question: "can the TV be controlled
over the cable — power off, volume".

Date: 2026-10-13. Measurements were taken on the laptop by the TV via `scripts\dev-laptop.ps1`, network requests
from the developer PC (192.168.0.43). All experiments were **read-only**: no VCP values were written, no TV
WebSocket/remote connection was opened, the volume and input were not changed.

## Summary

- **TV:** Samsung **UE55H6200** (H series, 2014, Orsay OS, not Tizen), Wi-Fi, currently **192.168.0.31**.
- **Cable:** the laptop's HDMI output is driven by the **NVIDIA GeForce GTX 1050**, the TV is connected to input **HDMI 2**
  (CEC address from EDID `2.0.0.0`). PC graphics cards have no HDMI-CEC; **the TV does not support DDC/CI** (an I²C
  error on every request). The TV cannot be controlled over the cable itself without extra hardware.
- **Network:** the TV responds over UPnP — **volume and mute can be read without pairing** (`RenderingControl`), and the
  same service has `SetVolume`/`SetMute`. The network remote on the H series is an encrypted protocol with PIN pairing (ports 8080/8000);
  powering off through it is possible, **powering on over the network is impossible for this model**.
- **Recommendation:** 1) TV volume over UPnP (free, no TV settings); 2) power off — via the encrypted
  H/J protocol or a CEC adapter; 3) power on and input switching — only **HDMI-CEC** (a
  Pulse-Eight USB-CEC adapter ≈ $48 inline in the cable, or a Raspberry Pi on a free HDMI input) or an IR blaster.

## 1. The hardware (measurements)

Laptop MAIBENBEN ZiMai Z5 (`Win32_ComputerSystem`). Video adapters (`Win32_VideoController`):

| Adapter | Driver | PNP |
|---|---|---|
| NVIDIA GeForce GTX 1050 | 30.0.15.1289 | `PCI\VEN_10DE&DEV_1C8D&SUBSYS_10441D05` |
| Intel UHD Graphics 630 | 31.0.101.1999 | `PCI\VEN_8086&DEV_3E9B&SUBSYS_10441D05` |

So there **is** a discrete graphics card. `EnumDisplayDevices` in the user session:

```
\\.\DISPLAY1 | Intel(R) UHD Graphics 630   -> MONITOR\NCP0028  (built-in panel LM156LF9L01)
\\.\DISPLAY4 | NVIDIA GeForce GTX 1050     -> MONITOR\SAM0B60  (TV "SAMSUNG")
```

The HDMI port is wired to NVIDIA, not Intel (so the Intel "CEC over LSPCON" question is irrelevant for this
laptop). `WmiMonitorConnectionParams`: the TV has `VideoOutputTechnology = 5` (HDMI), the panel `0x80000000`
(internal).

TV EDID (registry `HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY\SAM0B60\…\Device Parameters\EDID`, 256 bytes,
1 CEA-861 extension): name `SAMSUNG`, panel manufacturing week/year 48/2013, in the HDMI VSDB (OUI `00-0C-03`)
**CEC physical address `2.0.0.0`** — the laptop is plugged into input HDMI 2. This will be needed for CEC (switching the input
to the laptop = `Active Source`/`Set Stream Path` with address `2.0.0.0`).

## 2. HDMI-CEC

### 2.1. Do PC graphics cards have CEC

- **No, as a rule.** Pulse-Eight states it plainly: "Almost all graphics cards do not support CEC in any form"
  ([pulse-eight.com](https://www.pulse-eight.com/p/104/usb-hdmi-cec-adapter)). The CEC line (HDMI pin 13)
  on PC graphics cards is usually not connected to a controller.
- **NVIDIA GeForce:** CEC is not supported on either Windows or Linux; a years-long feature-request thread
  on the NVIDIA forum has no official answer, CEC exists only on Tegra/Jetson
  ([forums.developer.nvidia.com](https://forums.developer.nvidia.com/t/hdmi-cec-support/31445)). Our HDMI is
  on the GTX 1050, so there is no native CEC.
- **Intel:** Linux has "CEC-Tunneling-over-AUX" (DisplayPort 1.3) for DP→HDMI adapters and LSPCON-like
  converters (`drm_dp_cec.c`, i915); however many converters (Parade PS176) do not wire the CEC pin,
  it works well on MegaChips 2900 ([patch i915](https://patchwork.kernel.org/patch/10066347/),
  [drm_dp_cec.c](https://cos.googlesource.com/third_party/kernel/+/96c2b8f3d7e33875c0d0b3a01f65fafdbacffa8a/drivers/gpu/drm/drm_dp_cec.c)).
  On Windows there is no equivalent in the Intel driver and no public Windows CEC API — "not verified" against a primary source
  (no Microsoft documentation on a CEC API could be found). Intel NUCs with CEC use a built-in Pulse-Eight board,
  not the GPU ([libCEC README](https://github.com/Pulse-Eight/libcec)). For our laptop the question is moot: HDMI is on NVIDIA.
- Built-in CEC support in Windows: **none** (we found neither an API nor a device class; "not verified" as
  proof of absence).

### 2.2. External adapters

**Pulse-Eight USB-CEC Adapter** (P8-USBCECv1), ≈ **$48**
([pulse-eight.com](https://www.pulse-eight.com/p/104/usb-hdmi-cec-adapter)):

- Connects **inline in the HDMI cable** between the laptop and the TV (HDMI in, HDMI out) + USB to the laptop. Passive — no
  repeater: total cable length ≈ 2 m for UHD and ≈ 5 m for 1080p; up to 4K60 4:2:0 8-bit. For our 1080p TV
  it fits. Alternative for a long cable: the adapter on **another** TV HDMI input, with the laptop connected directly.
- Shows up in Windows as USB CDC (a COM port), the driver comes with the libCEC installer.
- The **libCEC** library: Windows/Linux/macOS, .NET bindings (`LibCecSharp`, pure C# over the C API, x64/x86/arm64,
  .NET 8 samples in [cec-dotnet](https://github.com/Pulse-Eight/cec-dotnet)), Python, Node, Rust
  ([github.com/Pulse-Eight/libcec](https://github.com/Pulse-Eight/libcec)).
- **License: GPL-2.0-or-later or commercial** from Pulse-Eight
  ([LICENSE.md](https://github.com/Pulse-Eight/libcec/blob/master/LICENSE.md)). Embedding `LibCecSharp` in the
  companion makes the companion a derivative work under the GPL. It is safer to run **`cec-client.exe` as a separate process**
  (shipped with libCEC) and write commands to its stdin: `on 0`, `standby 0`, `as` (make the laptop the active
  source), `volup`, `voldown`, `mute`, `tx …` (raw frames). A process boundary is the usual way to keep the GPL
  out of your own program (legally "not verified", but common practice).
- libCEC also supports: Raspberry Pi (built-in CEC), Pulse-Eight NUC adapters, some Exynos/Amlogic.

**Raspberry Pi as a CEC bridge** (Pi Zero 2 W ≈ $15 + mini-HDMI cable + power): the Pi plugs into a **free**
TV HDMI input (CEC is a shared bus for all inputs), runs `cec-client`/`cec-ctl` and a small HTTP service, and the companion
sends it commands over the network. Pro — no break in the laptop's HDMI cable and no length limit; con — one more device
and its power supply (powered from the TV's USB it will lose power when the TV turns off — a separate power supply is needed, otherwise the TV cannot be turned on).
Switching the TV to the laptop's input from the Pi is a broadcast `Set Stream Path` (`0x86`) with address `2.0.0.0`
("not verified" on this TV).

### 2.3. What CEC gives on this TV

On Samsung, CEC is called **Anynet+ (HDMI-CEC)** and is enabled in the "System" menu (for 2014 the path starts with
`Menu > System > …`; [samsung.com](https://www.samsung.com/ae/support/tv-audio-video/how-to-use-anynet-on-your-samsung-tv/)).

| Function | CEC message | Expectation |
|---|---|---|
| Turn the TV off | `<Standby>` (0x36) to address 0 | works on Samsung, a standard Anynet+ function |
| Turn the TV on | `<Image View On>`/`<Text View On>` + `<Active Source>` | works if the TV is in standby with power (not unplugged) |
| Switch the input to the laptop | `<Active Source>` [2.0.0.0] | works with an inline adapter; with a Pi — `Set Stream Path` |
| Volume/mute | `<User Control Pressed>` Volume Up/Down/Mute | **usually only when an audio system is present (receiver/soundbar, ARC)**; the TV itself, as a rule, does not respond to volume commands from a source — "not verified" on the UE55H6200 |

CEC risks: Anynet+ sometimes turns the TV on/off by itself together with other devices
([samsung.com](https://www.samsung.com/pk/support/tv-audio-video/how-to-troubleshoot-the-samsung-tv-that-keeps-turning-off-by-itself));
an inline adapter is one more connector where the signal may drop.

## 3. DDC/CI over HDMI

Experiment (read-only) in the user's interactive session (a one-off scheduled task; the task and files
were deleted): `EnumDisplayMonitors` → `GetPhysicalMonitorsFromHMONITOR` → `GetCapabilitiesStringLength` and
`GetVCPFeatureAndVCPFeatureReply` for 0x10, 0x62, 0x8D, 0xD6, 0x60
([API](https://learn.microsoft.com/en-us/windows/win32/api/lowlevelmonitorconfigurationapi/nf-lowlevelmonitorconfigurationapi-getvcpfeatureandvcpfeaturereply)).
Raw results:

```
== HMONITOR \\.\DISPLAY1 (Intel, built-in panel) rect=0,0-1536,864 primary=True
  physical: 'Generic PnP Monitor'
  GetCapabilitiesStringLength failed err=0xC0262589 after 126 ms
  VCP 0x10/0x62/0x8D/0xD6/0x60: failed err=0xC0262589 (~60 ms each)
== HMONITOR \\.\DISPLAY4 (NVIDIA, TV SAMSUNG) rect=0,-864-1280,-144 primary=False
  physical: 'Generic PnP Monitor'
  GetCapabilitiesStringLength failed err=0xC0262582 after 0 ms
  VCP 0x10/0x62/0x8D/0xD6/0x60: failed err=0xC0262582 (0–5 ms each)
```

- `0xC0262582` = `ERROR_GRAPHICS_I2C_ERROR_TRANSMITTING_DATA` ("an error occurred while transmitting data to the device on the I²C bus"):
  the TV does not respond at the DDC/CI address (0x37). NVIDIA does pass DDC/CI over HDMI in principle (monitors are
  controlled through it), so the conclusion is — **this Samsung has no DDC/CI**. No volume (0x62), no power (0xD6), no input
  (0x60). TVs, as a rule, do not implement DDC/CI.
- `0xC0262589` = `ERROR_GRAPHICS_DDCCI_INVALID_MESSAGE_COMMAND` on the built-in eDP panel — a common occurrence,
  unrelated to the TV.

Bottom line: **DDC/CI is a dead end for this TV.**

## 4. Controlling the TV over the network

### 4.1. Finding the TV on the network

- ARP on both PCs: MAC `50-85-69-FC-3A-97` (Samsung OUI) appears at 192.168.0.24–.31, currently answers
  ping at **192.168.0.31**. The laptop's ARP table also had `50-56-BF-C0-50-13` at .28/.29 (also Samsung; it is part of the UDN
  of the TV's services — probably the MAC of the TV's wired port, "not verified"). In the `/api/v2/` response the `ip` field = `192.168.0.24`
  when queried at .31 — **the TV often changes its address via DHCP**. A DHCP reservation on the router for
  `50:85:69:FC:3A:97` is needed, or a search for the TV on every start (below).
- SSDP: a broadcast `M-SEARCH` from PowerShell got no replies (the replies come from the TV's address to our port rather than
  to the multicast address, and the Windows firewall drops them; the rules were not changed). Through the system SSDP service
  (`UPnP.UPnPDeviceFinder`, `upnp:rootdevice`) the TV was found — 4 root devices `[TV]Samsung LED55 / UE55H6200`:
  - `urn:samsung.com:device:MainTVServer2:1` → service `MainTVAgent2` (`:7676/smp_2_`)
  - `urn:samsung.com:device:RemoteControlReceiver:1` → `MultiScreenService` (`:7676/smp_7_`), capability
    `samsung:multiscreen:1` at `:8001/ms/1.0/`, `ProductCap` contains `Y2014,ENC`
  - `urn:schemas-upnp-org:device:MediaRenderer:1` (DLNA DMR, `:7676/smp_15_`) → `RenderingControl`,
    `ConnectionManager`, `AVTransport`
  - `urn:dial-multiscreen-org:device:dialreceiver:1` (DIAL, `:7676/smp_25_`)
- mDNS: a unicast query for `_samsungmsf._tcp.local` to `192.168.0.31:5353` → a reply, instance
  `068e7781-006e-1000-bbbf-5056bfc05013`.
- Open TCP ports on the TV: **7676** (UPnP), **8000** (socket.io), **8001** (MSF REST), **8080** (pairing).
  Closed: 8002, 9197, 55000 (the old pre-2014 remote), 52235, 1900/tcp.

### 4.2. `GET http://192.168.0.31:8001/api/v2/`

```json
{
  "id": "068e7781-006e-1000-bbbf-5056bfc05013",
  "name": "[TV]Samsung LED55",
  "version": "2.0.24",
  "device": {
    "type": "Samsung SmartTV", "model": "14_X14", "modelName": "UE55H6200",
    "description": "Samsung TV RCR", "networkType": "wireless", "ssid": "ABC_NET 5g",
    "ip": "192.168.0.24", "firmwareVersion": "Unknown", "resolution": "1920x1080",
    "countryCode": "RS", "msfVersion": "2.0.24", "smartHubAgreement": "true",
    "developerMode": "0", "developerIP": "0.0.0.0"
  },
  "type": "Samsung SmartTV",
  "uri": "http://192.168.0.31:8001/api/v2/"
}
```

There are **no `PowerState`, `TokenAuthSupport`, `wifiMac`, `OS` fields** — they appeared on 2016+ Tizen models. Model
`14_X14` = 2014, Orsay. `GET /api/v2/applications/111299001912` (YouTube) → `"running": false` (the DIAL status
of applications can be read without pairing).

### 4.3. Volume over UPnP/DLNA (`RenderingControl`) — works without pairing

The SCPD `:7676/smp_16_` contains `GetVolume`, `SetVolume`, `GetMute`, `SetMute`, `GetBrightness`/`SetBrightness`
etc.; control — `POST :7676/smp_17_`, `SOAPACTION: "urn:schemas-upnp-org:service:RenderingControl:1#GetVolume"`,
body `<InstanceID>0</InstanceID><Channel>Master</Channel>`. Measurement (Get only):

```
GetVolume -> 200 <CurrentVolume>0</CurrentVolume>
GetMute   -> 200 <CurrentMute>0</CurrentMute>
```

The response came without any permission prompt. Volume 0 — either it is set that way, or the TV was at that moment
in standby/on another input ("not verified": the TV's state at the time of the measurement was not observed). That `SetVolume`
on Samsung changes **the TV's main volume**, not just the DLNA player's, is known from SamyGO for models from the
B series onwards (forums, not a primary source: [forum.samygo.tv](https://forum.samygo.tv/viewtopic.php?p=11678),
[iobroker](https://forum.iobroker.net/post/77038) — which also has an example of a 500 response to `SetVolume` on another model); **`SetVolume` was not called on the UE55H6200 — "not verified"**.
The protocol is standard UPnP AV ([RenderingControl:1](https://upnp.org/specs/av/UPnP-av-RenderingControl-v1-Service.pdf)),
implementable in .NET on `HttpClient` in ~50 lines, no libraries needed.

`MainTVAgent2` (`:7676/smp_4_`) declares `GetSourceList`, `GetCurrentExternalSource`, **`SetMainTVSource`**
(input switching), `GetDTVInformation` etc., but `GetSourceList` and `GetCurrentExternalSource` returned
the UPnP error `501 Action Failed`. They may require pairing or work only while the TV is on —
"not verified". Input switching over the network this way is not confirmed yet.

### 4.4. Network remote on the H series (2014): an encrypted protocol

The H and J series (2014–2015, `ENC` in `ProductCap`) use **encrypted pairing with a PIN**, not the old port
55000 and not the 2016+ WebSocket. The sequence (per [PySmartCrypto in ha-samsungtv-encrypted](https://github.com/sermayoral/ha-samsungtv-encrypted)):

1. `GET :8080/ws/apps/CloudPINPage` — state of the PIN page;
2. `POST :8080/ws/apps/CloudPINPage` (body `pin4`) — **the TV shows the PIN**;
3. `GET :8080/ws/pairing?step=0&app_id=…&device_id=…&type=1`, `POST …step=1` (hello), `POST …step=2` (ack) —
   key exchange, resulting in an AES key and a `session_id`;
4. `DELETE :8080/ws/apps/CloudPINPage/run` — close the PIN page;
5. commands: `GET :8000/socket.io/1/` → `ws://…:8000/socket.io/1/websocket/<sid>`, `1::/com.samsung.companion`,
   then AES-encrypted frames with key codes (`KEY_POWEROFF`/`KEY_POWER`, `KEY_VOLUP`, `KEY_VOLDOWN`,
   `KEY_MUTE`, `KEY_SOURCE`, `KEY_HDMI`…).

The **H6200 model is listed as working** in [ha-samsungtv-encrypted](https://github.com/sermayoral/ha-samsungtv-encrypted);
the same project warns: on some models the token **expires** (in a week to a month) and is reset when the TV is unplugged
— re-pairing with the PIN is needed. **There is no power-on**: "turn on" only by an external action. The encryption is
partially reverse-engineered (a static key from the SmartView app); only Python/C implementations exist
([SmartCrypto/samsung_network_control](https://github.com/quan3t/samsung_network_control),
[samsungtvws `encrypted`](https://github.com/xchwarze/samsung-tv-ws-api)) — porting to C# is feasible but laborious
("not verified": the amount of work is an estimate).

Noticed during the research: `GET :8080/ws/pairing?step=0…` (without step 2, which shows the PIN) returned
`{"auth_data":""}`, `GET :8000/socket.io/1` — a session ID; this was a reachability check, the PIN page
was not requested. If anything did pop up on the TV, it came from these requests.

### 4.5. For reference: Samsung TVs 2016+ (Tizen) — not applicable to this TV

- WebSocket `ws://<tv>:8001/api/v2/channels/samsung.remote.control?name=<base64 name>` or
  `wss://<tv>:8002/…&token=<token>`; on the first connection the TV asks "Allow?", the token arrives in the
  `ms.channel.connect` event (`data.token`) and is stored by the client
  ([samsungtvws connection.py](https://github.com/xchwarze/samsung-tv-ws-api/blob/master/samsungtvws/connection.py)).
- Key press: `{"method":"ms.remote.control","params":{"Cmd":"Click","DataOfCmd":"KEY_VOLUP","Option":"false","TypeOfRemote":"SendRemoteKey"}}`
  ([remote.py](https://github.com/xchwarze/samsung-tv-ws-api/blob/master/samsungtvws/remote.py)).
- To stop it from asking every time — "Device Connection Manager → Access Notification → First Time Only";
  WebSocket does not work across subnets/VLANs ([Home Assistant](https://www.home-assistant.io/integrations/samsungtv)).
- Power-on — Wake-on-LAN to the TV's MAC with the "Power On with Mobile"/"Network Standby" setting enabled
  (same source). Our H series has no such feature (HA-encrypted: "no native power-on").

### 4.6. SmartThings (cloud)

REST API `api.smartthings.com` with OAuth or a personal token; the TV has the capabilities `switch` (on/off),
`audioVolume` (`setVolume`, `volumeUp/Down`), `audioMute`, `mediaInputSource` (`setInputSource`, list in
`supportedInputSources`) ([grenton](https://knowledgebase.grenton.com/en/sterowanie-telewizorem-poprzez-smarthings),
[community.smartthings.com](https://community.smartthings.com/t/setinputsource-syntax-for-samsungtv/219538)).
But: (1) a 2014 TV apparently **cannot be connected** to SmartThings (all examples are 2016+; "not verified");
(2) Samsung is winding down free API access from October 2026 — a Personal Plan at $4.99/month is needed
([home-assistant.io](https://www.home-assistant.io/integrations/smartthings)); (3) dependence on the internet.
**Not an option for this TV.**

### 4.7. IR blaster

The power button on the 2014 Samsung Smart Control remote is **IR** (the rest are Bluetooth)
([samsung.com](https://www.samsung.com/uk/support/tv-audio-video/how-to-pair-your-smart-control-remote-to-your-2014-model-tv/)).
A network IR blaster (Broadlink RM4 mini, ≈ $20–30, local protocol, [python-broadlink](https://github.com/mjg59/python-broadlink))
or a USB IR transmitter can do everything the remote does: power (on/off), volume, `SOURCE`/`HDMI`. Cons: it needs
a direct line of sight to the IR receiver, there is no feedback (it is unknown whether the TV is on — power is a toggle), the codes have to be
learned. Prices and the Broadlink protocol are "not verified" in practice in this project.

## 5. Windows facilities

- **Volume of the HDMI output device** (already exists: `sys systemVolume`, CoreAudio `IAudioEndpointVolume`) —
  this is digital attenuation of the signal before the TV; the TV's own volume does not change. A practical scheme without anything
  else: set the TV volume once to a comfortable maximum and adjust it from the phone with the Windows volume.
  Downside — at low levels bit depth is lost (unnoticeable in practice), and the TV volume on another input
  stays as it is.
- **Turning off the screen** (already exists: `sys displayPower`, `SC_MONITORPOWER`
  ([WM_SYSCOMMAND](https://learn.microsoft.com/en-us/windows/win32/menurc/wm-syscommand))) — both
  displays go dark; the TV shows "No Signal". A Samsung H series goes into standby by itself if
  "Eco Solution → No Signal Power Off" (15/30/60 min) is enabled
  ([samsung.com, H series](https://www.samsung.com/in/support/tv-audio-video/what-is-eco-solution-in-samsung-h-series-tv/)).
  The TV cannot be turned back on this way — a signal appearing on HDMI does not wake the TV without CEC ("not verified" on this TV).
- **Laptop screen only** (`SetDisplayConfig(SDC_TOPOLOGY_INTERNAL | SDC_APPLY)`, like Win+P "PC screen
  only"; [SetDisplayConfig](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setdisplayconfig))
  — the TV loses the signal (then as above), and the built-in panel stays on. Reverting — `SDC_TOPOLOGY_EXTEND`/
  `SDC_TOPOLOGY_CLONE` or a saved configuration. Risk: Windows moves windows, VLC ends up on the laptop
  panel — the companion already moves the window back to the preferred monitor when it reappears.
- Bottom line: Windows facilities give a "soft power-off" (via the TV's timer) and digital volume, but **not power-on** and
  not the real TV volume.

## 6. Summary table

| Method | Channel | Off | On | TV volume | Input | Cost | Effort | One-time setup |
|---|---|---|---|---|---|---|---|---|
| DDC/CI | HDMI | no | no | no | no | 0 | — | checked: the TV does not respond |
| CEC via GPU | HDMI | no | no | no | no | 0 | — | not available on NVIDIA/Intel under Windows |
| Pulse-Eight USB-CEC + `cec-client` | HDMI (inline) | yes | yes | usually no (only with an ARC audio system) | yes | ≈ $48 | medium | Anynet+ on, adapter, libCEC |
| Raspberry Pi + CEC | HDMI (free input) + LAN | yes | yes | usually no | yes (Set Stream Path) | ≈ $25–40 | medium-high | Pi, separate power supply, Anynet+ |
| UPnP `RenderingControl` | LAN | no | no | **yes** (Get checked, Set not) | no | 0 | low | IP reservation or SSDP search |
| UPnP `MainTVAgent2` | LAN | no | no | — | possibly (`SetMainTVSource`, currently 501) | 0 | low | not verified |
| Encrypted H/J remote (8080/8000) | LAN | yes | no | yes (keys) | yes (`KEY_SOURCE`/`KEY_HDMI`) | 0 | high (crypto, port from Python) | PIN on the TV, repeat when the token expires |
| IR blaster (Broadlink) | IR + LAN | yes* | yes* | yes | yes | ≈ $20–30 | medium | learning the codes, line of sight |
| SmartThings | cloud | — | — | — | — | $4.99/month | — | 2014 TV not supported (not verified) |
| `SC_MONITORPOWER` / `SetDisplayConfig` | Windows | indirectly (TV timer) | no | no | no | 0 | already exists / low | "No Signal Power Off" on the TV |
| CoreAudio HDMI | Windows | — | — | digital (not the TV) | — | 0 | already exists | TV volume at maximum |

\* IR power is a toggle, with no feedback about the state.

## 7. Recommendations

### What to do in the companion (in order)

1. **`tvVolume` over UPnP (right away, free).** TV discovery: SSDP via the system service or our own
   `M-SEARCH` + unicast polling, mDNS `_samsungmsf._tcp`, a cache of the last IP + UDN (`uuid:0a21fe80-…` for the DMR);
   read the description `/smp_15_` → the `RenderingControl` `controlURL` (do not hardcode `smp_17_`). Commands:
   `GetVolume`/`SetVolume`/`GetMute`/`SetMute`. Before implementing — **one manual experiment with the user's
   consent**: with the TV on, compare `GetVolume` with the volume on screen and do `SetVolume` +1.
   If the TV volume changes, this is the main win: the real TV volume without cables or pairing.
   Keep in mind: the Windows firewall drops replies to a broadcast `M-SEARCH` (the companion needs an inbound
   UDP rule for its exe, or to use `UPnPDeviceFinder`/`Windows.Devices.Enumeration` with the AEP SSDP protocol).
2. **"Soft" `tvPower off`** — `displayPower off` already exists; add a "laptop screen only" variant
   (`SetDisplayConfig`) and a hint in the UI: enable "No Signal Power Off" on the TV.
3. **`tvPower on/off` + `tvInput` via HDMI-CEC** — if the user buys a Pulse-Eight USB-CEC: the companion
   runs `cec-client.exe -t p -d 1` (the process is kept open), stdin commands `on 0`, `standby 0`, `as`;
   state — `pow 0`. Do not link `LibCecSharp` (GPL) without a commercial license. This is the only
   reliable way to **turn on** the TV and switch it to the laptop.
4. **Encrypted H/J remote** — only if a CEC adapter is not acceptable: gives power-off and volume/
   source keys, but requires porting crypto code from Python, PIN pairing and periodic re-pairing.
   Low priority.
5. SmartThings and DDC/CI — do not do.

### Protocol (proposal, not yet added to `protocol/README.md`)

- `caps`: `tvControl` (+ in `state.system.tv`: `{"available":bool,"backend":"upnp"|"cec"|…,"power":"on"|"standby"|"unknown","volume":int?,"mute":bool?}`).
- `sys` commands: `tvPower` `{"on": bool}`; `tvVolume` `{"value": 0..100}` | `{"delta": int}` | `{"mute": bool}`;
  `tvInput` `{"source": "companion"}` (make the laptop the active source) — as CEC becomes available.
- Errors: `tv_unavailable` (TV not found/off), `tv_unsupported` (the required channel is missing), texts via
  `ProtocolText`.
- Changes — simultaneously in `protocol/`, `android/core/protocol`, `companion/VlcRemote.Core` (project rule).

### What the user needs to do once

- Router: a **DHCP reservation** for the TV (`50:85:69:FC:3A:97` → e.g. 192.168.0.31) — the TV currently "wanders" across
  addresses .24–.31.
- TV: "Eco Solution → No Signal Power Off" (if the "turn off the screen → the TV goes to sleep by itself" scenario is wanted).
- To turn the TV on: buy a **Pulse-Eight USB-CEC** (≈ $48) and enable **Anynet+ (HDMI-CEC)** in the TV menu; or
  an IR blaster. Without this the UE55H6200 cannot be turned on programmatically.
- Allow one `SetVolume` experiment on the TV (see item 1).

### Risks

- A 2014 TV: the firmware is no longer updated, UPnP behaviour may differ from what is described on forums;
  `MainTVAgent2` answers `501`.
- UPnP has no authentication: anyone on the local network can change the TV volume (that was already the case, the companion
  makes nothing worse).
- CEC: Anynet+ may "pull along" the TV together with other devices; an inline adapter limits the cable length.
- libCEC's GPL — keep the process boundary.
- Encrypted protocol: the token expires, PIN re-pairing on the TV.

## Sources

- Pulse-Eight USB-CEC: https://www.pulse-eight.com/p/104/usb-hdmi-cec-adapter
- libCEC: https://github.com/Pulse-Eight/libcec, license https://github.com/Pulse-Eight/libcec/blob/master/LICENSE.md,
  .NET: https://github.com/Pulse-Eight/cec-dotnet
- NVIDIA and CEC: https://forums.developer.nvidia.com/t/hdmi-cec-support/31445
- Linux CEC-over-AUX: https://patchwork.kernel.org/patch/10066347/
- DDC/CI API: https://learn.microsoft.com/en-us/windows/win32/api/lowlevelmonitorconfigurationapi/nf-lowlevelmonitorconfigurationapi-getvcpfeatureandvcpfeaturereply
- SetDisplayConfig: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setdisplayconfig
- WM_SYSCOMMAND / SC_MONITORPOWER: https://learn.microsoft.com/en-us/windows/win32/menurc/wm-syscommand
- UPnP RenderingControl: https://upnp.org/specs/av/UPnP-av-RenderingControl-v1-Service.pdf
- Samsung volume over SOAP: https://forum.samygo.tv/viewtopic.php?p=11678, https://forum.iobroker.net/post/77038
- H/J encrypted protocol: https://github.com/sermayoral/ha-samsungtv-encrypted, https://github.com/quan3t/samsung_network_control
- samsungtvws (2016+ and `encrypted`): https://github.com/xchwarze/samsung-tv-ws-api
- Home Assistant Samsung TV: https://www.home-assistant.io/integrations/samsungtv; SmartThings: https://www.home-assistant.io/integrations/smartthings
- Anynet+: https://www.samsung.com/ae/support/tv-audio-video/how-to-use-anynet-on-your-samsung-tv/
- H series Eco Solution: https://www.samsung.com/in/support/tv-audio-video/what-is-eco-solution-in-samsung-h-series-tv/
- Smart Control 2014 (IR power): https://www.samsung.com/uk/support/tv-audio-video/how-to-pair-your-smart-control-remote-to-your-2014-model-tv/
- Broadlink: https://github.com/mjg59/python-broadlink
