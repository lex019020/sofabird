# Транспорт и Windows‑компаньон для пульта VLC: исследование

Дата: 2026-10-12. Контекст: Android‑пульт (Kotlin/Compose) для VLC 3.0.x на ноутбуке с
Windows 11, подключённом к телевизору. Компаньон на ПК — опциональный (см.
[ADR‑0001](../adr/0001-control-paths.md), уровень 2). Разработчик знает C#/.NET и Kotlin;
на ПК установлены .NET SDK 9.0.310 и 10.0.102.

Легенда:
- **проверено локально** — выполнено на этом ПК (Windows 11 Pro 10.0.26200, адаптер
  «MediaTek Bluetooth Adapter», VLC 3.0.23, пользователь без прав администратора);
- **не проверено** — утверждение из документации/сообщества или вывод, который следует
  подтвердить экспериментом.

Исходники проверочных программ (`BtProbe`, `DnsSdProbe`, замеры размеров) лежали в
scratch‑папке сессии; ключевые фрагменты повторены в тексте.

---

## 0. Краткая сводка

| Вопрос | Ответ |
|---|---|
| Bluetooth без LAN | **Classic RFCOMM** через WinRT `RfcommServiceProvider` + `StreamSocketListener`. Работает из обычного unpackaged .NET 9 приложения без прав администратора — **проверено локально**. Android: `createRfcommSocketToServiceRecord(uuid)` + `BLUETOOTH_CONNECT`, сопряжение показывает сама система при `connect()`. |
| BLE вместо RFCOMM | Возможно (`GattServiceProvider`, адаптер этого ПК поддерживает peripheral‑роль — проверено), но хуже: короткие сообщения, своя фрагментация, нет потока. Не рекомендуется как основной. |
| Bluetooth без кода | Bluetooth PAN (телефон раздаёт сеть по Bluetooth, Windows 11 «Join» в настройках) даёт обычный IP‑канал; ПК получает адрес из 192.168.44.x (код Android). Нюанс: Windows отнесёт эту сеть к профилю «Общедоступная» — нужны правила брандмауэра на Public (не проверено). |
| Обнаружение в LAN | mDNS/DNS‑SD через **встроенный responder Windows**: Win32 `DnsServiceRegister` (P/Invoke, без библиотек) или WinRT `DnssdServiceInstance` — оба **проверены локально**. Сторонние .NET‑библиотеки: Makaretu — мёртвая с 2019, Zeroconf и Tmds.MDns — только поиск (не умеют объявлять). На Android — `NsdManager` с защитой от его багов + запасной UDP‑маяк + ручной ввод. |
| Чистый VLC без компаньона | Не объявляет себя ни по mDNS, ни как‑либо ещё (проверено по исходникам `http.lua` веток 3.0.x и master). Остаётся: скан /24 по порту 8080 (401 с `realm="VLC stream"`), либо имя `<ПК>.local` (Windows отвечает на mDNS‑имя, Android умеет резолвить `.local`). |
| Сервер протокола | **Kestrel minimal API + WebSocket** (без HTTP.sys и `urlacl`). Цена: +25,6 МБ self‑contained (+11,2 МБ в сжатом single‑file) — измерено. Альтернатива «ноль зависимостей»: `TcpListener` + `WebSocket.CreateFromStream`. `HttpListener` не подходит (нужен `netsh http add urlacl` с правами администратора для не‑localhost). |
| Кадрирование | NDJSON (одна JSON‑строка на сообщение) одинаково поверх RFCOMM и WebSocket. |
| VLC ↔ компаньон | HTTP (как телефон) + один RC‑сокет на `127.0.0.1` для событий/`adev`/`atrack`. Запуск `vlc.exe` с аргументами; правка `vlcrc` только при остановленном VLC и с сохранением формата `#key=value`. |
| Экран/ТВ | `Screen.AllScreens` + `SetWindowPos` окна VLC на монитор‑ТВ, затем `fullscreen`; `SetDisplayConfig(SDC_TOPOLOGY_*)`; `SC_MONITORPOWER`; `SetThreadExecutionState`; `SetSuspendState`/`shutdown.exe`. HDMI‑CEC только с USB‑адаптером Pulse‑Eight. WoL по Wi‑Fi — ненадёжен. |
| Звук | Сначала — переключение устройства **внутри VLC** (RC `adev`), системное устройство по умолчанию — через недокументированный `IPolicyConfig` (как SoundSwitch/Sunshine). Громкость Windows — `IAudioEndpointVolume` (NAudio). |
| Форма компаньона | WinForms‑трей (`NotifyIcon`) + Kestrel в одном `WinExe`; автозапуск через `HKCU\...\Run`; установка/обновление **Velopack** в `%LocalAppData%` без UAC; single‑file self‑contained ≈ 59 МБ (измерено). |

---

## 1. Bluetooth без локальной сети

### 1a. Classic Bluetooth RFCOMM (SPP) — сервер на Windows из .NET

