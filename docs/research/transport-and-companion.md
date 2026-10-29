# Transport and the Windows companion for the VLC remote: research

Date: 2026-10-12. Context: an Android remote (Kotlin/Compose) for VLC 3.0.x on a Windows 11 laptop
connected to a TV. The companion on the PC is optional (see
[ADR-0001](../adr/0001-control-paths.md), level 2). The developer knows C#/.NET and Kotlin;
.NET SDK 9.0.310 and 10.0.102 are installed on the PC.

Legend:
- **verified locally** — done on this PC (Windows 11 Pro 10.0.26200, adapter
  "MediaTek Bluetooth Adapter", VLC 3.0.23, user without administrator rights);
- **not verified** — a claim from documentation/the community, or a conclusion that should be
  confirmed by an experiment.

The sources of the probe programs (`BtProbe`, `DnsSdProbe`, size measurements) were kept in the
session's scratch folder; the key fragments are repeated in the text.

---

## 0. Short summary

| Question | Answer |
|---|---|
| Bluetooth without a LAN | **Classic RFCOMM** via WinRT `RfcommServiceProvider` + `StreamSocketListener`. Works from an ordinary unpackaged .NET 9 application without administrator rights — **verified locally**. Android: `createRfcommSocketToServiceRecord(uuid)` + `BLUETOOTH_CONNECT`; the system itself shows the pairing prompt on `connect()`. |
| BLE instead of RFCOMM | Possible (`GattServiceProvider`; this PC's adapter supports the peripheral role — verified), but worse: short messages, own fragmentation, no stream. Not recommended as the primary channel. |
| Bluetooth without code | Bluetooth PAN (the phone shares its network over Bluetooth, Windows 11 "Join" in the settings) gives an ordinary IP channel; the PC gets an address from 192.168.44.x (Android code). Caveat: Windows will assign this network to the "Public" profile — firewall rules for Public are needed (not verified). |
| Discovery in the LAN | mDNS/DNS-SD via the **built-in Windows responder**: Win32 `DnsServiceRegister` (P/Invoke, no libraries) or WinRT `DnssdServiceInstance` — both **verified locally**. Third-party .NET libraries: Makaretu — dead since 2019, Zeroconf and Tmds.MDns — browsing only (cannot advertise). On Android — `NsdManager` with protection against its bugs + a fallback UDP beacon + manual entry. |
| Plain VLC without the companion | Does not announce itself via mDNS or in any other way (verified in the `http.lua` sources of the 3.0.x and master branches). What remains: a /24 scan on port 8080 (401 with `realm="VLC stream"`), or the name `<PC>.local` (Windows answers to its mDNS name, Android can resolve `.local`). |
| Protocol server | **Kestrel minimal API + WebSocket** (no HTTP.sys and no `urlacl`). Cost: +25.6 MB self-contained (+11.2 MB in a compressed single-file) — measured. "Zero dependencies" alternative: `TcpListener` + `WebSocket.CreateFromStream`. `HttpListener` is unsuitable (needs `netsh http add urlacl` with administrator rights for non-localhost). |
| Framing | NDJSON (one JSON line per message), the same over RFCOMM and WebSocket. |
| VLC ↔ companion | HTTP (like the phone) + one RC socket on `127.0.0.1` for events/`adev`/`atrack`. Launching `vlc.exe` with arguments; editing `vlcrc` only while VLC is stopped and preserving the `#key=value` format. |
| Screen/TV | `Screen.AllScreens` + `SetWindowPos` of the VLC window onto the TV monitor, then `fullscreen`; `SetDisplayConfig(SDC_TOPOLOGY_*)`; `SC_MONITORPOWER`; `SetThreadExecutionState`; `SetSuspendState`/`shutdown.exe`. HDMI-CEC only with the Pulse-Eight USB adapter. WoL over Wi-Fi is unreliable. |
| Sound | First — switching the device **inside VLC** (RC `adev`); the system default device — via the undocumented `IPolicyConfig` (like SoundSwitch/Sunshine). Windows volume — `IAudioEndpointVolume` (NAudio). |
| Companion form | WinForms tray (`NotifyIcon`) + Kestrel in a single `WinExe`; autostart via `HKCU\...\Run`; install/update with **Velopack** into `%LocalAppData%` without UAC; single-file self-contained ≈ 59 MB (measured). |

---

## 1. Bluetooth without a local network

### 1a. Classic Bluetooth RFCOMM (SPP) — a server on Windows from .NET

**API.** The `Windows.Devices.Bluetooth.Rfcomm` namespace (WinRT): `RfcommServiceProvider.CreateAsync(RfcommServiceId)`
creates a local service, `StreamSocketListener.BindServiceNameAsync(serviceId, protectionLevel)`
binds the listener to the service, `provider.SdpRawAttributes` — extra attributes of the SDP record,
`provider.StartAdvertising(listener, radioDiscoverable)` publishes the record
([CreateAsync](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.rfcomm.rfcommserviceprovider.createasync),
[StartAdvertising](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.rfcomm.rfcommserviceprovider.startadvertising),
[BindServiceNameAsync](https://learn.microsoft.com/en-us/uwp/api/windows.networking.sockets.streamsocketlistener.bindservicenameasync),
guide [Send or receive files with RFCOMM](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/send-or-receive-files-with-rfcomm),
sample [BluetoothRfcommChat](https://github.com/microsoft/Windows-universal-samples/tree/main/Samples/BluetoothRfcommChat)).
From .NET everything is available without NuGet with `TargetFramework` `net9.0-windows10.0.19041.0` or higher
(built-in C#/WinRT projections).

**Does it work from an ordinary desktop application without a manifest and without administrator rights — verified
locally: yes.** The documentation requires the `bluetooth` capability (that is about packaged/UWP), but a console
`net9.0-windows10.0.19041.0` exe, run as `<PC>\<user>` without elevation
(`packaged=False`), successfully performed `CreateAsync` → `BindServiceNameAsync` → `SdpRawAttributes.Add`
→ `StartAdvertising(listener, false)` → `StopAdvertising`. Probe output:

```
User: <PC>\<user>, elevated(admin)=False
OS: Microsoft Windows NT 10.0.26200.0, packaged=False
BluetoothAdapter: addr=021A2B3C4D60 classic=True le=True central=True peripheral=True advOffload=True extAdv=True
RFCOMM: advertising OK, serviceId={C0F1B7A2-5C0E-4B3E-9A0E-1F2D3C4B5A69}
RFCOMM: stopped
```

Minimal server (C#, net9.0-windows10.0.19041.0):

```csharp
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

static readonly Guid ServiceUuid = Guid.Parse("c0f1b7a2-5c0e-4b3e-9a0e-1f2d3c4b5a69"); // our own 128-bit UUID

var provider = await RfcommServiceProvider.CreateAsync(RfcommServiceId.FromUuid(ServiceUuid));
var listener = new StreamSocketListener();
listener.ConnectionReceived += async (_, e) =>
{
    // one client at a time; a byte stream — NDJSON on top of it (see section 3d)
    using var socket = e.Socket;
    using var input  = socket.InputStream.AsStreamForRead();
    using var output = socket.OutputStream.AsStreamForWrite();
    await session.RunAsync(input, output);           // code shared with the WebSocket transport
};
await listener.BindServiceNameAsync(provider.ServiceId.AsString(),
    SocketProtectionLevel.BluetoothEncryptionWithAuthentication);      // require pairing+encryption

// SDP attribute 0x0100 = ServiceName (string, 8-bit length)
var w = new DataWriter { UnicodeEncoding = UnicodeEncoding.Utf8 };
w.WriteByte(0x25); w.WriteByte((byte)"VLC Remote".Length); w.WriteString("VLC Remote");
provider.SdpRawAttributes.Add(0x0100, w.DetachBuffer());

provider.StartAdvertising(listener, radioDiscoverable: false);
```

Notes on the code:
- The probe used `BluetoothEncryptionAllowNullAuthentication` (as in the Microsoft sample);
  `BluetoothEncryptionWithAuthentication` to force pairing is **not verified** together with
  Android; check during implementation.
- Windows assigns the RFCOMM channel dynamically; the client must look it up by UUID via SDP (Android
  does this in `connect()`), so a fixed channel number is not needed.
- After `StopAdvertising`/resume from sleep it is better to recreate the listener (not verified, but
  cheaper than hunting down a "hung" listener).

**SDP record and UUID.** An own 128-bit UUID is mandatory (not SPP `00001101-…`), so that the phone
does not confuse the companion with COM ports. ServiceName (0x0100) is useful for debugging scanners.

**Is pairing needed and who initiates it.**
- An Android client with a "secure" socket requires a bonded device; if there is no pairing, "the Android
  framework automatically shows a pairing request notification or dialog to the user during
  the connection procedure" and `connect()` blocks until pairing completes
  ([Connect Bluetooth devices](https://developer.android.com/develop/connectivity/bluetooth/connect-bluetooth-devices)).
- On the Windows side an incoming pairing request is shown by default as an
  "Add a device" notification (as described on [ElevenForum](https://www.elevenforum.com/t/enable-or-disable-notification-for-new-bluetooth-device-connection-in-windows-11.44957/);
  **not verified** on this PC). This is the simplest scenario: the user pairs the phone with the PC
  once from the Windows or Android settings.
- Pairing can be initiated programmatically from the companion:
  `DeviceInformation.Pairing.Custom.PairAsync(DevicePairingKinds.ConfirmPinMatch|…)` with a
  `PairingRequested` handler ([DeviceInformationPairing](https://learn.microsoft.com/en-us/uwp/api/windows.devices.enumeration.deviceinformationpairing),
  [DeviceInformationCustomPairing](https://learn.microsoft.com/en-us/uwp/api/windows.devices.enumeration.deviceinformationcustompairing)).
  Then the phone must be "discoverable" (on Android — the Bluetooth settings screen is open). Working from an
  unpackaged application — **not verified** (not called in the probe), but the API is from the same family as the
  verified RFCOMM.

**Must the PC be "discoverable".** Only for pairing initiated from the phone. To connect an
already paired phone it is enough for the radio to be on (ordinary Bluetooth behaviour —
paging by a known address; **not verified** for this exact combination, but that is how any
headset works). Switches:
- programmatically — `StartAdvertising(listener, radioDiscoverable: true)` ("Indicates whether the radio
  is discoverable") — for the duration of pairing, then restart with `false`;
- manually — Settings → Bluetooth & devices → Devices → More Bluetooth settings → "Allow Bluetooth
  devices to find this PC" ([description](https://geekrewind.com/how-to-turn-allow-bluetooth-devices-to-find-this-pc-on-or-off-in-windows-11/)).

**Rights.** Administrator not needed — verified locally.

**Throughput / latency.** Classic BR/EDR: 1 Mbit/s (BR), 2/3 Mbit/s (EDR); at the
application level ~720 kbit/s (BR) and ~2.1 Mbit/s (EDR) ([ib-lenhardt](https://ib-lenhardt.com/kb/glossary/bluetooth-classic-br-edr));
practical SPP estimates 0.7–2.1 Mbit/s ([GT student report](https://ece4012y201902.ece.gatech.edu/spring/sd19p49/trpethanvargas.docx)).
For remote commands (hundreds of bytes) this is more than enough; do not push cover art/thumbnails over Bluetooth
(or push compressed ones ≤ 50 KB). I did not find round-trip latency in the sources; tens of ms are expected —
**not verified**, measure in the prototype.

**Known pitfalls.**
- Bluetooth adapter power saving: Microsoft advises unchecking "Allow the computer to turn off
  this device to save power" ([Bluetooth keeps disconnecting](https://support.microsoft.com/windows/bluetooth-keeps-disconnecting-in-windows-81a9cb1e-d15c-4e72-b025-cc0d992dca9f));
  Dell writes that since Windows 10 2004 this flag no longer exists on the adapter itself, it remains on HID devices
  ([Dell KB](https://dell.com/support/kbdoc/en-us/000122996)). For the companion's RFCOMM session this
  means: expect drops after idle/sleep and reconnect from the phone.
- Modern Standby: when the laptop goes to sleep the RFCOMM socket dies; the companion must recreate the
  listener after `SystemEvents.PowerModeChanged`/`Resume` (not verified).
- Android caches SDP records; when the UUID/channel changes, `device.fetchUuidsWithSdp()` helps
  (common knowledge, not verified).
- One client at a time: a second `ConnectionReceived` — reject it or evict the first one.

**32feet.NET (InTheHand.Net.Bluetooth).** Alive: 4.2.x, releases in 2025–2026 (4.2.2 — 06/2025, 4.2.3 —
02/2026, 4.2.4 — 04/2026 per the NuGet profile; versions ≥ 4.2.3 — inferred from the publisher profile, **not
verified** on the package page), MIT, targets .NET 9/.NET Standard 2.0/.NET Framework
([NuGet](https://www.nuget.org/packages/InTheHand.Net.Bluetooth), [GitHub](https://github.com/inthehand/32feet)).
On Windows it claims RFCOMM client and server, discovery, pickers; GATT server — "planned"
([wiki](https://github.com/inthehand/32feet/wiki)). Which API it uses under the hood on Windows
(Win32 Winsock or WinRT) is not stated in the documentation — **not verified**. Since plain WinRT
is verified and works, 32feet is only needed if the `BluetoothListener`/
`BluetoothClient` API style or pairing via `BluetoothSecurity.PairRequest` is needed. Recommendation: no 32feet.

### 1b. Android client

**Socket.** `BluetoothDevice.createRfcommSocketToServiceRecord(uuid)` — "secure outgoing connection
… using SDP lookup of uuid", the remote side is authenticated, the channel is encrypted; MITM protection
exists only with an authenticated link key. `createInsecureRfcommSocketToServiceRecord(uuid)` —
"will not have an authenticated link key", vulnerable to MITM; on devices ≥ 2.1 the channel is encrypted anyway
(encryption mandatory), on pre-2.1 — not
([BluetoothDevice](https://developer.android.com/reference/android/bluetooth/BluetoothDevice),
[javadoc source](https://android.googlesource.com/platform/packages/modules/Bluetooth/+/refs/heads/main/framework/java/android/bluetooth/BluetoothDevice.java)).
Use the **secure** variant. `connect()` is blocking (timeout ~12 s), run it off the UI thread;
always call `adapter.cancelDiscovery()` before it
([Connect Bluetooth devices](https://developer.android.com/develop/connectivity/bluetooth/connect-bluetooth-devices)).

**Permissions (API 31+).** From [Bluetooth permissions](https://developer.android.com/develop/connectivity/bluetooth/bt-permissions):

```xml
<!-- Needed only if the app searches for devices itself (startDiscovery). -->
<uses-permission android:name="android.permission.BLUETOOTH_SCAN"
                 android:usesPermissionFlags="neverForLocation" />
<!-- Talking to already paired devices. -->
<uses-permission android:name="android.permission.BLUETOOTH_CONNECT" />
<!-- Old versions -->
<uses-permission android:name="android.permission.BLUETOOTH" android:maxSdkVersion="30" />
<uses-permission android:name="android.permission.BLUETOOTH_ADMIN" android:maxSdkVersion="30" />
```

All three new ones are runtime permissions. If the device is picked from `getBondedDevices()` or via
CDM, `BLUETOOTH_CONNECT` is enough; `BLUETOOTH_SCAN` is only needed for our own `startDiscovery()`
(with `neverForLocation` — without `ACCESS_FINE_LOCATION`; CDM "doesn't require location permissions").

**Companion Device Manager** — a nicer selection/pairing UX: a system dialog, the system performs the
scan, no location permission; after association one can request
`REQUEST_COMPANION_RUN_IN_BACKGROUND`, `REQUEST_COMPANION_START_FOREGROUND_SERVICES_FROM_BACKGROUND`
and observe device presence (`startObservingDevicePresence`, since API 36 — a variant with
`ObservingDevicePresenceRequest` and binding a `CompanionDeviceService` while the device is "within BLE
range or connected using Bluetooth"). CDM "doesn't create connections on its own" — the socket
has to be opened by ourselves ([Companion device pairing](https://developer.android.com/develop/connectivity/bluetooth/companion-device-pairing)).

```kotlin
// API 33+
val filter = BluetoothDeviceFilter.Builder()
    .addServiceUuid(ParcelUuid(SERVICE_UUID), null)   // only PCs with our RFCOMM service
    .build()
val request = AssociationRequest.Builder()
    .addDeviceFilter(filter)
    .setSingleDevice(false)
    .build()
val cdm = getSystemService(CompanionDeviceManager::class.java)
cdm.associate(request, mainExecutor, object : CompanionDeviceManager.Callback() {
    override fun onAssociationPending(intentSender: IntentSender) =
        chooserLauncher.launch(IntentSenderRequest.Builder(intentSender).build())
    override fun onAssociationCreated(info: AssociationInfo) {
        // info.deviceMacAddress -> save it, then getRemoteDevice(mac) + createBond() if needed
    }
    override fun onFailure(error: CharSequence?) { /* show the error */ }
})
```

Caveat: the service UUID filter in CDM relies on the PC's SDP/EIR data; whether the Windows RFCOMM
service is visible to this filter during a scan is **not verified**; the fallback is `setNamePattern`
by the PC name or picking from `getBondedDevices()`.

**Connecting and reading (Kotlin, NDJSON):**

```kotlin
class BtLink(private val device: BluetoothDevice, private val adapter: BluetoothAdapter) {
    private var socket: BluetoothSocket? = null

    @RequiresPermission(Manifest.permission.BLUETOOTH_CONNECT)
    suspend fun connect() = withContext(Dispatchers.IO) {
        adapter.cancelDiscovery()
        val s = device.createRfcommSocketToServiceRecord(SERVICE_UUID)
        s.connect()                       // blocks; if not paired the system shows a dialog
        socket = s
    }

    fun lines(): Flow<String> = flow {
        val reader = socket!!.inputStream.bufferedReader(Charsets.UTF_8)
        while (currentCoroutineContext().isActive) {
            val line = reader.readLine() ?: break
            if (line.isNotBlank()) emit(line)
        }
    }.flowOn(Dispatchers.IO)

    suspend fun send(json: String) = withContext(Dispatchers.IO) {
        socket!!.outputStream.run { write((json + "\n").toByteArray()); flush() }
    }
}
```

**Reconnecting.** Connect when the app comes to the foreground; on `IOException` —
exponential pause 1→2→4→8 s (up to ~30 s) and retry, meanwhile showing "looking for the PC…"; listen to
`BluetoothDevice.ACTION_ACL_CONNECTED/DISCONNECTED` and `BluetoothAdapter.ACTION_STATE_CHANGED`
so as not to hammer a switched-off radio. If N attempts fail — offer LAN/manual IP.

**Background.** For a remote it is not necessary to keep the socket in the background: close it ~1 min after going to the
background. If a "now playing" notification with buttons is needed — a foreground service of type
`connectedDevice` (`FOREGROUND_SERVICE_CONNECTED_DEVICE`, a mandatory type since Android 14;
Google advises preferring the CDM presence API for long-lived connections)
([FGS types](https://developer.android.com/develop/background-work/services/fgs/service-types)).

### 1c. BLE alternative: Windows as a GATT peripheral

`GattServiceProvider` (since 10.0.15063) lets Windows advertise a service and characteristics; it needs an
adapter with the peripheral role (`BluetoothAdapter.IsPeripheralRoleSupported`; `True` on this PC —
verified locally; there are reports of adapters without support
([SO](https://windows-hexerror.linestarve.com/q/so48956229-gattserviceproviderstartadvertising-fails-bluetooth-device-issue))).
Limitations: "there is no explicit functionality associated with this API to disconnect" — disconnecting
only from the central side; Windows "usually operates in the client role"
([GattServiceProvider](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.genericattributeprofile.gattserviceprovider),
[GATT server guide](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/gatt-server)).
In practice: the default ATT MTU is 23 bytes (20 of payload), up to 512 with `requestMtu` — own
JSON fragmentation is needed; no stream semantics, notifications are limited; on the other hand pairing is not
mandatory and power consumption is lower (general BLE properties, **not verified** in this combination).

Comparison: RFCOMM — a stream, ~1–2 Mbit/s, one pairing, `readLine()`; BLE — messages ≤ 512 B,
an own fragmentation protocol, two stacks (GATT server on Windows + GATT client on Android), more
code and more "quirks" on different phones. **Recommendation: RFCOMM**; keep BLE as
plan B in case it turns out that SDP/RFCOMM to Windows is unstable on some phones.

### 1d. Fallback without code: Bluetooth PAN (tethering)

- Windows 11 can join a Bluetooth PAN from the settings: Settings → Bluetooth & devices →
  Devices → expand the phone → "Personal Area Network (PAN)" → Join → "Access Point" → Connect;
  disconnecting — "Disconnect PAN" ([Microsoft Support](https://support.microsoft.com/en-us/windows/connect-to-a-bluetooth-network-in-windows-0d371763-6297-4eef-9d39-f8c469d181c2)).
  Requirements: the devices are paired, Bluetooth tethering is enabled on the phone. According to
  community reports, the button appeared in build 22631.1906 ([ElevenForum](https://elevenforum.com/t/connect-to-bluetooth-personal-area-network-btpan-in-windows-11.20046/)); this PC has build 26200.
- On Android: Settings → Network & internet → Hotspot & tethering → Bluetooth tethering (the path
  depends on the vendor skin; **not verified**). Whether tethering is possible when the phone has no internet
  depends on the firmware (**not verified**; on many devices the switch is available).
- Addressing (from the Android code `TetheringConfiguration.java`): "BT is limited to max default of 5
  connections. 192.168.44.1 to 192.168.48.1"; the legacy DHCP range for BT is `192.168.44.2–254`
  (USB — 192.168.42.x, Wi-Fi — 192.168.43.x) ([source](https://android.googlesource.com/platform/packages/modules/Connectivity/+/refs/heads/main/Tethering/src/com/android/networkstack/tethering/TetheringConfiguration.java)).
  The array is named `LEGACY_…` — in newer versions the prefix may be chosen differently, so **do not
  hard-code 192.168.44.x**: the phone is the gateway, the PC gets an address via DHCP. How the phone finds the PC: the PC
  announces itself (mDNS registers on all interfaces, UDP beacon), or the phone scans
  its PAN /24 (254 addresses — fast). `NsdManager` working over the `bt-pan` interface — **not
  verified**.
- The main caveat: Windows will assign the PAN to the "Public network" profile, where incoming connections
  are blocked more strictly; the firewall rule for the companion and VLC must include the Public profile
  (expected, **not verified**).
- Plus: everything "LAN-based" works over PAN, including level 0 (direct VLC HTTP), without the companion.
  Minus: manual clicks on both sides for every session (PAN auto-connect in Windows — **not
  verified**), and the phone has to keep tethering on meanwhile.

### 1e. Wi-Fi Direct / hotspot on the phone — briefly

- **Phone hotspot**: the PC connects to the phone's Wi-Fi access point, gets an IP (historically
  192.168.43.x), then everything works as in a LAN. The most reliable "no router" option, but the PC loses its
  Wi-Fi internet (if there is no Ethernet), and the phone drains its battery.
- **Windows hotspot ("Mobile hotspot")**: the phone connects to the laptop. Works only if the
  PC has a connection that it "shares" (a Windows limitation; **not verified** for the case without
  internet).
- **Wi-Fi Direct**: the Windows API `Windows.Devices.WiFiDirect` (`WiFiDirectAdvertisementPublisher`,
  `WiFiDirectConnectionListener`) is alive, while `Windows.Devices.WiFiDirect.Services` is marked deprecated
  ([namespace](https://learn.microsoft.com/en-us/uwp/api/windows.devices.wifidirect.services)); on
  Android — `WifiP2pManager`. Windows↔Android P2P compatibility has historically been fickle, the code on both
  sides is non-trivial — **not recommended**.

---

## 2. Discovering the PC from the phone in the LAN

### 2a. mDNS / DNS-SD

**Android, `NsdManager`.** A long history of bugs: empty TXT records, "onServiceLost → immediately
onServiceFound" (flapping), quirks with several simultaneous discoveries
([TinyDNSSD](https://github.com/youviewtv/tinydnssd), [RxBonjour → jmDNS](https://github.com/Ennova-IT/RxBonjour),
[Guardian Project](https://dev.guardianproject.info/issues/2900)). What has changed:
- API 34: `registerServiceInfoCallback(…)` instead of `resolveService` (the latter is deprecated —
  the documentation mentions both API 34 and API 36), `NsdServiceInfo.getHostAddresses()` instead of
  `getHost()` ([NsdManager](https://developer.android.com/reference/android/net/nsd/NsdManager),
  [NsdServiceInfo](https://developer.android.com/reference/android/net/nsd/NsdServiceInfo)).
- Starting with "T extensions 7" the system itself manages multicast reception for foreground apps;
  previously `WifiManager.MulticastLock` + `CHANGE_WIFI_MULTICAST_STATE` were required
  ([NsdManager](https://developer.android.com/reference/android/net/nsd/NsdManager),
  [MulticastLock](https://developer.android.com/reference/android/net/wifi/WifiManager.MulticastLock)).
- After Android 12 the native daemon does not have to be alive; use only `NsdManager`.

Recommended approach on the phone:
1. One active `discoverServices("_vlcremote._tcp", PROTOCOL_DNS_SD, …)` at a time; stop in `onStop`.
2. On API 34+ — `registerServiceInfoCallback` for the found record; below that — `resolveService` with a
   queue (no more than one resolve at a time).
3. Debounce `onServiceLost` (2–3 s) before removing from the list.
4. Hold a `MulticastLock` during discovery on API < 34 (and just in case, in general).
5. In parallel send the UDP beacon (2b) and offer manual entry of `host:port`/QR.
6. Do not rely on TXT: return everything needed (PC name, protocol version, fingerprint) over HTTP
   `GET /info` after discovery.

**Windows — what to advertise the service with.**

| Option | Advertises? | Status | Verdict |
|---|---|---|---|
| Win32 `DnsServiceRegister` (dnsapi.dll, windns.h), the built-in Windows responder | yes | documented, Windows 10+, "registration is tied to the lifetime of the calling process" ([DnsServiceRegister](https://learn.microsoft.com/en-us/windows/win32/api/windns/nf-windns-dnsserviceregister), [DnsServiceConstructInstance](https://learn.microsoft.com/en-us/windows/win32/api/windns/nf-windns-dnsserviceconstructinstance)) | **recommended**; **verified locally** — registering and deregistering `VlcRemoteProbe._vlcremote._tcp.local` returned status 0 |
| WinRT `Windows.Networking.ServiceDiscovery.Dnssd.DnssdServiceInstance` | yes | works, but `RegisterStreamSocketListenerAsync` is tied to a WinRT `StreamSocketListener`; `DnssdServiceWatcher` is marked "not supported" ([namespace](https://learn.microsoft.com/en-us/uwp/api/windows.networking.servicediscovery.dnssd)) | **verified locally** (`status=Success`), but awkward next to Kestrel — the port is not held by a WinRT listener |
| Makaretu.Dns.Multicast (net-mdns) | yes (`ServiceDiscovery.Advertise`) | 0.27.0 of 05.11.2019, no releases since ([NuGet](https://www.nuget.org/packages/Makaretu.Dns.Multicast), [GitHub](https://github.com/richardschneider/net-mdns)); its own responder on 5353 next to the built-in one (conflicts are described for other stacks: [Gajim #9245](https://dev.gajim.org/gajim/gajim/-/issues/9245)) | no |
| Zeroconf (novotnyllc) 3.7.16 | **no**, browse/resolve only ([README](https://github.com/novotnyllc/Zeroconf)) | alive (push 10/2025) | unsuitable for the PC side |
| Tmds.MDns 0.8.0 | **no**, `ServiceBrowser` only ([README](https://github.com/tmds/Tmds.MDns)) | 04/2023 | unsuitable |
| Mono.Zeroconf 0.9.0 | yes | archived 2011, requires Bonjour/Avahi ([mono-project](https://www.mono-project.com/archived/monozeroconf/)) | no |

Verified P/Invoke (`net9.0-windows` is enough, no WinRT):

```csharp
static class DnsSd
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate void RegisterComplete(uint status, IntPtr ctx, IntPtr instance);

    [StructLayout(LayoutKind.Sequential)]
    public struct DNS_SERVICE_REGISTER_REQUEST
    {
        public uint Version;                 // 1
        public uint InterfaceIndex;          // 0 = all interfaces
        public IntPtr pServiceInstance;
        public IntPtr pRegisterCompletionCallback;
        public IntPtr pQueryContext;
        public IntPtr hCredentials;
        public int unicastEnabled;
    }

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr DnsServiceConstructInstance(string serviceName, string hostName,
        IntPtr ip4, IntPtr ip6, ushort port, ushort priority, ushort weight, uint propsCount,
        [In, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] keys,
        [In, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] values);
    [DllImport("dnsapi.dll")] public static extern void DnsServiceFreeInstance(IntPtr inst);
    [DllImport("dnsapi.dll")] public static extern uint DnsServiceRegister(ref DNS_SERVICE_REGISTER_REQUEST r, IntPtr cancel);
    [DllImport("dnsapi.dll")] public static extern uint DnsServiceDeRegister(ref DNS_SERVICE_REGISTER_REQUEST r, IntPtr cancel);
}

// usage (DNS_REQUEST_PENDING = 9506, in the callback status 0 = success)
var inst = DnsSd.DnsServiceConstructInstance(
    $"{Environment.MachineName}._vlcremote._tcp.local", $"{Environment.MachineName}.local",
    IntPtr.Zero, IntPtr.Zero, 47555, 0, 0, 2, new[] { "v", "id" }, new[] { "1", pcId });
var req = new DnsSd.DNS_SERVICE_REGISTER_REQUEST { Version = 1, pServiceInstance = inst,
    pRegisterCompletionCallback = Marshal.GetFunctionPointerForDelegate(callback) };
uint rc = DnsSd.DnsServiceRegister(ref req, IntPtr.Zero);   // 9506 → wait for the callback
```

Keep the `callback` delegate in a field (GC); on exit — `DnsServiceDeRegister` (also asynchronous) and
`DnsServiceFreeInstance`. When the process exits, Windows removes the record itself.

In addition: Windows 10 1703+ answers to `<PC-name>.local` with the built-in responder
([technote](https://slightfuture.com/technote/windows-mdns-dnssd/), **not verified** locally), and
the Android resolver since November 2021 (the DnsResolver module) transparently resolves `*.local` via
`getaddrinfo()` (except over VPN and mobile data) ([AOSP DNS Resolver](https://source.android.com/docs/core/ota/modular-system/dns-resolver)).
That is, the user can enter `<pc-name>.local` instead of an IP — a handy fallback even without
the companion.

### 2b. UDP beacon

Scheme: the phone sends `{"q":"vlcremote","v":1}` to `255.255.255.255:47556` and to the directed broadcast
of its subnet; the companion replies with unicast `{"name":"DESKTOP-…","port":47555,"id":"…","v":1}`.
An alternative is for the companion to beacon by itself every 2 s (worse: network noise, the phone must listen).
Pros: 40 lines of code on both sides, independent of mDNS stacks. Cons: some routers drop
broadcast; with **AP/client isolation** nothing passes between wireless clients (neither unicast
nor broadcast — then VLC HTTP is unreachable too); wired PC ↔ wireless phone usually works
("communications between wireless and wired clients are allowed" — [batman-adv](https://www.open-mesh.org/doc/batman-adv/Ap-isolation.html);
Meraki in bridge mode blocks everything except the gateway — [Meraki](https://documentation.meraki.com/Wireless/Operate_and_Maintain/How_Tos/Firewall_and_Traffic_Shaping/Wireless_Client_Isolation);
on TP-Link, isolation "does not stop the flow of the multicast and broadcast traffic" — [forum](https://community.tp-link.com/en/smart-home/forum/topic/661906)).
On Android, receiving broadcast on some devices also requires a `MulticastLock` (not verified).
Conclusion: the beacon is a cheap addition to mDNS, not a replacement for manual entry.

### 2c. /24 scan on port 8080 — what existing remotes do

- VlcFreemote claims it "will automatically detect" VLC, the method is not disclosed
  ([F-Droid](https://f-droid.org/en/packages/com.nicolasbrailo.vlcfreemote/), [GitHub](https://github.com/nicolasbrailo/VlcFreemote));
  VLC Direct requires entering the IP manually; "Remote for VLC" ([samicemalone](https://github.com/samicemalone/android-vlc-remote)) —
  manual entry. The sources do not allow claiming that they scan the subnet (**not verified**); in
  practice "auto-detection" without a companion can only be a scan.
- Scan implementation: TCP connect to `x.y.z.1–254:8080` with a 200–300 ms timeout, 32–64 parallel
  threads → 2–5 s; confirmation — `GET /requests/status.json` without a password must return
  `401` with `WWW-Authenticate: Basic realm="VLC stream"` (the realm string is confirmed in the
  [live probe](live-probe.md)). A non-standard port — a field in the settings.
- **Can plain VLC be discovered any other way — no.** `share/lua/intf/http.lua` in the 3.0.x and master branches
  contains neither `vlc.sa`, nor `_http._tcp`, nor bonjour/announce (verified in the sources:
  [3.0.x](https://github.com/videolan/vlc/blob/3.0.x/share/lua/intf/http.lua),
  [master](https://github.com/videolan/vlc/blob/master/share/lua/intf/http.lua)). The 2020 patch
  "lua: http: Announce the web interface over mdns" ([vlc-devel](https://mailman.videolan.org/pipermail/vlc-devel/2020-September/137015.html))
  received objections ("should not be advertised until it is secured" —
  [reply](https://mailman.videolan.org/pipermail/vlc-devel/2020-September/137017.html);
  [question about localhost](https://mailman.videolan.org/pipermail/vlc-devel/2020-September/137021.html))
  and did not make it into the code. VLC has no UPnP/SSDP server either (the service discovery modules are client-side).

### 2d. Windows Firewall

- By default incoming connections are blocked in the Private/Public profiles. On the first `listen` Windows
  shows a "Windows Security Alert"; pressing **Cancel** silently creates blocking TCP+UDP rules
  for the exe, and the dialog never appears again ([Rebex](https://blog.rebex.net/beware-pressing-cancel-on-windows-firewall-security-alert)).
  A user with administrator/network operator rights can unblock from the dialog
  ([MS docs](https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-server-2008-R2-and-2008/cc766312(v=ws.10)));
  the behaviour for a standard user (a prompt for UAC credentials) — **not verified**. On this PC
  there are currently no rules for `vlc.exe` (verified with `Get-NetFirewallRule`), i.e. enabling VLC's HTTP interface
  here will trigger such a dialog.
- Adding a rule requires administrator rights in both APIs:
  `netsh advfirewall firewall add rule name="VLC Remote Companion" dir=in action=allow program="<exe>" protocol=TCP localport=47555 profile=private,public`
  ([netsh advfirewall](https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-server-2008-R2-and-2008/dd734783(v=ws.10)))
  and COM `INetFwRules::Add` via `HNetCfg.FwPolicy2` (an access error if not elevated;
  [INetFwRules::Add](https://learn.microsoft.com/en-us/windows/win32/api/netfw/nf-netfw-inetfwrules-add)).
  There is no way without an administrator.
- Strategy: per-user installation without UAC (Velopack), and on first launch the companion checks
  whether its rule exists (`INetFwPolicy2.Rules` or `netsh … show rule name=…`) and offers an
  "Allow in firewall" button, which runs `netsh` via `ProcessStartInfo { Verb = "runas" }`
  (one UAC window). With the same action add a rule for `vlc.exe` (the path from
  `HKLM\SOFTWARE\VideoLAN\VLC\InstallDir` — on this PC the key exists, value
  `C:\Program Files\VideoLAN\VLC`, `Version=3.0.23`; verified). A program-based rule survives
  updates because Velopack always keeps the exe in `%LocalAppData%\<AppId>\current\`
  ("avoids problems with firewall rules" — [Velopack: migrating from Squirrel](https://docs.velopack.io/migrating/squirrel)).
- Restricting the rule with `remoteip=localsubnet` is cheap protection against accidental port forwarding.

---

## 3. Companion ↔ phone protocol over the LAN

### 3a. WebSocket server in .NET

| Option | Size/dependencies | Needs `urlacl`/admin? | Assessment |
|---|---|---|---|
| **Kestrel, minimal API** (`FrameworkReference Microsoft.AspNetCore.App`, `CreateSlimBuilder`, `UseWebSockets`) | measured: WinForms tray self-contained single-file 107.7 MB → with Kestrel 133.3 MB (+25.6); with compression 48.1 → 59.3 MB (+11.2); framework-dependent 0.2 MB, but then the user must also have the ASP.NET Core Runtime installed | no: Kestrel uses its own sockets, not HTTP.sys | **recommended**: HTTP endpoints too (`/info`, `/pair`, `/art`), TLS if desired, WebSockets out of the box ([docs](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/websockets)) |
| `HttpListener` + `HttpListenerContext.AcceptWebSocketAsync` | in the BCL, 0 MB | **yes**: non-localhost prefixes require `netsh http add urlacl` from an administrator ([HttpListener](https://learn.microsoft.com/en-us/dotnet/fundamentals/runtime-libraries/system-net-httplistener), [URL ACL](https://www.awise.us/2006/04/10/httplistener-and-url-acls.html)) | no |
| `TcpListener` + manual HTTP upgrade + `WebSocket.CreateFromStream(stream, isServer:true, …)` | in the BCL, 0 MB, ~120 lines of own code ([CreateFromStream](https://learn.microsoft.com/en-us/dotnet/api/system.net.websockets.websocket.createfromstream)) | no | plan B if size is critical |
| WatsonWebsocket 4.1.8 (02/2026) | 0 dependencies ([NuGet](https://www.nuget.org/packages/WatsonWebsocket)) | `HttpListener` inside → the same `urlacl` problem (**not verified**) | no |
| Fleck 1.2.0 | raw sockets, last release 2016 ([comparison](https://www.esegece.com/products/websockets/comparison-net/)) | no | dead |

Minimal Kestrel server (built and published in the probe):

```csharp
var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:47555");
var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
app.MapGet("/info", () => Results.Json(new { name = Environment.MachineName, v = 1, id = pcId }));
app.Map("/ws", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    using WebSocket ws = await ctx.WebSockets.AcceptWebSocketAsync();
    await session.RunAsync(new WebSocketLineTransport(ws));   // the same NDJSON as over RFCOMM
});
_ = app.RunAsync();           // next to the tray's Application.Run()
```

On Android — OkHttp `WebSocket` (available in `okhttp3`, [docs](https://square.github.io/okhttp/)) or the Ktor
client; either is a single class of code.

### 3b. TLS or not

In a home WPA2 network, traffic between devices can be read by anyone who knows the Wi-Fi password (that is,
household members). The threat is a leak of the remote's token → a stranger can pause movies. For v1 it is reasonable to go
**without TLS**, but with a versioned protocol and an `fp` field (certificate fingerprint) in the QR,
reserved for future self-signed TLS with pinning (Kestrel: `UseHttps(cert)`; Android:
OkHttp `CertificatePinner`). This is cheaper than dragging in certificates right away, and breaks nothing later.
`ws://` on Android requires `usesCleartextTraffic`/a network security config for the LAN — keep that in mind.

### 3c. Pairing and authentication

1. In the tray the user clicks "Connect a phone". The companion generates a 6-digit code (TTL 2 min,
   5 attempts), shows a window with a QR code **on the TV monitor** (visible from the couch) and the same code.
   QR: `vlcremote://pair?host=192.168.1.20&port=47555&id=<pcId>&code=123456&v=1&fp=`.
2. The phone opens the WS and sends `{"t":"pair","code":"123456","device":"Pixel 8"}`.
3. The companion returns `{"t":"paired","token":"<32 random bytes base64url>"}`; on its side it stores the
   SHA-256 of the token and the device name; the phone — in EncryptedSharedPreferences/DataStore.
4. After that the first line of every session is `{"t":"auth","token":"…"}`; without it — only `/info`.
5. Over Bluetooth — the same token (the channel is already authenticated by pairing, but a single code is simpler).
6. Revocation: device list in the tray → "Forget".

### 3d. Framing shared by Bluetooth and WebSocket

**NDJSON**: one JSON value per line, `\n` is the separator, UTF-8, no line breaks inside a line
([ndjson spec](https://github.com/ndjson/ndjson-spec)). Over RFCOMM — literally the bytes of the stream;
over WebSocket — one text message = one line (a trailing `\n` is allowed). Pros:
`BufferedReader.readLine()` / `StreamReader.ReadLineAsync()`, readable in logs. Limits:
at most 64 KB per line; binary data (cover art) — only via HTTP `/art` over the LAN (over Bluetooth —
do not transfer, or a base64 thumbnail ≤ 20 KB). A length prefix only pays off for binary frames —
not needed here.

Envelope:

```json
{"t":"cmd","id":7,"m":"seek","a":{"rel":30}}
{"t":"res","id":7,"ok":true}
{"t":"evt","m":"state","d":{"state":"playing","time":912,"length":10705,"volume":256,"atrack":2}}
{"t":"ping"}  /  {"t":"pong"}
```

### 3e. Push versus polling

The companion holds one RC socket to VLC (push lines `status change: (…)`, see the [live probe](live-probe.md))
and polls `status.json` every 500–1000 ms (local latency ~21 ms), computes a diff and sends
only the changes to the phone. The phone does not poll at all — only subscription and commands. Heartbeat
`ping/pong` every 20 s at the application level (works the same over BT and WS; Kestrel has its own
`KeepAliveInterval`, 2 min by default — reduce it). Two missing `pong`s in a row →
reconnect.

---

## 4. Companion ↔ VLC

### 4a. VLC's HTTP interface

The same API as the phone uses at level 0: `requests/status.json?command=…`, `playlist.json`,
`browse.json?uri=…`, `/art`; commands `pl_play/pl_pause/pl_stop/pl_next/pl_previous`, `seek`,
`volume`, `rate`, `fullscreen`, `audio_track/subtitle_track/video_track`, `audiodelay/subdelay`,
`aspectratio`, `in_play/in_enqueue` ([README requests](https://github.com/videolan/vlc/blob/3.0.x/share/lua/http/requests/README.txt);
details and quirks — [live probe](live-probe.md)). Authentication — Basic, empty login,
password `--http-password`; without a password `http.lua` in 3.0.x returns `403` for everything and logs "Password unset,
insecure web interface disabled" (verified in the source). The `--http-host` option passed to Lua
is ignored with the message "HTTP host ignored" — host/port are set by the core options `--http-host`
`--http-port` ([wiki http_intf](https://wiki.videolan.org/Documentation:Modules/http_intf/)).
The companion can keep HTTP to VLC on `127.0.0.1` only and be the sole LAN entry point.

### 4b. RC interface over TCP

`--extraintf rc --rc-host 127.0.0.1:4212 --rc-quiet` (on Windows without `--rc-quiet` a
console opens, and commands are accepted only after Enter — [forum](https://forum.videolan.org/viewtopic.php?p=243197));
`--rc-host` — "Accept commands over TCP instead of stdin" ([wiki rc](https://wiki.videolan.org/Documentation:Modules/rc/)).
There is no authentication at all → **127.0.0.1 only**. One connection at a time, text format,
push events, `adev` (list/select Windows audio devices), `atrack/strack/vtrack` with a marker for the
current track, `quit` ([live probe](live-probe.md)). The companion is the only RC client and
multiplexes the phones' requests.

### 4c. A player inside the companion: LibVLCSharp

LibVLCSharp 3.8.x + VideoLAN.LibVLC.Windows 3.0.23(.1) ([LibVLCSharp](https://www.nuget.org/packages/LibVLCSharp),
[LibVLC.Windows](https://www.nuget.org/packages/VideoLAN.LibVLC.Windows); exact release dates — **not
verified**).

| Pros | Cons |
|---|---|
| Full programmatic control: output device (`SetOutputDevice`), tracks, events without parsing, window on the right monitor, fullscreen without `SetWindowPos` hacks | Duplicates the player: the user's own settings, cache, plugins, skins, hotkeys do not apply |
| No races over "who launched VLC" and over `vlcrc` | Native DLLs ~ +80 MB in the distribution (size of the LibVLC.Windows package — **not verified**) |
| One process, one protocol | A user who opened a file by double-clicking ends up in "the wrong" player |
|  | More surface for bugs (video output in a WinForms host, HDR/hardware decoding) |

Verdict (matches ADR-0001): not for v1; a possible "built-in mode" later.

### 4d. Launching `vlc.exe` with arguments

```text
"C:\Program Files\VideoLAN\VLC\vlc.exe" --extraintf http:rc --http-host 127.0.0.1 --http-port 8080
  --http-password <pw> --rc-host 127.0.0.1:4212 --rc-quiet
  --fullscreen --qt-fullscreen-screennumber=1 "D:\shared\film.mkv"
```

- `--qt-fullscreen-screennumber` — a **zero-based index** into `QGuiApplication::screens()`; out-of-range
  values are ignored (the window is not moved) — verified in
  [main_interface.cpp 3.0.x](https://github.com/videolan/vlc/blob/3.0.x/modules/gui/qt/main_interface.cpp)
  (`if (numscreen >= 0 && numscreen < QGuiApplication::screens().length())`, then
  `windowHandle()->setScreen(...)`, `move(...)`). The default is `-1` (in `vlcrc`:
  `#qt-fullscreen-screennumber=-1`, verified locally). Works only for embedded video
  ([vlc-devel 2019](https://mailman.videolan.org/pipermail/vlc-devel/2019-October/128255.html)).
  The order of Qt screens and `Screen.AllScreens` may differ — **not verified**; it is more reliable
  to move the window ourselves (section 5).
- `--one-instance --playlist-enqueue <file>` adds the file to an already running instance
  ([forum](https://forum.videolan.org/viewtopic.php?p=221412), [another](https://forum.videolan.org/viewtopic.php?p=264918));
  `--started-from-file` appears only in forum posts — **not verified**. Important: if
  VLC is already running in one-instance mode, **the interface arguments of the second launch are not applied**
  (only playlist items are passed on) — so either `extraintf` must be in `vlcrc`, or the
  companion owns VLC's lifecycle.
- Other useful ones: `--no-video-title-show`, `--qt-minimal-view`, `--no-qt-fs-controller`,
  `--start-time`, `--sub-file`, `--audio-track/--sub-track` — per `vlc --longhelp`.

### 4e. Auto-configuration via `vlcrc`

The file `%APPDATA%\vlc\vlcrc` ([wiki Vlcrc](https://wiki.videolan.org/Vlcrc)). The format per
[src/config/file.c 3.0.x](https://github.com/videolan/vlc/blob/3.0.x/src/config/file.c) and the local
file (3479 lines, written by VLC 3.0.21; verified):

```text
###
###  vlc 3.0.21
###
### lines beginning with a '#' character are comments
###

[visual] # Visualizer filter

# Effects list (string)
#effect-list=spectrum
...
#extraintf=
#http-host=
#http-port=8080
#http-password=
#rc-host=
#one-instance=0
```

- A value equal to the default is written commented out as `#key=value`; a changed one — `key=value`.
  The header has a UTF-8 BOM. Sections `[module] # description`.
- `config_SaveConfigFile` **does not read the old file**: it serializes the modules loaded in memory in full and
  replaces the file atomically (`vlcrc.<pid>` → rename; on Windows unlink first). Lines with unknown
  options are ignored on load and **lost** on save. If the file is read-only, VLC
  refuses to save (code comment: "Some users make vlcrc read-only to prevent changes").
- When VLC saves: Preferences → Save; auto-save `config_AutoSaveConfigFile` only when the
  `config_dirty` flag is set (when exactly it is set, besides the preferences dialog, — **not verified**).

Safe editing algorithm for the companion:
1. Only when `vlc.exe` is not running (otherwise VLC may overwrite the file on exit).
2. Read the file as UTF-8 with BOM, line by line. For each key (`extraintf`, `http-password`,
   `http-host`, `http-port`, `rc-host`) find the **first** line `^#?key=` and replace it with `key=value`
   (the key stays in its section). If the key is missing — append it to the end of the `[core]` section; for
   `rc-host` — `[rc]`, for `http-password` — `[lua]` (in 3.0.x `http-password` is declared by the lua module).
   The loader looks options up by name; the role of sections when reading — **not verified**, so keep to the
   sections.
3. Write to a temporary file alongside and `File.Move(tmp, vlcrc, overwrite: true)`.
4. If there is no `vlcrc` — create a minimal file with the header and the required lines (VLC should accept a
   file without the full list of options — **not verified**).
5. `extraintf=http:rc` — several interfaces separated by a colon (syntax from the live probe).
6. Store the HTTP password both in `vlcrc` and in the companion (the companion uses it itself).

Process detection/control: `Process.GetProcessesByName("vlc")`; the installation path —
`HKLM\SOFTWARE\VideoLAN\VLC\InstallDir` (verified) with a fallback to `%ProgramFiles%\VideoLAN\VLC\vlc.exe`;
"is HTTP alive" — TCP connect to the port + waiting for `401`. Graceful shutdown — RC `quit` (verified in the
live probe; `command=key&val=quit` over HTTP does not close it), then `WaitForExit(5s)` → `Kill()`.
On-demand launch — `Process.Start` with the arguments from 4d; right after start the first requests may
return an empty response — retry (live probe).

---

## 5. Screen and TV from the companion (Windows 11, .NET)

**Enumerating monitors.**
- WinForms `Screen.AllScreens` (`DeviceName` like `\\.\DISPLAY2`, `Bounds`, `Primary`)
  ([docs](https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.screen.allscreens)) —
  enough for a tray application.
- Win32 `EnumDisplayMonitors` + `GetMonitorInfo` ([docs](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enumdisplaymonitors)).
- WinRT `Windows.Devices.Display.DisplayMonitor` gives a human-readable name and the connection type
  (HDMI/DisplayPort/internal) — handy for showing "LG TV (HDMI)" in the remote
  ([docs](https://learn.microsoft.com/en-us/uwp/api/windows.devices.display.displaymonitor)).

**Find the VLC window and move it to the TV.**
- `Process.MainWindowHandle` — works as long as the process has a visible main window; with
  `--no-embedded-video` the video lives in a separate window.
- `EnumWindows` → filter by PID (`GetWindowThreadProcessId`) and window class. For VLC 3 (Qt5) the
  community uses the class `Qt5QWindowIcon` and the title "VLC media player"
  ([VBA example](https://xf2023.herber.de/1684to1688/1686695_Fenstergroee_mit_VBA_ermitteln.html));
  the title changes to `<file name> - VLC media player` (verified via `setVLCWindowsTitle` in the
  source), the class — **not verified** locally (VLC was not running), check with Spy++. VLC 4 (Qt6)
  will have a different class.
- Moving: `SetWindowPos(hWnd, IntPtr.Zero, tv.Bounds.X, tv.Bounds.Y, w, h, SWP_NOZORDER | SWP_SHOWWINDOW)`
  ([docs](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos)), then
  `fullscreen` via HTTP/RC: VLC goes fullscreen on the monitor where the window is (the gist of the
  2017 patch: [vlc-devel](https://mailman.videolan.org/pipermail/vlc-devel/2017-December/117078.html)).
  If already fullscreen — first `fullscreen off`, move, `fullscreen on`.

```csharp
static IntPtr FindVlcMainWindow(int pid)
{
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, _) =>
    {
        GetWindowThreadProcessId(h, out uint p);
        if (p != pid || !IsWindowVisible(h)) return true;
        var cls = new StringBuilder(64); GetClassName(h, cls, cls.Capacity);
        var title = new StringBuilder(256); GetWindowText(h, title, title.Capacity);
        if (cls.ToString().StartsWith("Qt5QWindow") && title.ToString().EndsWith("VLC media player"))
        { found = h; return false; }
        return true;
    }, IntPtr.Zero);
    return found;
}

static void MoveToScreen(IntPtr hWnd, Screen target)
{
    var b = target.Bounds;
    ShowWindow(hWnd, SW_RESTORE);
    SetWindowPos(hWnd, IntPtr.Zero, b.X, b.Y, b.Width * 2 / 3, b.Height * 2 / 3, SWP_NOZORDER | SWP_SHOWWINDOW);
}
```

**Projection mode.** `SetDisplayConfig(0, null, 0, null, SDC_TOPOLOGY_EXTEND|SDC_APPLY)` /
`SDC_TOPOLOGY_EXTERNAL` / `SDC_TOPOLOGY_CLONE` / `SDC_TOPOLOGY_INTERNAL` — "requests the last …
configuration from the persistence database"
([SetDisplayConfig](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setdisplayconfig));
read the current topology with `QueryDisplayConfig(QDC_DATABASE_CURRENT, …, out topologyId)`
([QueryDisplayConfig](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-querydisplayconfig)).
`DisplaySwitch.exe /internal|/clone|/extend|/external` — after 22H2 the slash switches reportedly
stopped working, numbers 1–4 are used instead ([Microsoft Q&A](https://learn.microsoft.com/en-us/answers/questions/1036148/displayswitchexe-behavior-on-windows-11-22h2.html),
[winaero](https://winaero.com/switch-between-multiple-displays-monitors-directly-with-a-shortcut-or-from-the-command-line-in-windows-7-and-windows-8/));
on this PC `DisplaySwitch.exe` 10.0.26100 exists, the arguments — **not verified**. Use
`SetDisplayConfig` (documented, no child processes).

```csharp
[DllImport("user32.dll")] static extern int SetDisplayConfig(uint n1, IntPtr p1, uint n2, IntPtr p2, uint flags);
const uint SDC_TOPOLOGY_INTERNAL = 1, SDC_TOPOLOGY_CLONE = 2, SDC_TOPOLOGY_EXTEND = 4, SDC_TOPOLOGY_EXTERNAL = 8, SDC_APPLY = 0x80;
static bool Extend()   => SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_TOPOLOGY_EXTEND  | SDC_APPLY) == 0;
static bool TvOnly()   => SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_TOPOLOGY_EXTERNAL | SDC_APPLY) == 0;
```

**Turning the display off/on.** `SendMessage(HWND_BROADCAST, WM_SYSCOMMAND, SC_MONITORPOWER, 2)`
(2 — off, 1 — low power, −1 — on, the last one is undocumented)
([WM_SYSCOMMAND](https://learn.microsoft.com/en-us/windows/win32/menurc/wm-syscommand),
[CodeProject](https://www.codeproject.com/KB/system/display_states.aspx)). On Windows 11 any
mouse movement/key press turns the screen back on; on some laptops with Modern Standby the behaviour
differs ([report](https://en.delphipraxis.net/topic/13282-turning-monitor-on-laptop-puts-application-to-sleep-also)).
For "on" it is more reliable to simulate input (`SendInput` with zero mouse movement). The TV does not
turn off from this — it sees "no signal" and goes to standby on its own timer.

**HDMI-CEC.** CEC is not available from a laptop's HDMI port (PC graphics cards do not expose the CEC line). The solution is a
Pulse-Eight USB-CEC adapter between the laptop and the TV + libcec: `tv_on.cmd`/`tv_off.cmd`, input selection
`-s -p N` ([Pulse-Eight](https://support.pulse-eight.com/support/solutions/articles/30000027391),
[what libcec is](https://support.pulse-eight.com/support/solutions/articles/30000053002-what-is-the-pulse-eight-cec-adapter-and-libcec-)).
From .NET — run `cec-client` as a process (there is also a .NET wrapper, LibCecSharp — **not verified**).
An optional "if an adapter is found" feature.

**No sleeping during a movie.** `SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED)`
while `playing`, `SetThreadExecutionState(ES_CONTINUOUS)` — to release
([docs](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-setthreadexecutionstate)).
VLC itself also blocks screen blanking during video (general behaviour, **not verified** for
3.0.23 on Win11); the companion needs its own flag for the scenarios "paused, but do not blank the screen" and
"turn off the screen, but do not put the PC to sleep" (e.g. audio).

**Sleep/hibernation/shutdown.** `SetSuspendState(bHibernate, bForce /*no effect*/, bWakeupEventsDisabled)`
requires the `SE_SHUTDOWN_NAME` privilege via `AdjustTokenPrivileges` (an ordinary user has it,
but it is disabled) ([docs](https://learn.microsoft.com/en-us/windows/win32/api/powrprof/nf-powrprof-setsuspendstate));
`shutdown.exe /s /t 0`, `/h` (hibernation), `/r` ([docs](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/shutdown)),
`shutdown.exe` has no sleep; a known trap: `rundll32 powrprof.dll,SetSuspendState 0,1,0`
hibernates if hibernation is enabled → for sleep use P/Invoke `SetSuspendState(false, …)`.
Locking — `LockWorkStation()`.

**Wake-on-LAN.** A magic packet (UDP to 255.255.255.255:9, 6×FF + 16×MAC) from the phone is trivial;
the companion hands over the PC's MAC during pairing. But a laptop on Wi-Fi: WoWLAN is "supported in most wireless
cards" according to Intel ([Intel community](https://community.intel.com/t5/Wireless/Wake-on-Wireless-Lan-WOWLAN-compatibility/m-p/1451835)),
in practice it depends on the adapter/driver/BIOS and often does not work
([AnandTech](https://forums.anandtech.com/threads/difficulty-setting-up-wowlan.2630657/)); with Ethernet
it is reliable. In the remote — a "Wake up" button as best-effort with a hint.

---

## 6. Sound from the companion

**Switching the output device.**
1. **Inside VLC** (documented, no hacks): RC `adev` without an argument → list of Windows devices
   with IDs `{0.0.0.00000000}.{GUID}` and `*` on the current one; `adev <id>` switches; at startup —
   `--aout mmdevice --mmdevice-audio-device=<id>` ([live probe](live-probe.md)). This covers
   "laptop speakers ↔ HDMI TV ↔ Bluetooth headphones" for a movie without touching the system.
2. **System default device**: there is no public API; all utilities (SoundSwitch,
   AudioEndPointController, Sunshine, AudioSwitcher) use the undocumented COM
   `IPolicyConfig::SetDefaultEndpoint(deviceId, ERole)`
   ([AudioEndPointController](https://github.com/Belphemur/AudioEndPointController),
   [Sunshine PolicyConfig.h](https://docs.lizardbyte.dev/projects/sunshine/latest/PolicyConfig_8h.html),
   [MS Q&A](https://learn.microsoft.com/en-za/answers/questions/226483/set-active-audio-endpoint)).
   Works on Windows 10/11 per community reports; officially it may break with any update.
   The GUIDs `CLSID {870af99c-171d-4f9e-af0d-e63df40c2bc9}` / `IID {f8679f50-850a-41cf-9c72-430f290290c8}`
   are widely quoted in the code of these projects, but the page with them could not be retrieved in this research
   — **check against `PolicyConfig.h`** before use. Implement it ourselves as a 60-line
   `[ComImport]` interface (the vtable method order is critical) and wrap it in try/catch with a
   graceful fallback. AudioSwitcher.AudioApi (3.1.0-beta3, [NuGet](https://www.nuget.org/packages/AudioSwitcher.AudioApi))
   — in beta, dates of last activity **not verified**; not worth pulling in for a single call.
3. Device enumeration and **system volume**: NAudio ([GitHub](https://github.com/naudio/NAudio)),
   `MMDeviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).AudioEndpointVolume`
   (`MasterVolumeLevelScalar`, `Mute`) — this is `IAudioEndpointVolume`
   ([docs](https://learn.microsoft.com/en-us/windows/win32/api/endpointvolume/nn-endpointvolume-iaudioendpointvolume)).
   VLC volume (0..512, 256 = 100 %) is a separate entity ([live probe](live-probe.md)).

```csharp
using NAudio.CoreAudioApi;
using var en = new MMDeviceEnumerator();
foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
    Console.WriteLine($"{d.ID}  {d.FriendlyName}");
var def = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
def.AudioEndpointVolume.MasterVolumeLevelScalar = 0.35f;   // 35 %
```

**What to expose in the remote.** The main slider — VLC volume; a second one, "system volume" + mute
(for the case when VLC is at 100 % and it is still too quiet/loud); a list of VLC output devices with
human-readable names (`adev`); optionally "make it the Windows default device"
(IPolicyConfig) — for the "headphones for everything" scenario. Device IDs are the same in VLC (`mmdevice`) and
CoreAudio — names can be matched.

---

## 7. The companion's form on Windows

**Tray application.** The simplest is a WinForms `WinExe` (`net9.0-windows`, `UseWindowsForms`) with
`NotifyIcon` + `ContextMenuStrip`, without a main window ([NotifyIcon](https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.notifyicon));
the "Pairing QR" and "Settings" windows are ordinary forms on demand. In the probe the WinForms tray and
Kestrel were built into one process: `_ = app.RunAsync(); Application.Run();`. WPF — the same, but without
a standard tray control (needs Hardcodet.NotifyIcon.Wpf); WinUI 3 — Windows App SDK, heavier and
harder in unpackaged mode. Single instance — a `Mutex`.

**Autostart.** `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` — no administrator needed,
the user sees it and can disable it in Task Manager → Startup
([Run keys](https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys)).
The Startup folder — the same, but with a shortcut. Task Scheduler — needed only for "with highest privileges" (not
required here). Choice: the Run key, with a toggle in the tray menu.

**Publish size (measured, .NET 9.0.310, win-x64, Release):**

| Configuration | Tray (WinForms) | Tray + Kestrel |
|---|---|---|
| framework-dependent, single-file | 0.2 MB (needs .NET Desktop Runtime 9; for Kestrel — also ASP.NET Core Runtime) | 0.2 MB |
| self-contained, single-file | 107.7 MB | 133.3 MB |
| self-contained, single-file, `EnableCompressionInSingleFile` | 48.1 MB | 59.3 MB |

Trimming WinForms was not considered (historically incompatible — **not verified** for .NET 9).
For users without .NET — self-contained compressed (~60 MB download, decompressed into memory at
startup; startup ~0.5–1 s slower — **not verified**). For ourselves — framework-dependent.

**Installation without an administrator, and updates.**
- **Velopack**: the successor of Squirrel.Windows/Clowd.Squirrel ("Clowd.Squirrel has been renamed to
  Velopack" — [NuGet Clowd.Squirrel](https://www.nuget.org/packages/Clowd.Squirrel)); installs into
  `%LocalAppData%\<AppId>\current\` without UAC, `current` is replaced entirely on update (keep logs and
  settings separately), updates from any static URL or GitHub Releases
  ([Windows packaging](https://docs.velopack.io/packaging/operating-systems/windows),
  [C# getting started](https://docs.velopack.io/getting-started/csharp)); C# SDK + the `vpk` CLI
  (`dotnet tool install -g vpk`; the vpk and package versions must match). Delta updates and
  signature verification are claimed in the documentation, **not verified** in this research. Flow:
  `dotnet publish … --self-contained -r win-x64 -o publish` → `vpk pack --packId VlcRemoteCompanion --packVersion 1.0.0 --packDir publish` → `vpk upload github …`.
- The original Squirrel.Windows — no activity (exact repository status **not verified**); do not use.
- Minimum without a framework: a zip on GitHub Releases + checking `releases/latest` from the tray and a
  "download" link. Acceptable for a home project, but Velopack gives the same for an hour of work.
- An unsigned exe will trigger SmartScreen on first launch (common knowledge); fine for ourselves.

**Logging.** `Microsoft.Extensions.Logging` + Serilog `File` sink with rotation in
`%LocalAppData%\VlcRemoteCompanion\logs\` ([Serilog](https://serilog.net/)); no tokens or
passwords; an "Open log folder" button in the tray.

**Alternative languages for the companion.**
- **Rust**: `windows-rs` gives the same WinRT (RFCOMM, DNS-SD, Bluetooth) and COM (CoreAudio) APIs,
  `tray-icon` for the tray, Velopack has a Rust SDK; exe 3–6 MB without a runtime
  ([windows-rs](https://github.com/microsoft/windows-rs), [tray-icon](https://github.com/tauri-apps/tray-icon)).
  The price — a different language, 2–3× the time for the same Windows API surface.
- **Go**: `systray` exists ([getlantern/systray](https://github.com/getlantern/systray)), but WinRT/COM
  from Go is painful (cgo or manual vtables), an RFCOMM server via Winsock by hand.
- Why **C#/.NET**: the whole required surface (WinRT Bluetooth and DNS-SD, COM CoreAudio, Win32 windows/
  displays, Kestrel) is first-class and already verified; a 50 MB difference in distribution size is
  insignificant for a home HTPC; the developer knows the language.

---

## 8. Security in a home LAN

- **VLC HTTP**: Basic auth over plain HTTP with an empty login; the password is visible to anyone who can
  sniff the Wi-Fi (knows the WPA2 password). With the companion — `--http-host 127.0.0.1`: VLC is not
  visible on the network at all, the only LAN entry point is the companion with a token. Without the companion (level 0) —
  accept the risk; the password is not the same as the user's other passwords.
- **RC**: no authentication — **`127.0.0.1` only**, never `0.0.0.0`.
- **Companion**: a 256-bit token, stored as SHA-256; the pairing code lives 2 minutes and only
  while the "Connect a phone" window is open; an attempt limit; respond only to RFC 1918 and
  link-local addresses (protection against accidental port forwarding); no UPnP mapping; `/info` without secrets;
  a firewall rule per program+port, `remoteip=localsubnet`; once TLS arrives — pinning the
  fingerprint from the QR (TOFU).
- **Bluetooth**: pairing (SSP) provides link-level encryption; use
  authenticated sockets (`createRfcommSocketToServiceRecord` + `BluetoothEncryptionWithAuthentication`),
  not the insecure variant (MITM). The application token — still required (a single model, protection against someone else's
  paired device).
- **Do not leave the LAN**: no cloud relays in v1; access from the internet — only via the
  user's own VPN (WireGuard/Tailscale), which is transparent to the protocol.
- **Updates**: HTTPS only (GitHub); sign releases where possible.

---

## Recommendations

### Transport architecture

```
   Android (Kotlin/Compose)                                Windows 11 HTPC
 ┌──────────────────────────┐                     ┌─────────────────────────────────────┐
 │ UI ── PlayerBackend ─────┼─ level 0/1 ─────────┼─▶ VLC http intf :8080 (Lua, Basic)  │
 │        │                 │   (HTTP, no         │        ▲                 ▲          │
 │        │ CompanionBackend│    companion)       │        │ 127.0.0.1       │ RC 4212  │
 │        │   │             │                     │   ┌────┴─────────────────┴────────┐ │
 │        │   ├─ WsTransport┼─ LAN ws://pc:47555 ─┼──▶│ Companion (.NET 9, tray)       │ │
 │        │   │  (OkHttp)   │   NDJSON + token    │   │  Kestrel WS/HTTP :47555        │ │
 │        │   └─ BtTransport┼─ RFCOMM (UUID) ─────┼──▶│  RfcommServiceProvider         │ │
 │        │      (secure)   │   NDJSON + token    │   │  DnsServiceRegister (_vlcremote)│ │
 │ Discovery: NsdManager +  │◀─ mDNS / UDP beacon ┼───│  UDP beacon responder :47556   │ │
 │  UDP beacon + /24 scan   │                     │   │  VLC process mgr, vlcrc        │ │
 │  + manual host / QR      │                     │   │  Win32: windows, SetDisplayCfg,│ │
 └──────────────────────────┘                     │   │   power; CoreAudio; IPolicyCfg │ │
          ▲                                       │   └────────────────────────────────┘ │
          └── Bluetooth PAN (no code) → ordinary IP path, like LAN ────────────────────────┘
```

A single `Session` class on the PC serves both transports through a "line in / line out" abstraction.
On the phone `CompanionBackend` picks the transport: LAN found → WS; no network/no PC in mDNS within 3 s
and there is a paired PC → RFCOMM; both unavailable → suggest PAN/hotspot.

### Chosen libraries and versions

| Component | Choice | Version (as of 2026-10) |
|---|---|---|
| Tray, windows | WinForms (`net9.0-windows10.0.19041.0`, `UseWindowsForms`) | .NET 9 (SDK 9.0.310 installed) |
| HTTP/WebSocket | Kestrel (`FrameworkReference Microsoft.AspNetCore.App`, `CreateSlimBuilder`) | part of .NET 9 |
| Bluetooth RFCOMM | WinRT `Windows.Devices.Bluetooth.Rfcomm` (no NuGet) | Windows SDK 10.0.19041+ |
| mDNS/DNS-SD | Win32 `dnsapi.dll` P/Invoke (`DnsServiceRegister`) | Windows 10+ |
| Audio | NAudio (CoreAudio) + our own `IPolicyConfig` interop | NAudio 2.x |
| Win32 | Manual P/Invoke or the `Microsoft.Windows.CsWin32` source generator | CsWin32 0.3.x |
| Logs | Serilog + Serilog.Sinks.File | 4.x / 6.x |
| Install/updates | Velopack + `vpk` | 0.0.5xx (NuGet, keep versions in sync) |
| JSON | `System.Text.Json` (source-generated) | part of .NET 9 |
| Android BT | `android.bluetooth` + `CompanionDeviceManager` | API 26+ / 33+ |
| Android WS | OkHttp `WebSocket` | 4.12 / 5.x |
| Android discovery | `NsdManager` (API 34+ callbacks) + UDP | — |

### Complexity estimate

| Piece | Complexity | Rough estimate |
|---|---|---|
| Tray + Kestrel + NDJSON session + token/QR | S–M | 2–3 days |
| RFCOMM server on Windows + the same `Session` | S (the API is verified to work) | 1 day + tests with 2–3 phones |
| Android: WS transport + discovery (NSD + beacon + manual entry) | M | 2–3 days (NSD is fickle) |
| Android: BT transport + CDM + reconnect | M | 2–3 days |
| VLC manager: process, arguments, `vlcrc`, RC multiplexer, diff push | M | 3 days |
| Windows/monitors/projection/sleep/keep-awake | S–M | 1–2 days (+ tests on a real TV) |
| Audio: `adev` + NAudio volume | S | 0.5 day; `IPolicyConfig` another +0.5 |
| Firewall (detection + UAC helper) | S | 0.5 day |
| Velopack, releases, autostart, logs | S | 1 day |
| HDMI-CEC (optional, with an adapter) | S | 0.5 day |
| TLS with pinning (postponed) | M | 1–2 days |

### What to check first in the prototype (open questions)

1. The full Android → Windows RFCOMM cycle: pairing via the dialog on `connect()`, the "Add a
   device" notification on the PC, `BluetoothEncryptionWithAuthentication`.
2. Survival of the RFCOMM listener after the laptop sleeps/wakes.
3. `NsdManager` on the target phone finds `_vlcremote._tcp` advertised by the Windows responder;
   behaviour over Bluetooth PAN and the firewall profile of the PAN network.
4. The VLC window class (Spy++) and the order of Qt screens vs `Screen.AllScreens`.
5. `SetDisplayConfig(SDC_TOPOLOGY_EXTERNAL)` on this laptop; `SC_MONITORPOWER` under Modern Standby.
6. `IPolicyConfig` on the current Windows 11 build (26200).
7. Editing `vlcrc`: VLC accepts a minimal file; which actions set `config_dirty`.