**API.** Пространство `Windows.Devices.Bluetooth.Rfcomm` (WinRT): `RfcommServiceProvider.CreateAsync(RfcommServiceId)`
создаёт локальный сервис, `StreamSocketListener.BindServiceNameAsync(serviceId, protectionLevel)`
привязывает слушатель к сервису, `provider.SdpRawAttributes` — доп. атрибуты SDP‑записи,
`provider.StartAdvertising(listener, radioDiscoverable)` публикует запись
([CreateAsync](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.rfcomm.rfcommserviceprovider.createasync),
[StartAdvertising](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.rfcomm.rfcommserviceprovider.startadvertising),
[BindServiceNameAsync](https://learn.microsoft.com/en-us/uwp/api/windows.networking.sockets.streamsocketlistener.bindservicenameasync),
гайд [Send or receive files with RFCOMM](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/send-or-receive-files-with-rfcomm),
образец [BluetoothRfcommChat](https://github.com/microsoft/Windows-universal-samples/tree/main/Samples/BluetoothRfcommChat)).
Из .NET всё доступно без NuGet при `TargetFramework` `net9.0-windows10.0.19041.0` или выше
(встроенные C#/WinRT‑проекции).

**Работает ли из обычного desktop‑приложения без манифеста и без администратора — проверено
локально: да.** Документация требует capability `bluetooth` (это про packaged/UWP), но консольный
`net9.0-windows10.0.19041.0` exe, запущенный как `DESKTOP-9SO2EON\Alexey` без повышения
(`packaged=False`), успешно выполнил `CreateAsync` → `BindServiceNameAsync` → `SdpRawAttributes.Add`
→ `StartAdvertising(listener, false)` → `StopAdvertising`. Вывод пробы:

```
User: DESKTOP-9SO2EON\Alexey, elevated(admin)=False
OS: Microsoft Windows NT 10.0.26200.0, packaged=False
BluetoothAdapter: addr=0468749A2BFC classic=True le=True central=True peripheral=True advOffload=True extAdv=True
RFCOMM: advertising OK, serviceId={C0F1B7A2-5C0E-4B3E-9A0E-1F2D3C4B5A69}
RFCOMM: stopped
```

Минимальный сервер (C#, net9.0-windows10.0.19041.0):

```csharp
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

static readonly Guid ServiceUuid = Guid.Parse("c0f1b7a2-5c0e-4b3e-9a0e-1f2d3c4b5a69"); // свой 128-бит UUID

var provider = await RfcommServiceProvider.CreateAsync(RfcommServiceId.FromUuid(ServiceUuid));
var listener = new StreamSocketListener();
listener.ConnectionReceived += async (_, e) =>
{
    // один клиент за раз; поток байт — поверх него NDJSON (см. раздел 3d)
    using var socket = e.Socket;
    using var input  = socket.InputStream.AsStreamForRead();
    using var output = socket.OutputStream.AsStreamForWrite();
    await session.RunAsync(input, output);           // общий код с WebSocket-транспортом
};
await listener.BindServiceNameAsync(provider.ServiceId.AsString(),
    SocketProtectionLevel.BluetoothEncryptionWithAuthentication);      // требовать сопряжение+шифрование

// SDP-атрибут 0x0100 = ServiceName (строка, 8-битная длина)
var w = new DataWriter { UnicodeEncoding = UnicodeEncoding.Utf8 };
w.WriteByte(0x25); w.WriteByte((byte)"VLC Remote".Length); w.WriteString("VLC Remote");
provider.SdpRawAttributes.Add(0x0100, w.DetachBuffer());

provider.StartAdvertising(listener, radioDiscoverable: false);
```

Замечания к коду:
- В пробе использовался `BluetoothEncryptionAllowNullAuthentication` (так в образце Microsoft);
  `BluetoothEncryptionWithAuthentication` для принуждения к сопряжению — **не проверено** в связке с
  Android, проверить при реализации.
- Канал RFCOMM Windows выдаёт динамически; клиент обязан искать его по UUID через SDP (Android
  это делает в `connect()`), поэтому фиксированный номер канала не нужен.
- После `StopAdvertising`/выхода из сна слушатель лучше пересоздавать (не проверено, но
  дешевле, чем ловить «зависший» listener).

**SDP‑запись и UUID.** Свой 128‑битный UUID — обязательно (не SPP `00001101-…`), чтобы телефон
не путал компаньон с COM‑портами. ServiceName (0x0100) полезен для отладочных сканеров.

**Нужно ли сопряжение и кто его инициирует.**
- Android‑клиент с «secure» сокетом требует bonded‑устройство; если пары нет, «the Android
  framework automatically shows a pairing request notification or dialog to the user during
  the connection procedure» и `connect()` блокируется до завершения сопряжения
  ([Connect Bluetooth devices](https://developer.android.com/develop/connectivity/bluetooth/connect-bluetooth-devices)).
- На стороне Windows входящий запрос на сопряжение по умолчанию показывается уведомлением
  «Add a device» (по описанию [ElevenForum](https://www.elevenforum.com/t/enable-or-disable-notification-for-new-bluetooth-device-connection-in-windows-11.44957/);
  **не проверено** на этом ПК). Это и есть самый простой сценарий: пользователь один раз
  сопрягает телефон с ПК из настроек Windows или Android.
- Из компаньона сопряжение можно инициировать программно:
  `DeviceInformation.Pairing.Custom.PairAsync(DevicePairingKinds.ConfirmPinMatch|…)` с обработчиком
  `PairingRequested` ([DeviceInformationPairing](https://learn.microsoft.com/en-us/uwp/api/windows.devices.enumeration.deviceinformationpairing),
  [DeviceInformationCustomPairing](https://learn.microsoft.com/en-us/uwp/api/windows.devices.enumeration.deviceinformationcustompairing)).
  Тогда телефон должен быть «видимым» (на Android — открыт экран настроек Bluetooth). Работа из
  unpackaged‑приложения — **не проверено** (в пробе не вызывалось), но API того же семейства, что и
  проверенный RFCOMM.

**Должен ли ПК быть «видимым».** Только для сопряжения, инициируемого с телефона. Для подключения
уже сопряжённого телефона достаточно, чтобы радио было включено (обычное поведение Bluetooth —
paging по известному адресу; **не проверено** именно на этой связке, но так работает любая
гарнитура). Переключатели:
- программно — `StartAdvertising(listener, radioDiscoverable: true)` («Indicates whether the radio
  is discoverable») — на время сопряжения, затем перезапустить с `false`;
- вручную — Settings → Bluetooth & devices → Devices → More Bluetooth settings → «Allow Bluetooth
  devices to find this PC» ([описание](https://geekrewind.com/how-to-turn-allow-bluetooth-devices-to-find-this-pc-on-or-off-in-windows-11/)).

**Права.** Администратор не нужен — проверено локально.

**Пропускная способность / задержка.** Classic BR/EDR: 1 Мбит/с (BR), 2/3 Мбит/с (EDR); на уровне
приложения ~720 кбит/с (BR) и ~2,1 Мбит/с (EDR) ([ib‑lenhardt](https://ib-lenhardt.com/kb/glossary/bluetooth-classic-br-edr));
практические SPP‑оценки 0,7–2,1 Мбит/с ([студенческий отчёт GT](https://ece4012y201902.ece.gatech.edu/spring/sd19p49/trpethanvargas.docx)).
Для команд пульта (сотни байт) этого с запасом; обложки/миниатюры по Bluetooth не гонять
(или гонять сжатые ≤ 50 КБ). Задержку округления не нашёл в источниках; ожидаемо десятки мс —
**не проверено**, замерить в прототипе.

**Известные грабли.**
- Энергосбережение Bluetooth‑адаптера: Microsoft советует снять «Allow the computer to turn off
  this device to save power» ([Bluetooth keeps disconnecting](https://support.microsoft.com/windows/bluetooth-keeps-disconnecting-in-windows-81a9cb1e-d15c-4e72-b025-cc0d992dca9f));
  Dell пишет, что с Windows 10 2004 этого флага на самом адаптере нет, он остался на HID‑устройствах
  ([Dell KB](https://dell.com/support/kbdoc/en-us/000122996)). Для RFCOMM‑сессии компаньона это
  означает: рассчитывать на обрывы после простоя/сна и переподключаться с телефона.
- Modern Standby: при засыпании ноутбука RFCOMM‑сокет умрёт; компаньон должен пересоздавать
  listener после `SystemEvents.PowerModeChanged`/`Resume` (не проверено).
- Android кэширует SDP‑записи; при смене UUID/канала помогает `device.fetchUuidsWithSdp()`
  (общее знание, не проверено).
- Одновременно один клиент: второй `ConnectionReceived` — отклонять или вытеснять первого.

**32feet.NET (InTheHand.Net.Bluetooth).** Жив: 4.2.x, релизы в 2025–2026 (4.2.2 — 06/2025, 4.2.3 —
02/2026, 4.2.4 — 04/2026 по профилю NuGet; версии ≥ 4.2.3 — вывод по профилю издателя, **не
проверено** по странице пакета), MIT, цели .NET 9/.NET Standard 2.0/.NET Framework
([NuGet](https://www.nuget.org/packages/InTheHand.Net.Bluetooth), [GitHub](https://github.com/inthehand/32feet)).
На Windows заявлены RFCOMM client и server, discovery, пикеры; GATT‑сервер — «planned»
([wiki](https://github.com/inthehand/32feet/wiki)). Какой API использует под капотом на Windows
(Win32 Winsock или WinRT), в документации не сказано — **не проверено**. Поскольку чистый WinRT
проверен и работает, 32feet нужен только если понадобится API‑стиль `BluetoothListener`/
`BluetoothClient` или сопряжение через `BluetoothSecurity.PairRequest`. Рекомендация: без 32feet.

### 1b. Android‑клиент

**Сокет.** `BluetoothDevice.createRfcommSocketToServiceRecord(uuid)` — «secure outgoing connection
… using SDP lookup of uuid», удалённая сторона аутентифицируется, канал шифруется; защита от MITM
есть только у аутентифицированного link key. `createInsecureRfcommSocketToServiceRecord(uuid)` —
«will not have an authenticated link key», уязвим к MITM; на устройствах ≥ 2.1 канал всё равно
шифруется (encryption mandatory), на pre‑2.1 — нет
([BluetoothDevice](https://developer.android.com/reference/android/bluetooth/BluetoothDevice),
[исходник javadoc](https://android.googlesource.com/platform/packages/modules/Bluetooth/+/refs/heads/main/framework/java/android/bluetooth/BluetoothDevice.java)).
Использовать **secure**‑вариант. `connect()` блокирующий (таймаут ~12 с), выполнять не в UI‑потоке;
перед ним всегда `adapter.cancelDiscovery()`
([Connect Bluetooth devices](https://developer.android.com/develop/connectivity/bluetooth/connect-bluetooth-devices)).

**Разрешения (API 31+).** Из [Bluetooth permissions](https://developer.android.com/develop/connectivity/bluetooth/bt-permissions):

```xml
<!-- Нужно только если приложение само ищет устройства (startDiscovery). -->
<uses-permission android:name="android.permission.BLUETOOTH_SCAN"
                 android:usesPermissionFlags="neverForLocation" />
<!-- Общение с уже сопряжёнными устройствами. -->
<uses-permission android:name="android.permission.BLUETOOTH_CONNECT" />
<!-- Старые версии -->
<uses-permission android:name="android.permission.BLUETOOTH" android:maxSdkVersion="30" />
<uses-permission android:name="android.permission.BLUETOOTH_ADMIN" android:maxSdkVersion="30" />
```

Все три новых — runtime‑разрешения. Если устройство выбирается из `getBondedDevices()` или через
CDM, достаточно `BLUETOOTH_CONNECT`; `BLUETOOTH_SCAN` нужен лишь для собственного `startDiscovery()`
(с `neverForLocation` — без `ACCESS_FINE_LOCATION`; CDM «doesn't require location permissions»).

**Companion Device Manager** — более приятный UX выбора/сопряжения: системный диалог, скан
выполняет система, без location‑разрешения; после ассоциации можно просить
`REQUEST_COMPANION_RUN_IN_BACKGROUND`, `REQUEST_COMPANION_START_FOREGROUND_SERVICES_FROM_BACKGROUND`
и наблюдать присутствие устройства (`startObservingDevicePresence`, с API 36 — вариант с
`ObservingDevicePresenceRequest` и привязкой `CompanionDeviceService`, пока устройство «within BLE
range or connected using Bluetooth»). CDM «doesn't create connections on its own» — сокет
открывать самим ([Companion device pairing](https://developer.android.com/develop/connectivity/bluetooth/companion-device-pairing)).

```kotlin
// API 33+
val filter = BluetoothDeviceFilter.Builder()
    .addServiceUuid(ParcelUuid(SERVICE_UUID), null)   // только ПК с нашим RFCOMM-сервисом
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
        // info.deviceMacAddress -> сохранить, далее getRemoteDevice(mac) + createBond() при необходимости
    }
    override fun onFailure(error: CharSequence?) { /* показать ошибку */ }
})
```

Оговорка: фильтр по service UUID в CDM опирается на SDP/EIR‑данные ПК; что Windows‑RFCOMM
сервис виден этим фильтром при скане — **не проверено**; запасной вариант — `setNamePattern`
по имени ПК либо выбор из `getBondedDevices()`.

**Подключение и чтение (Kotlin, NDJSON):**

```kotlin
class BtLink(private val device: BluetoothDevice, private val adapter: BluetoothAdapter) {
    private var socket: BluetoothSocket? = null

    @RequiresPermission(Manifest.permission.BLUETOOTH_CONNECT)
    suspend fun connect() = withContext(Dispatchers.IO) {
        adapter.cancelDiscovery()
        val s = device.createRfcommSocketToServiceRecord(SERVICE_UUID)
        s.connect()                       // блокирует; при отсутствии пары система покажет диалог
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

**Переподключение.** Подключаться при выходе приложения на передний план; при `IOException` —
экспоненциальная пауза 1→2→4→8 с (до ~30 с) и повтор, параллельно показывая «ищу ПК…»; слушать
`BluetoothDevice.ACTION_ACL_CONNECTED/DISCONNECTED` и `BluetoothAdapter.ACTION_STATE_CHANGED`,
чтобы не долбить выключенное радио. Если за N попыток не вышло — предложить LAN/ручной IP.

**Фон.** Для пульта удерживать сокет в фоне не обязательно: закрывать через ~1 мин после ухода в
фон. Если нужна «идёт воспроизведение»‑нотификация с кнопками — foreground service с типом
`connectedDevice` (`FOREGROUND_SERVICE_CONNECTED_DEVICE`, обязательный тип с Android 14;
Google советует для длительных связей предпочитать CDM presence API)
([FGS types](https://developer.android.com/develop/background-work/services/fgs/service-types)).

### 1c. BLE‑альтернатива: Windows как GATT‑периферия

`GattServiceProvider` (с 10.0.15063) позволяет Windows объявлять сервис и характеристики; нужен
адаптер с peripheral‑ролью (`BluetoothAdapter.IsPeripheralRoleSupported`; на этом ПК `True` —
проверено локально; есть отчёты адаптеров без поддержки
([SO](https://windows-hexerror.linestarve.com/q/so48956229-gattserviceproviderstartadvertising-fails-bluetooth-device-issue))).
Ограничения: «there is no explicit functionality associated with this API to disconnect» — разрыв
только со стороны центрального; Windows «usually operates in the client role»
([GattServiceProvider](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.genericattributeprofile.gattserviceprovider),
[GATT server guide](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/gatt-server)).
Практически: ATT‑MTU по умолчанию 23 байта (полезных 20), с `requestMtu` до 512 — нужна своя
фрагментация JSON; нет потоковой семантики, уведомления ограничены; зато не обязательно
сопряжение и ниже энергопотребление (общие свойства BLE, **не проверено** в этой связке).

Сравнение: RFCOMM — поток, ~1–2 Мбит/с, одно сопряжение, `readLine()`; BLE — сообщения ≤ 512 Б,
свой протокол фрагментации, два стека (GATT‑сервер на Windows + GATT‑клиент на Android), больше
кода и больше «особенностей» на разных телефонах. **Рекомендация: RFCOMM**; BLE оставить как
план Б, если выяснится, что у части телефонов SDP/RFCOMM к Windows работает нестабильно.

### 1d. Fallback без кода: Bluetooth PAN (tethering)

- Windows 11 умеет присоединяться к Bluetooth PAN из настроек: Settings → Bluetooth & devices →
  Devices → раскрыть телефон → «Personal Area Network (PAN)» → Join → «Access Point» → Connect;
  отключение — «Disconnect PAN» ([Microsoft Support](https://support.microsoft.com/en-us/windows/connect-to-a-bluetooth-network-in-windows-0d371763-6297-4eef-9d39-f8c469d181c2)).
  Требования: устройства сопряжены, на телефоне включена раздача по Bluetooth. По сообщениям
  сообщества, кнопка появилась в сборке 22631.1906 ([ElevenForum](https://elevenforum.com/t/connect-to-bluetooth-personal-area-network-btpan-in-windows-11.20046/)); на этом ПК сборка 26200.
- На Android: Settings → Network & internet → Hotspot & tethering → Bluetooth tethering (путь
  зависит от оболочки; **не проверено**). Возможна ли раздача, когда у телефона нет интернета —
  зависит от прошивки (**не проверено**; на многих устройствах переключатель доступен).
- Адресация (из кода Android `TetheringConfiguration.java`): «BT is limited to max default of 5
  connections. 192.168.44.1 to 192.168.48.1»; legacy DHCP‑диапазон для BT `192.168.44.2–254`
  (USB — 192.168.42.x, Wi‑Fi — 192.168.43.x) ([исходник](https://android.googlesource.com/platform/packages/modules/Connectivity/+/refs/heads/main/Tethering/src/com/android/networkstack/tethering/TetheringConfiguration.java)).
  Массив назван `LEGACY_…` — в новых версиях префикс может выбираться иначе, поэтому **не
  зашивать 192.168.44.x**: телефон — шлюз, ПК получает адрес по DHCP. Как телефону найти ПК: ПК
  объявляет себя (mDNS регистрируется на всех интерфейсах, UDP‑маяк), либо телефон сканирует
  свой PAN‑/24 (254 адреса — быстро). Работа `NsdManager` поверх интерфейса `bt-pan` — **не
  проверено**.
- Главный нюанс: Windows отнесёт PAN к профилю «Общедоступная сеть», где входящие соединения
  блокируются строже; правило брандмауэра компаньона и VLC должно включать профиль Public
  (ожидаемо, **не проверено**).
- Плюс: поверх PAN работает всё «LAN‑ное», включая уровень 0 (прямой VLC HTTP), без компаньона.
  Минус: ручные клики с обеих сторон при каждом сеансе (автоподключение PAN в Windows — **не
  проверено**), и телефон при этом должен держать tethering включённым.

### 1e. Wi‑Fi Direct / точка доступа на телефоне — кратко

- **Хотспот телефона**: ПК подключается к Wi‑Fi‑точке телефона, получает IP (исторически
  192.168.43.x), дальше всё как в LAN. Самый надёжный «без роутера» вариант, но ПК теряет свой
  Wi‑Fi‑интернет (если нет Ethernet), а телефон расходует батарею.
- **Хотспот Windows («Mobile hotspot»)**: телефон подключается к ноутбуку. Работает только если у
  ПК есть соединение, которым он «делится» (ограничение Windows; **не проверено** для случая без
  интернета).
- **Wi‑Fi Direct**: Windows API `Windows.Devices.WiFiDirect` (`WiFiDirectAdvertisementPublisher`,
  `WiFiDirectConnectionListener`) живо, а `Windows.Devices.WiFiDirect.Services` помечено deprecated
  ([namespace](https://learn.microsoft.com/en-us/uwp/api/windows.devices.wifidirect.services)); на
  Android — `WifiP2pManager`. Совместимость Windows↔Android P2P исторически капризна, код с двух
  сторон нетривиальный — **не рекомендуется**.

---

## 2. Обнаружение ПК из телефона в LAN

### 2a. mDNS / DNS‑SD

**Android, `NsdManager`.** Долгая история багов: пустые TXT‑записи, «onServiceLost → сразу
onServiceFound» (флаппинг), капризы при нескольких одновременных discovery
([TinyDNSSD](https://github.com/youviewtv/tinydnssd), [RxBonjour → jmDNS](https://github.com/Ennova-IT/RxBonjour),
[Guardian Project](https://dev.guardianproject.info/issues/2900)). Что изменилось:
- API 34: `registerServiceInfoCallback(…)` вместо `resolveService` (последний deprecated —
  в документации встречаются и API 34, и API 36), `NsdServiceInfo.getHostAddresses()` вместо
  `getHost()` ([NsdManager](https://developer.android.com/reference/android/net/nsd/NsdManager),
  [NsdServiceInfo](https://developer.android.com/reference/android/net/nsd/NsdServiceInfo)).
- Начиная с «T extensions 7» система сама управляет приёмом multicast для foreground‑приложений;
  раньше требовался `WifiManager.MulticastLock` + `CHANGE_WIFI_MULTICAST_STATE`
  ([NsdManager](https://developer.android.com/reference/android/net/nsd/NsdManager),
  [MulticastLock](https://developer.android.com/reference/android/net/wifi/WifiManager.MulticastLock)).
- После Android 12 нативный демон не обязан быть живым; использовать только `NsdManager`.

Рекомендуемый подход на телефоне:
1. Одно активное `discoverServices("_vlcremote._tcp", PROTOCOL_DNS_SD, …)` за раз; стоп в `onStop`.
2. На API 34+ — `registerServiceInfoCallback` для найденной записи; ниже — `resolveService` с
   очередью (не более одного resolve одновременно).
3. Дебаунс `onServiceLost` (2–3 с) перед удалением из списка.
4. Держать `MulticastLock` на время поиска на API < 34 (и на всякий случай вообще).
5. Параллельно слать UDP‑маяк (2b) и предлагать ручной ввод `host:port`/QR.
6. Не полагаться на TXT: всё нужное (имя ПК, версия протокола, отпечаток) отдавать по HTTP
   `GET /info` после обнаружения.

**Windows — чем объявлять сервис.**

| Вариант | Объявляет (advertise)? | Статус | Вердикт |
|---|---|---|---|
| Win32 `DnsServiceRegister` (dnsapi.dll, windns.h), встроенный responder Windows | да | документирован, Windows 10+, «registration is tied to the lifetime of the calling process» ([DnsServiceRegister](https://learn.microsoft.com/en-us/windows/win32/api/windns/nf-windns-dnsserviceregister), [DnsServiceConstructInstance](https://learn.microsoft.com/en-us/windows/win32/api/windns/nf-windns-dnsserviceconstructinstance)) | **рекомендуется**; **проверено локально** — регистрация и дерегистрация `VlcRemoteProbe._vlcremote._tcp.local` вернули status 0 |
| WinRT `Windows.Networking.ServiceDiscovery.Dnssd.DnssdServiceInstance` | да | работает, но `RegisterStreamSocketListenerAsync` привязан к WinRT `StreamSocketListener`; `DnssdServiceWatcher` помечен «not supported» ([namespace](https://learn.microsoft.com/en-us/uwp/api/windows.networking.servicediscovery.dnssd)) | **проверено локально** (`status=Success`), но неудобно рядом с Kestrel — порт занят не WinRT‑слушателем |
| Makaretu.Dns.Multicast (net‑mdns) | да (`ServiceDiscovery.Advertise`) | 0.27.0 от 05.11.2019, с тех пор релизов нет ([NuGet](https://www.nuget.org/packages/Makaretu.Dns.Multicast), [GitHub](https://github.com/richardschneider/net-mdns)); свой responder на 5353 рядом со встроенным (конфликты описаны для других стеков: [Gajim #9245](https://dev.gajim.org/gajim/gajim/-/issues/9245)) | нет |
| Zeroconf (novotnyllc) 3.7.16 | **нет**, только browse/resolve ([README](https://github.com/novotnyllc/Zeroconf)) | жив (push 10/2025) | не подходит для стороны ПК |
| Tmds.MDns 0.8.0 | **нет**, только `ServiceBrowser` ([README](https://github.com/tmds/Tmds.MDns)) | 04/2023 | не подходит |
| Mono.Zeroconf 0.9.0 | да | архив 2011 г., требует Bonjour/Avahi ([mono‑project](https://www.mono-project.com/archived/monozeroconf/)) | нет |

Проверенный P/Invoke (достаточно `net9.0-windows`, без WinRT):

```csharp
static class DnsSd
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate void RegisterComplete(uint status, IntPtr ctx, IntPtr instance);

    [StructLayout(LayoutKind.Sequential)]
    public struct DNS_SERVICE_REGISTER_REQUEST
    {
        public uint Version;                 // 1
        public uint InterfaceIndex;          // 0 = все интерфейсы
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

// использование (DNS_REQUEST_PENDING = 9506, в callback status 0 = успех)
var inst = DnsSd.DnsServiceConstructInstance(
    $"{Environment.MachineName}._vlcremote._tcp.local", $"{Environment.MachineName}.local",
    IntPtr.Zero, IntPtr.Zero, 47555, 0, 0, 2, new[] { "v", "id" }, new[] { "1", pcId });
var req = new DnsSd.DNS_SERVICE_REGISTER_REQUEST { Version = 1, pServiceInstance = inst,
    pRegisterCompletionCallback = Marshal.GetFunctionPointerForDelegate(callback) };
uint rc = DnsSd.DnsServiceRegister(ref req, IntPtr.Zero);   // 9506 → ждать callback
```

Делегат `callback` держать в поле (GC), при выходе — `DnsServiceDeRegister` (тоже асинхронно) и
`DnsServiceFreeInstance`. На выход процесса Windows снимает запись сама.

Дополнительно: Windows 10 1703+ отвечает на `<имя-ПК>.local` встроенным responder’ом
([technote](https://slightfuture.com/technote/windows-mdns-dnssd/), **не проверено** локально), а
резолвер Android с ноября 2021 (модуль DnsResolver) прозрачно резолвит `*.local` через
`getaddrinfo()` (кроме VPN и mobile data) ([AOSP DNS Resolver](https://source.android.com/docs/core/ota/modular-system/dns-resolver)).
То есть пользователь может ввести `DESKTOP-9SO2EON.local` вместо IP — удобный fallback даже без
компаньона.

### 2b. UDP‑маяк

Схема: телефон шлёт `{"q":"vlcremote","v":1}` на `255.255.255.255:47556` и на directed‑broadcast
своей подсети; компаньон отвечает unicast `{"name":"DESKTOP-…","port":47555,"id":"…","v":1}`.
Альтернатива — компаньон сам бикает раз в 2 с (хуже: шум в сети, телефон должен слушать).
Плюсы: 40 строк кода с обеих сторон, не зависит от mDNS‑стеков. Минусы: некоторые роутеры режут
broadcast, при **AP/client isolation** между беспроводными клиентами не ходит ничего (ни unicast,
ни broadcast — тогда и VLC HTTP недоступен); проводной ПК ↔ беспроводной телефон обычно работает
(«communications between wireless and wired clients are allowed» — [batman‑adv](https://www.open-mesh.org/doc/batman-adv/Ap-isolation.html);
Meraki в bridge‑режиме блокирует всё, кроме шлюза — [Meraki](https://documentation.meraki.com/Wireless/Operate_and_Maintain/How_Tos/Firewall_and_Traffic_Shaping/Wireless_Client_Isolation);
у TP‑Link изоляция «does not stop the flow of the multicast and broadcast traffic» — [форум](https://community.tp-link.com/en/smart-home/forum/topic/661906)).
На Android приём broadcast на некоторых устройствах тоже требует `MulticastLock` (не проверено).
Вывод: маяк — дешёвое дополнение к mDNS, не замена ручному вводу.

### 2c. Скан /24 по порту 8080 — что делают существующие пульты

- VlcFreemote заявляет «will automatically detect» VLC, метод не раскрыт
  ([F‑Droid](https://f-droid.org/en/packages/com.nicolasbrailo.vlcfreemote/), [GitHub](https://github.com/nicolasbrailo/VlcFreemote));
  VLC Direct требует ввести IP вручную; «Remote for VLC» ([samicemalone](https://github.com/samicemalone/android-vlc-remote)) —
  ручной ввод. Утверждать, что они сканируют подсеть, по источникам нельзя (**не проверено**); на
  практике «автодетект» без компаньона может быть только сканом.
- Реализация скана: TCP‑connect на `x.y.z.1–254:8080` с таймаутом 200–300 мс, параллельно 32–64
  потока → 2–5 с; подтверждение — `GET /requests/status.json` без пароля должен вернуть
  `401` с `WWW-Authenticate: Basic realm="VLC stream"` (строка realm подтверждена в
  [live‑probe](live-probe.md)). Нестандартный порт — поле в настройках.
- **Можно ли обнаружить чистый VLC иначе — нет.** `share/lua/intf/http.lua` в ветках 3.0.x и master
  не содержит ни `vlc.sa`, ни `_http._tcp`, ни bonjour/announce (проверено по исходникам:
  [3.0.x](https://github.com/videolan/vlc/blob/3.0.x/share/lua/intf/http.lua),
  [master](https://github.com/videolan/vlc/blob/master/share/lua/intf/http.lua)). Патч 2020 г.
  «lua: http: Announce the web interface over mdns» ([vlc‑devel](https://mailman.videolan.org/pipermail/vlc-devel/2020-September/137015.html))
  получил возражения («should not be advertised until it is secured» —
  [ответ](https://mailman.videolan.org/pipermail/vlc-devel/2020-September/137017.html);
  [вопрос про localhost](https://mailman.videolan.org/pipermail/vlc-devel/2020-September/137021.html))
  и в код не попал. UPnP/SSDP‑сервера у VLC тоже нет (модули service discovery — клиентские).

### 2d. Брандмауэр Windows

- По умолчанию входящие заблокированы в профилях Private/Public. При первом `listen` Windows
  показывает «Windows Security Alert»; нажатие **Cancel** молча создаёт блокирующие правила TCP+UDP
  для exe, и диалог больше не появляется ([Rebex](https://blog.rebex.net/beware-pressing-cancel-on-windows-firewall-security-alert)).
  Разблокировать из диалога может пользователь с правами администратора/network operator
  ([MS docs](https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-server-2008-R2-and-2008/cc766312(v=ws.10)));
  поведение для стандартного пользователя (запрос UAC‑учётки) — **не проверено**. На этом ПК
  правил для `vlc.exe` сейчас нет (проверено `Get-NetFirewallRule`), т. е. включение HTTP‑интерфейса
  VLC здесь вызовет такой диалог.
- Добавление правила требует администратора в обоих API:
  `netsh advfirewall firewall add rule name="VLC Remote Companion" dir=in action=allow program="<exe>" protocol=TCP localport=47555 profile=private,public`
  ([netsh advfirewall](https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-server-2008-R2-and-2008/dd734783(v=ws.10)))
  и COM `INetFwRules::Add` через `HNetCfg.FwPolicy2` (ошибка прав, если не elevated;
  [INetFwRules::Add](https://learn.microsoft.com/en-us/windows/win32/api/netfw/nf-netfw-inetfwrules-add)).
  Без администратора способа нет.
- Стратегия: установка per‑user без UAC (Velopack), а при первом запуске компаньон проверяет
  наличие своего правила (`INetFwPolicy2.Rules` или `netsh … show rule name=…`) и предлагает кнопку
  «Разрешить в брандмауэре», которая запускает `netsh` через `ProcessStartInfo { Verb = "runas" }`
  (одно UAC‑окно). Тем же действием добавить правило для `vlc.exe` (путь из
  `HKLM\SOFTWARE\VideoLAN\VLC\InstallDir` — на этом ПК ключ есть, значение
  `C:\Program Files\VideoLAN\VLC`, `Version=3.0.23`; проверено). Правило по программе переживает
  обновления, потому что Velopack всегда держит exe в `%LocalAppData%\<AppId>\current\`
  («avoids problems with firewall rules» — [Velopack: migrating from Squirrel](https://docs.velopack.io/migrating/squirrel)).
- Ограничить правило `remoteip=localsubnet` — дешёвая защита от случайного проброса.

---

## 3. Протокол компаньон ↔ телефон по LAN

### 3a. WebSocket‑сервер в .NET

| Вариант | Размер/зависимости | Нужен ли `urlacl`/админ | Оценка |
|---|---|---|---|
| **Kestrel, minimal API** (`FrameworkReference Microsoft.AspNetCore.App`, `CreateSlimBuilder`, `UseWebSockets`) | измерено: WinForms‑трей self‑contained single‑file 107,7 МБ → с Kestrel 133,3 МБ (+25,6); со сжатием 48,1 → 59,3 МБ (+11,2); framework‑dependent 0,2 МБ, но тогда у пользователя должен стоять и ASP.NET Core Runtime | нет: Kestrel — собственные сокеты, не HTTP.sys | **рекомендуется**: заодно HTTP‑эндпоинты (`/info`, `/pair`, `/art`), TLS при желании, WebSockets из коробки ([docs](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/websockets)) |
| `HttpListener` + `HttpListenerContext.AcceptWebSocketAsync` | в BCL, 0 МБ | **да**: не‑localhost префиксы требуют `netsh http add urlacl` от администратора ([HttpListener](https://learn.microsoft.com/en-us/dotnet/fundamentals/runtime-libraries/system-net-httplistener), [URL ACL](https://www.awise.us/2006/04/10/httplistener-and-url-acls.html)) | нет |
| `TcpListener` + ручной HTTP‑upgrade + `WebSocket.CreateFromStream(stream, isServer:true, …)` | в BCL, 0 МБ, ~120 строк своего кода ([CreateFromStream](https://learn.microsoft.com/en-us/dotnet/api/system.net.websockets.websocket.createfromstream)) | нет | план Б, если размер критичен |
| WatsonWebsocket 4.1.8 (02/2026) | 0 зависимостей ([NuGet](https://www.nuget.org/packages/WatsonWebsocket)) | внутри `HttpListener` → та же проблема `urlacl` (**не проверено**) | нет |
| Fleck 1.2.0 | raw sockets, последний релиз 2016 ([сравнение](https://www.esegece.com/products/websockets/comparison-net/)) | нет | мёртв |

Минимальный Kestrel‑сервер (собран и опубликован в пробе):

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
    await session.RunAsync(new WebSocketLineTransport(ws));   // тот же NDJSON, что и по RFCOMM
});
_ = app.RunAsync();           // рядом Application.Run() трея
```

На Android — OkHttp `WebSocket` (есть в `okhttp3`, [docs](https://square.github.io/okhttp/)) или Ktor
client; оба — один класс кода.

### 3b. TLS или нет

В домашней WPA2‑сети трафик между устройствами может прочитать тот, кто знает пароль Wi‑Fi (то есть
домочадцы). Угроза — утечка токена пульта → чужой может ставить фильмы на паузу. Для v1 разумно:
**без TLS**, но с версионированным протоколом и полем `fp` (отпечаток сертификата) в QR,
зарезервированным под будущий self‑signed TLS с пиннингом (Kestrel: `UseHttps(cert)`; Android:
OkHttp `CertificatePinner`). Это дешевле, чем сразу тащить сертификаты, и ничего не ломает потом.
`ws://` в Android требует `usesCleartextTraffic`/network security config для LAN — помнить.

### 3c. Сопряжение и аутентификация

1. Пользователь в трее нажимает «Подключить телефон». Компаньон генерирует 6‑значный код (TTL 2 мин,
   5 попыток), показывает окно с QR **на мониторе‑телевизоре** (его видно с дивана) и тем же кодом.
   QR: `vlcremote://pair?host=192.168.1.20&port=47555&id=<pcId>&code=123456&v=1&fp=`.
2. Телефон открывает WS и шлёт `{"t":"pair","code":"123456","device":"Pixel 8"}`.
3. Компаньон возвращает `{"t":"paired","token":"<32 случайных байта base64url>"}`; у себя хранит
   SHA‑256 токена и имя устройства; телефон — в EncryptedSharedPreferences/DataStore.
4. Далее первая строка каждой сессии: `{"t":"auth","token":"…"}`; без неё — только `/info`.
5. По Bluetooth — тот же токен (канал уже аутентифицирован сопряжением, но единый код проще).
6. Отзыв: список устройств в трее → «Забыть».

### 3d. Кадрирование, общее для Bluetooth и WebSocket

**NDJSON**: одно JSON‑значение на строку, `\n` — разделитель, UTF‑8, внутри строки переводов строк
нет ([ndjson spec](https://github.com/ndjson/ndjson-spec)). По RFCOMM — буквально байты потока;
по WebSocket — одно текстовое сообщение = одна строка (символ `\n` в конце допустим). Плюсы:
`BufferedReader.readLine()` / `StreamReader.ReadLineAsync()`, читается в логах. Ограничения:
максимум 64 КБ на строку, бинарные данные (обложки) — только через HTTP `/art` по LAN (по Bluetooth —
не передавать или base64‑миниатюра ≤ 20 КБ). Length‑prefix даёт выигрыш только для бинарных кадров —
здесь не нужен.

Конверт:

```json
{"t":"cmd","id":7,"m":"seek","a":{"rel":30}}
{"t":"res","id":7,"ok":true}
{"t":"evt","m":"state","d":{"state":"playing","time":912,"length":10705,"volume":256,"atrack":2}}
{"t":"ping"}  /  {"t":"pong"}
```

### 3e. Push против опроса

Компаньон держит один RC‑сокет к VLC (push‑строки `status change: (…)`, см. [live‑probe](live-probe.md))
и опрашивает `status.json` каждые 500–1000 мс (локальная задержка ~21 мс), считает diff и шлёт
телефону только изменения. Телефон не опрашивает вообще — только подписка и команды. Heartbeat
`ping/pong` раз в 20 с на уровне приложения (работает одинаково по BT и WS; у Kestrel свой
`KeepAliveInterval`, по умолчанию 2 мин — уменьшить). Отсутствие `pong` 2 раза подряд →
переподключение.

---

## 4. Компаньон ↔ VLC

### 4a. HTTP‑интерфейс VLC

Тот же API, что и у телефона на уровне 0: `requests/status.json?command=…`, `playlist.json`,
`browse.json?uri=…`, `/art`; команды `pl_play/pl_pause/pl_stop/pl_next/pl_previous`, `seek`,
`volume`, `rate`, `fullscreen`, `audio_track/subtitle_track/video_track`, `audiodelay/subdelay`,
`aspectratio`, `in_play/in_enqueue` ([README requests](https://github.com/videolan/vlc/blob/3.0.x/share/lua/http/requests/README.txt);
подробности и странности — [live‑probe](live-probe.md)). Аутентификация — Basic, пустой логин,
пароль `--http-password`; без пароля `http.lua` в 3.0.x отдаёт `403` на всё и пишет «Password unset,
insecure web interface disabled» (проверено по исходнику). Опция `--http-host`, переданная Lua,
игнорируется с сообщением «HTTP host ignored» — хост/порт задаются core‑опциями `--http-host`
`--http-port` ([wiki http_intf](https://wiki.videolan.org/Documentation:Modules/http_intf/)).
Компаньон может держать HTTP к VLC только на `127.0.0.1` и быть единственной LAN‑точкой входа.

### 4b. RC‑интерфейс по TCP

`--extraintf rc --rc-host 127.0.0.1:4212 --rc-quiet` (на Windows без `--rc-quiet` открывается
консоль, и команды принимаются только после Enter — [форум](https://forum.videolan.org/viewtopic.php?p=243197));
`--rc-host` — «Accept commands over TCP instead of stdin» ([wiki rc](https://wiki.videolan.org/Documentation:Modules/rc/)).
Аутентификации нет вообще → **только 127.0.0.1**. Одно соединение за раз, текстовый формат,
push‑события, `adev` (список/выбор аудиоустройств Windows), `atrack/strack/vtrack` с маркером
текущей дорожки, `quit` ([live‑probe](live-probe.md)). Компаньон — единственный RC‑клиент,
мультиплексирует запросы телефонов.

### 4c. Плеер внутри компаньона: LibVLCSharp

LibVLCSharp 3.8.x + VideoLAN.LibVLC.Windows 3.0.23(.1) ([LibVLCSharp](https://www.nuget.org/packages/LibVLCSharp),
[LibVLC.Windows](https://www.nuget.org/packages/VideoLAN.LibVLC.Windows); точные даты релизов — **не
проверено**).

| За | Против |
|---|---|
| Полный программный контроль: устройство вывода (`SetOutputDevice`), дорожки, события без парсинга, окно на нужном мониторе, fullscreen без хаков `SetWindowPos` | Дублирует плеер: свои настройки, кэш, плагины, скины, горячие клавиши пользователя не действуют |
| Нет гонок с «кто запустил VLC» и с `vlcrc` | Нативные DLL ~ +80 МБ в дистрибутиве (размер пакета LibVLC.Windows — **не проверено**) |
| Один процесс, один протокол | Пользователь, открывший файл двойным кликом, попадёт в «не тот» плеер |
|  | Больше поверхности для багов (вывод видео в WinForms‑хосте, HDR/аппаратное декодирование) |

Вердикт (совпадает с ADR‑0001): не для v1; возможный «встроенный режим» позже.

### 4d. Запуск `vlc.exe` с аргументами

```text
"C:\Program Files\VideoLAN\VLC\vlc.exe" --extraintf http:rc --http-host 127.0.0.1 --http-port 8080
  --http-password <pw> --rc-host 127.0.0.1:4212 --rc-quiet
  --fullscreen --qt-fullscreen-screennumber=1 "D:\shared\film.mkv"
```

- `--qt-fullscreen-screennumber` — **индекс с нуля** в `QGuiApplication::screens()`; значения вне
  диапазона игнорируются (окно не переносится) — проверено по
  [main_interface.cpp 3.0.x](https://github.com/videolan/vlc/blob/3.0.x/modules/gui/qt/main_interface.cpp)
  (`if (numscreen >= 0 && numscreen < QGuiApplication::screens().length())`, затем
  `windowHandle()->setScreen(...)`, `move(...)`). Умолчание `-1` (в `vlcrc`:
  `#qt-fullscreen-screennumber=-1`, проверено локально). Работает только для встроенного видео
  ([vlc‑devel 2019](https://mailman.videolan.org/pipermail/vlc-devel/2019-October/128255.html)).
  Порядок экранов Qt и `Screen.AllScreens` может не совпадать — **не проверено**; надёжнее
  переносить окно самим (раздел 5).
- `--one-instance --playlist-enqueue <файл>` добавляет файл в уже запущенный экземпляр
  ([форум](https://forum.videolan.org/viewtopic.php?p=221412), [ещё](https://forum.videolan.org/viewtopic.php?p=264918));
  `--started-from-file` встречается только в форумных постах — **не проверено**. Важно: если
  VLC уже запущен в one‑instance‑режиме, **аргументы интерфейсов второго запуска не применяются**
  (передаются только элементы плейлиста) — поэтому либо `extraintf` должен быть в `vlcrc`, либо
  компаньон владеет жизненным циклом VLC.
- Прочее полезное: `--no-video-title-show`, `--qt-minimal-view`, `--no-qt-fs-controller`,
  `--start-time`, `--sub-file`, `--audio-track/--sub-track` — по `vlc --longhelp`.

### 4e. Автонастройка через `vlcrc`

Файл `%APPDATA%\vlc\vlcrc` ([wiki Vlcrc](https://wiki.videolan.org/Vlcrc)). Формат по
[src/config/file.c 3.0.x](https://github.com/videolan/vlc/blob/3.0.x/src/config/file.c) и локальному
файлу (3479 строк, записан VLC 3.0.21; проверено):

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

- Значение, равное умолчанию, пишется закомментированным `#key=value`; изменённое — `key=value`.
  Заголовок с UTF‑8 BOM. Секции `[module] # описание`.
- `config_SaveConfigFile` **не читает старый файл**: сериализует загруженные в память модули целиком и
  заменяет файл атомарно (`vlcrc.<pid>` → rename; на Windows сначала unlink). Строки неизвестных
  опций при загрузке игнорируются и при сохранении **теряются**. Если файл read‑only — VLC
  отказывается сохранять (комментарий в коде: «Some users make vlcrc read-only to prevent changes»).
- Когда VLC сохраняет: Preferences → Save; автосохранение `config_AutoSaveConfigFile` только при
  флаге `config_dirty` (когда именно он выставляется, кроме диалога настроек, — **не проверено**).

Безопасный алгоритм правки компаньоном:
1. Только когда `vlc.exe` не запущен (иначе VLC может перезаписать файл при выходе).
2. Прочитать файл как UTF‑8 с BOM, построчно. Для каждого ключа (`extraintf`, `http-password`,
   `http-host`, `http-port`, `rc-host`) найти **первую** строку `^#?key=` и заменить на `key=value`
   (ключ остаётся в своей секции). Если ключа нет — дописать в конец секции `[core]`, для
   `rc-host` — `[rc]`, для `http-password` — `[lua]` (в 3.0.x `http-password` объявлен модулем lua).
   Загрузчик ищет опции по имени; роль секций при чтении — **не проверено**, поэтому секции
   соблюдать.
3. Записать во временный файл рядом и `File.Move(tmp, vlcrc, overwrite: true)`.
4. Если `vlcrc` нет — создать минимальный файл с заголовком и нужными строками (VLC должен принять
   файл без полного списка опций — **не проверено**).
5. `extraintf=http:rc` — несколько интерфейсов через двоеточие (синтаксис из live‑probe).
6. Пароль HTTP хранить и в `vlcrc`, и у компаньона (компаньон им же пользуется).

Детект/управление процессом: `Process.GetProcessesByName("vlc")`; путь установки —
`HKLM\SOFTWARE\VideoLAN\VLC\InstallDir` (проверено) с fallback на `%ProgramFiles%\VideoLAN\VLC\vlc.exe`;
«жив ли HTTP» — TCP‑connect на порт + ожидание `401`. Корректное закрытие — RC `quit` (проверено в
live‑probe; `command=key&val=quit` по HTTP не закрывает), затем `WaitForExit(5s)` → `Kill()`.
Запуск по требованию — `Process.Start` с аргументами из 4d; после старта первые запросы могут
вернуть пустой ответ — ретрай (live‑probe).

---

## 5. Экран и телевизор из компаньона (Windows 11, .NET)

**Перечисление мониторов.**
- WinForms `Screen.AllScreens` (`DeviceName` вида `\\.\DISPLAY2`, `Bounds`, `Primary`)
  ([docs](https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.screen.allscreens)) —
  достаточно для трей‑приложения.
- Win32 `EnumDisplayMonitors` + `GetMonitorInfo` ([docs](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enumdisplaymonitors)).
- WinRT `Windows.Devices.Display.DisplayMonitor` даёт человеческое имя и тип подключения
  (HDMI/DisplayPort/внутренний) — удобно, чтобы в пульте написать «LG TV (HDMI)»
  ([docs](https://learn.microsoft.com/en-us/uwp/api/windows.devices.display.displaymonitor)).

**Найти окно VLC и перенести на ТВ.**
- `Process.MainWindowHandle` — работает, пока у процесса есть видимое главное окно; при
  `--no-embedded-video` видео живёт в отдельном окне.
- `EnumWindows` → фильтр по PID (`GetWindowThreadProcessId`) и классу окна. Для VLC 3 (Qt5) в
  сообществе используют класс `Qt5QWindowIcon` и заголовок «VLC media player»
  ([пример VBA](https://xf2023.herber.de/1684to1688/1686695_Fenstergroee_mit_VBA_ermitteln.html));
  заголовок меняется на `<имя файла> - VLC media player` (проверено по `setVLCWindowsTitle` в
  исходнике), класс — **не проверено** локально (VLC не был запущен), проверить Spy++. VLC 4 (Qt6)
  будет иметь другой класс.
- Перенос: `SetWindowPos(hWnd, IntPtr.Zero, tv.Bounds.X, tv.Bounds.Y, w, h, SWP_NOZORDER | SWP_SHOWWINDOW)`
  ([docs](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos)), затем
  `fullscreen` через HTTP/RC: VLC уходит в полный экран на том мониторе, где стоит окно (суть
  патча 2017 г.: [vlc‑devel](https://mailman.videolan.org/pipermail/vlc-devel/2017-December/117078.html)).
  Если уже в полном экране — сначала `fullscreen off`, перенести, `fullscreen on`.

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

**Режим проецирования.** `SetDisplayConfig(0, null, 0, null, SDC_TOPOLOGY_EXTEND|SDC_APPLY)` /
`SDC_TOPOLOGY_EXTERNAL` / `SDC_TOPOLOGY_CLONE` / `SDC_TOPOLOGY_INTERNAL` — «requests the last …
configuration from the persistence database»
([SetDisplayConfig](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setdisplayconfig));
текущую топологию читать `QueryDisplayConfig(QDC_DATABASE_CURRENT, …, out topologyId)`
([QueryDisplayConfig](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-querydisplayconfig)).
`DisplaySwitch.exe /internal|/clone|/extend|/external` — после 22H2 ключи со слэшем, по сообщениям,
перестали работать, вместо них числа 1–4 ([Microsoft Q&A](https://learn.microsoft.com/en-us/answers/questions/1036148/displayswitchexe-behavior-on-windows-11-22h2.html),
[winaero](https://winaero.com/switch-between-multiple-displays-monitors-directly-with-a-shortcut-or-from-the-command-line-in-windows-7-and-windows-8/));
на этом ПК `DisplaySwitch.exe` 10.0.26100 есть, аргументы — **не проверено**. Использовать
`SetDisplayConfig` (документирован, без дочерних процессов).

```csharp
[DllImport("user32.dll")] static extern int SetDisplayConfig(uint n1, IntPtr p1, uint n2, IntPtr p2, uint flags);
const uint SDC_TOPOLOGY_INTERNAL = 1, SDC_TOPOLOGY_CLONE = 2, SDC_TOPOLOGY_EXTEND = 4, SDC_TOPOLOGY_EXTERNAL = 8, SDC_APPLY = 0x80;
static bool Extend()   => SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_TOPOLOGY_EXTEND  | SDC_APPLY) == 0;
static bool TvOnly()   => SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_TOPOLOGY_EXTERNAL | SDC_APPLY) == 0;
```

**Выключить/включить дисплей.** `SendMessage(HWND_BROADCAST, WM_SYSCOMMAND, SC_MONITORPOWER, 2)`
(2 — off, 1 — low power, −1 — on, последнее недокументировано)
([WM_SYSCOMMAND](https://learn.microsoft.com/en-us/windows/win32/menurc/wm-syscommand),
[CodeProject](https://www.codeproject.com/KB/system/display_states.aspx)). На Windows 11 любое
движение мыши/клавиша включает экран обратно, на части ноутбуков с Modern Standby поведение
отличается ([отчёт](https://en.delphipraxis.net/topic/13282-turning-monitor-on-laptop-puts-application-to-sleep-also)).
Для «включить» надёжнее имитировать ввод (`SendInput` с нулевым движением мыши). Телевизор от этого
не выключается — он видит «нет сигнала» и уходит в standby по своему таймеру.

**HDMI‑CEC.** Из HDMI‑порта ноутбука CEC недоступен (видеокарты ПК не выводят CEC‑линию). Решение —
USB‑CEC‑адаптер Pulse‑Eight между ноутбуком и ТВ + libcec: `tv_on.cmd`/`tv_off.cmd`, выбор входа
`-s -p N` ([Pulse‑Eight](https://support.pulse-eight.com/support/solutions/articles/30000027391),
[что такое libcec](https://support.pulse-eight.com/support/solutions/articles/30000053002-what-is-the-pulse-eight-cec-adapter-and-libcec-)).
Из .NET — запуск `cec-client` процессом (есть и .NET‑обёртка LibCecSharp — **не проверено**).
Опциональная фича «если найден адаптер».

**Не спать во время фильма.** `SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED)`
на время `playing`, `SetThreadExecutionState(ES_CONTINUOUS)` — снять
([docs](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-setthreadexecutionstate)).
Сам VLC во время видео тоже блокирует гашение экрана (общее поведение, **не проверено** для
3.0.23 на Win11); компаньону нужен свой флаг для сценариев «пауза, но экран не гасить» и
«выключить экран, но ПК не усыплять» (например, аудио).

**Сон/гибернация/выключение.** `SetSuspendState(bHibernate, bForce /*no effect*/, bWakeupEventsDisabled)`
требует привилегии `SE_SHUTDOWN_NAME` через `AdjustTokenPrivileges` (у обычного пользователя она
есть, но выключена) ([docs](https://learn.microsoft.com/en-us/windows/win32/api/powrprof/nf-powrprof-setsuspendstate));
`shutdown.exe /s /t 0`, `/h` (гибернация), `/r` ([docs](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/shutdown)),
сна у `shutdown.exe` нет; известная ловушка `rundll32 powrprof.dll,SetSuspendState 0,1,0`
гибернирует, если гибернация включена → для сна использовать P/Invoke `SetSuspendState(false, …)`.
Блокировка — `LockWorkStation()`.

**Wake‑on‑LAN.** Магический пакет (UDP на 255.255.255.255:9, 6×FF + 16×MAC) из телефона — тривиально;
MAC ПК компаньон отдаёт при сопряжении. Но ноутбук по Wi‑Fi: WoWLAN «supported in most wireless
cards» по словам Intel ([Intel community](https://community.intel.com/t5/Wireless/Wake-on-Wireless-Lan-WOWLAN-compatibility/m-p/1451835)),
на практике зависит от адаптера/драйвера/BIOS и часто не срабатывает
([AnandTech](https://forums.anandtech.com/threads/difficulty-setting-up-wowlan.2630657/)); с Ethernet
надёжно. В пульте — кнопка «Разбудить» как best‑effort с подсказкой.

---

## 6. Звук из компаньона

**Переключение устройства вывода.**
1. **Внутри VLC** (документировано, без хаков): RC `adev` без аргумента → список устройств Windows
   с ID `{0.0.0.00000000}.{GUID}` и `*` на текущем; `adev <id>` переключает; на старте —
   `--aout mmdevice --mmdevice-audio-device=<id>` ([live‑probe](live-probe.md)). Это закрывает
   «динамики ноутбука ↔ HDMI ТВ ↔ Bluetooth‑наушники» для фильма, не трогая систему.
2. **Системное устройство по умолчанию**: публичного API нет; все утилиты (SoundSwitch,
   AudioEndPointController, Sunshine, AudioSwitcher) используют недокументированный COM
   `IPolicyConfig::SetDefaultEndpoint(deviceId, ERole)`
   ([AudioEndPointController](https://github.com/Belphemur/AudioEndPointController),
   [Sunshine PolicyConfig.h](https://docs.lizardbyte.dev/projects/sunshine/latest/PolicyConfig_8h.html),
   [MS Q&A](https://learn.microsoft.com/en-za/answers/questions/226483/set-active-audio-endpoint)).
   Работает на Windows 10/11 по отчётам сообщества; официально может сломаться в любом обновлении.
   GUID `CLSID {870af99c-171d-4f9e-af0d-e63df40c2bc9}` / `IID {f8679f50-850a-41cf-9c72-430f290290c8}`
   широко цитируются в коде этих проектов, но страницу с ними в этом исследовании получить не
   удалось — **сверить с `PolicyConfig.h`** перед использованием. Реализовать как 60‑строчный
   `[ComImport]` интерфейс самим (порядок методов vtable критичен) и обернуть в try/catch с
   graceful‑fallback. AudioSwitcher.AudioApi (3.1.0‑beta3, [NuGet](https://www.nuget.org/packages/AudioSwitcher.AudioApi))
   — в бете, даты последней активности **не проверены**; тащить ради одного вызова не стоит.
3. Перечисление устройств и **системная громкость**: NAudio ([GitHub](https://github.com/naudio/NAudio)),
   `MMDeviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).AudioEndpointVolume`
   (`MasterVolumeLevelScalar`, `Mute`) — это `IAudioEndpointVolume`
   ([docs](https://learn.microsoft.com/en-us/windows/win32/api/endpointvolume/nn-endpointvolume-iaudioendpointvolume)).
   Громкость VLC (0..512, 256 = 100 %) — отдельная сущность ([live‑probe](live-probe.md)).

```csharp
using NAudio.CoreAudioApi;
using var en = new MMDeviceEnumerator();
foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
    Console.WriteLine($"{d.ID}  {d.FriendlyName}");
var def = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
def.AudioEndpointVolume.MasterVolumeLevelScalar = 0.35f;   // 35 %
```

**Что выносить в пульт.** Основной слайдер — громкость VLC; второй, «системная громкость» + mute
(для случая, когда VLC на 100 %, а всё равно тихо/громко); список устройств вывода VLC с
человеческими именами (`adev`); опционально «сделать устройством Windows по умолчанию»
(IPolicyConfig) — для сценария «наушники для всего». ID устройств одинаковы в VLC (`mmdevice`) и
CoreAudio — можно сопоставлять имена.

---

## 7. Форма компаньона на Windows

**Трей‑приложение.** Проще всего — WinForms `WinExe` (`net9.0-windows`, `UseWindowsForms`) с
`NotifyIcon` + `ContextMenuStrip`, без главного окна ([NotifyIcon](https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.notifyicon));
окна «QR для сопряжения» и «Настройки» — обычные формы по требованию. В пробе WinForms‑трей и
Kestrel собраны в одном процессе: `_ = app.RunAsync(); Application.Run();`. WPF — то же, но без
штатного трей‑контрола (нужен Hardcodet.NotifyIcon.Wpf); WinUI 3 — Windows App SDK, тяжелее и
сложнее с unpackaged‑режимом. Единственный экземпляр — `Mutex`.

**Автозапуск.** `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` — без администратора,
пользователь видит и может отключить в Диспетчере задач → Автозагрузка
([Run keys](https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys)).
Папка Startup — то же, но ярлыком. Планировщик — нужен только для «с наивысшими правами» (здесь
не требуется). Выбор: Run‑ключ, переключатель в меню трея.

**Размер публикации (измерено, .NET 9.0.310, win‑x64, Release):**

| Конфигурация | Трей (WinForms) | Трей + Kestrel |
|---|---|---|
| framework‑dependent, single‑file | 0,2 МБ (нужен .NET Desktop Runtime 9; для Kestrel — ещё ASP.NET Core Runtime) | 0,2 МБ |
| self‑contained, single‑file | 107,7 МБ | 133,3 МБ |
| self‑contained, single‑file, `EnableCompressionInSingleFile` | 48,1 МБ | 59,3 МБ |

Trimming WinForms не рассматривался (исторически несовместим — **не проверено** для .NET 9).
Для пользователей без .NET — self‑contained сжатый (~60 МБ скачивания, распаковка в память при
старте; старт на ~0,5–1 с медленнее — **не проверено**). Для себя — framework‑dependent.

**Установка без администратора и обновления.**
- **Velopack**: наследник Squirrel.Windows/Clowd.Squirrel («Clowd.Squirrel has been renamed to
  Velopack» — [NuGet Clowd.Squirrel](https://www.nuget.org/packages/Clowd.Squirrel)); ставит в
  `%LocalAppData%\<AppId>\current\` без UAC, `current` целиком заменяется при обновлении (логи и
  настройки хранить отдельно), обновления с любого статического URL или GitHub Releases
  ([Windows packaging](https://docs.velopack.io/packaging/operating-systems/windows),
  [C# getting started](https://docs.velopack.io/getting-started/csharp)); C#‑SDK + CLI `vpk`
  (`dotnet tool install -g vpk`; версии vpk и пакета должны совпадать). Дельта‑обновления и
  проверка подписи — по документации заявлены, в этом исследовании **не проверены**. Поток:
  `dotnet publish … --self-contained -r win-x64 -o publish` → `vpk pack --packId VlcRemoteCompanion --packVersion 1.0.0 --packDir publish` → `vpk upload github …`.
- Оригинальный Squirrel.Windows — без активности (точный статус репозитория **не проверен**); не брать.
- Минимум без фреймворка: zip на GitHub Releases + проверка `releases/latest` из трея и ссылка
  «скачать». Для домашнего проекта допустимо, но Velopack даёт то же за час работы.
- Неподписанный exe вызовет SmartScreen при первом запуске (общее знание); для себя — ок.

**Логирование.** `Microsoft.Extensions.Logging` + Serilog `File`‑sink с ротацией в
`%LocalAppData%\VlcRemoteCompanion\logs\` ([Serilog](https://serilog.net/)); без токенов и
паролей; кнопка «Открыть папку логов» в трее.

**Альтернативные языки компаньона.**
- **Rust**: `windows-rs` даёт те же WinRT (RFCOMM, DNS‑SD, Bluetooth) и COM (CoreAudio) API,
  `tray-icon` для трея, Velopack имеет Rust‑SDK; exe 3–6 МБ без рантайма
  ([windows‑rs](https://github.com/microsoft/windows-rs), [tray‑icon](https://github.com/tauri-apps/tray-icon)).
  Цена — другой язык, 2–3× времени на ту же поверхность Windows API.
- **Go**: `systray` есть ([getlantern/systray](https://github.com/getlantern/systray)), но WinRT/COM
  из Go — боль (cgo или ручные vtable), RFCOMM‑сервер через Winsock руками.
- Почему **C#/.NET**: вся нужная поверхность (WinRT Bluetooth и DNS‑SD, COM CoreAudio, Win32 окна/
  дисплеи, Kestrel) первоклассна и уже проверена; разница в 50 МБ дистрибутива для домашнего
  HTPC несущественна; разработчик знает язык.

---

## 8. Безопасность в домашней LAN

- **VLC HTTP**: Basic‑auth по открытому HTTP с пустым логином; пароль виден любому, кто может
  слушать Wi‑Fi (знает WPA2‑пароль). При наличии компаньона — `--http-host 127.0.0.1`: VLC вообще
  не виден в сети, единственная LAN‑точка — компаньон с токеном. Без компаньона (уровень 0) —
  принять риск, пароль не равен другим паролям пользователя.
- **RC**: без аутентификации — **только `127.0.0.1`**, никогда `0.0.0.0`.
- **Компаньон**: токен 256 бит, в хранилище — SHA‑256; код сопряжения живёт 2 минуты и только
  пока открыто окно «Подключить телефон»; лимит попыток; отвечать только адресам из RFC 1918 и
  link‑local (защита от случайного проброса порта); не делать UPnP‑маппинг; `/info` без секретов;
  правило брандмауэра на программу+порт, `remoteip=localsubnet`; при появлении TLS — пиннинг
  отпечатка из QR (TOFU).
- **Bluetooth**: сопряжение (SSP) даёт шифрование на канальном уровне; использовать
  аутентифицированные сокеты (`createRfcommSocketToServiceRecord` + `BluetoothEncryptionWithAuthentication`),
  не insecure‑вариант (MITM). Токен приложения — всё равно (единая модель, защита от чужого
  сопряжённого устройства).
- **Не выходить за LAN**: никаких облачных реле в v1; доступ из интернета — только через
  пользовательский VPN (WireGuard/Tailscale), который прозрачен для протокола.
- **Обновления**: только HTTPS (GitHub); по возможности подписывать релизы.

---

## Рекомендации

### Архитектура транспорта

```
   Android (Kotlin/Compose)                                Windows 11 HTPC
 ┌──────────────────────────┐                     ┌─────────────────────────────────────┐
 │ UI ── PlayerBackend ─────┼─ уровень 0/1 ───────┼─▶ VLC http intf :8080 (Lua, Basic)  │
 │        │                 │   (HTTP, без        │        ▲                 ▲          │
 │        │ CompanionBackend│    компаньона)      │        │ 127.0.0.1       │ RC 4212  │
 │        │   │             │                     │   ┌────┴─────────────────┴────────┐ │
 │        │   ├─ WsTransport┼─ LAN ws://pc:47555 ─┼──▶│ Companion (.NET 9, трей)       │ │
 │        │   │  (OkHttp)   │   NDJSON + token    │   │  Kestrel WS/HTTP :47555        │ │
 │        │   └─ BtTransport┼─ RFCOMM (UUID) ─────┼──▶│  RfcommServiceProvider         │ │
 │        │      (secure)   │   NDJSON + token    │   │  DnsServiceRegister (_vlcremote)│ │
 │ Discovery: NsdManager +  │◀─ mDNS / UDP beacon ┼───│  UDP beacon responder :47556   │ │
 │  UDP beacon + /24 scan   │                     │   │  VLC process mgr, vlcrc        │ │
 │  + ручной host / QR      │                     │   │  Win32: окна, SetDisplayConfig,│ │
 └──────────────────────────┘                     │   │   power; CoreAudio; IPolicyCfg │ │
          ▲                                       │   └────────────────────────────────┘ │
          └── Bluetooth PAN (без кода) → обычный IP‑путь, как LAN ─────────────────────────┘
```

Один `Session`‑класс на ПК обслуживает оба транспорта через абстракцию «строка туда/строка сюда».
На телефоне `CompanionBackend` выбирает транспорт: LAN найден → WS; нет сети/нет ПК в mDNS за 3 с
и есть сопряжённый ПК → RFCOMM; оба недоступны → подсказать PAN/хотспот.

### Выбранные библиотеки и версии

| Компонент | Выбор | Версия (на 2026‑10) |
|---|---|---|
| Трей, окна | WinForms (`net9.0-windows10.0.19041.0`, `UseWindowsForms`) | .NET 9 (SDK 9.0.310 установлен) |
| HTTP/WebSocket | Kestrel (`FrameworkReference Microsoft.AspNetCore.App`, `CreateSlimBuilder`) | в составе .NET 9 |
| Bluetooth RFCOMM | WinRT `Windows.Devices.Bluetooth.Rfcomm` (без NuGet) | Windows SDK 10.0.19041+ |
| mDNS/DNS‑SD | Win32 `dnsapi.dll` P/Invoke (`DnsServiceRegister`) | Windows 10+ |
| Аудио | NAudio (CoreAudio) + собственный `IPolicyConfig` interop | NAudio 2.x |
| Win32 | P/Invoke вручную или `Microsoft.Windows.CsWin32` source‑generator | CsWin32 0.3.x |
| Логи | Serilog + Serilog.Sinks.File | 4.x / 6.x |
| Установка/обновления | Velopack + `vpk` | 0.0.5xx (NuGet, версии синхронизировать) |
| JSON | `System.Text.Json` (source‑generated) | в составе .NET 9 |
| Android BT | `android.bluetooth` + `CompanionDeviceManager` | API 26+ / 33+ |
| Android WS | OkHttp `WebSocket` | 4.12 / 5.x |
| Android discovery | `NsdManager` (API 34+ callbacks) + UDP | — |

### Оценка сложности

| Кусок | Сложность | Ориентир |
|---|---|---|
| Трей + Kestrel + NDJSON‑сессия + токен/QR | S–M | 2–3 дня |
| RFCOMM‑сервер на Windows + тот же `Session` | S (проверено, что API работает) | 1 день + тесты с 2–3 телефонами |
| Android: WS‑транспорт + discovery (NSD + beacon + ручной ввод) | M | 2–3 дня (NSD капризен) |
| Android: BT‑транспорт + CDM + реконнект | M | 2–3 дня |
| VLC‑менеджер: процесс, аргументы, `vlcrc`, RC‑мультиплексор, diff‑push | M | 3 дня |
| Окна/мониторы/проецирование/сон/keep‑awake | S–M | 1–2 дня (+ тесты на реальном ТВ) |
| Аудио: `adev` + NAudio громкость | S | 0,5 дня; `IPolicyConfig` ещё +0,5 |
| Брандмауэр (детект + UAC‑помощник) | S | 0,5 дня |
| Velopack, релизы, автозапуск, логи | S | 1 день |
| HDMI‑CEC (опционально, с адаптером) | S | 0,5 дня |
| TLS с пиннингом (отложено) | M | 1–2 дня |

### Что проверить первым в прототипе (открытые вопросы)

1. Полный цикл Android → Windows RFCOMM: сопряжение по диалогу при `connect()`, уведомление «Add a
   device» на ПК, `BluetoothEncryptionWithAuthentication`.
2. Выживание RFCOMM‑listener после сна/пробуждения ноутбука.
3. `NsdManager` на целевом телефоне находит `_vlcremote._tcp`, объявленный Windows‑responder’ом;
   поведение поверх Bluetooth PAN и профиль брандмауэра PAN‑сети.
4. Класс окна VLC (Spy++) и порядок экранов Qt vs `Screen.AllScreens`.
5. `SetDisplayConfig(SDC_TOPOLOGY_EXTERNAL)` на этом ноутбуке; `SC_MONITORPOWER` под Modern Standby.
6. `IPolicyConfig` на текущей сборке Windows 11 (26200).
7. Правка `vlcrc`: VLC принимает минимальный файл; какие действия выставляют `config_dirty`.
