# Управление телевизором: питание, громкость, вход

Исследование: может ли компаньон (трей‑приложение .NET 9 на ноутбуке у ТВ) сам управлять телевизором — выключать
и включать его, менять громкость, по возможности переключать вход. Вопрос пользователя: «можно ли управлять ТВ
по кабелю — выключение, громкость».

Дата: 2026‑10‑13. Замеры — на ноутбуке у ТВ через `scripts\dev-laptop.ps1`, сетевые запросы — с ПК разработчика
(192.168.0.43). Все эксперименты **только на чтение**: значения VCP не записывались, WebSocket/пульт ТВ не
открывался, громкость и вход не менялись.

## Коротко

- **Телевизор:** Samsung **UE55H6200** (серия H, 2014 год, ОС Orsay, не Tizen), Wi‑Fi, сейчас **192.168.0.31**.
- **Кабель:** HDMI‑выход ноутбука идёт от **NVIDIA GeForce GTX 1050**, ТВ подключён ко входу **HDMI 2**
  (CEC‑адрес из EDID `2.0.0.0`). HDMI‑CEC у видеокарт ПК нет; **DDC/CI телевизор не поддерживает** (ошибка I²C
  на каждый запрос). По самому кабелю без дополнительного железа управлять ТВ нельзя.
- **Сеть:** ТВ отвечает по UPnP — **громкость и mute читаются без сопряжения** (`RenderingControl`), там же есть
  `SetVolume`/`SetMute`. Пульт по сети у H‑серии — зашифрованный протокол с PIN‑сопряжением (порты 8080/8000);
  выключение через него возможно, **включение по сети у этой модели невозможно**.
- **Рекомендация:** 1) громкость ТВ по UPnP (бесплатно, без настроек ТВ); 2) выключение — через зашифрованный
  протокол H/J либо через CEC‑адаптер; 3) включение и переключение входа — только **HDMI‑CEC** (адаптер
  Pulse‑Eight USB‑CEC ≈ $48 в разрыв кабеля или Raspberry Pi на свободном HDMI‑входе) или ИК‑бластер.

## 1. Что за железо (замеры)

Ноутбук MAIBENBEN ZiMai Z5 (`Win32_ComputerSystem`). Видеоадаптеры (`Win32_VideoController`):

| Адаптер | Драйвер | PNP |
|---|---|---|
| NVIDIA GeForce GTX 1050 | 30.0.15.1289 | `PCI\VEN_10DE&DEV_1C8D&SUBSYS_10441D05` |
| Intel UHD Graphics 630 | 31.0.101.1999 | `PCI\VEN_8086&DEV_3E9B&SUBSYS_10441D05` |

То есть дискретная видеокарта **есть**. `EnumDisplayDevices` в сеансе пользователя:

```
\\.\DISPLAY1 | Intel(R) UHD Graphics 630   -> MONITOR\NCP0028  (встроенная панель LM156LF9L01)
\\.\DISPLAY4 | NVIDIA GeForce GTX 1050     -> MONITOR\SAM0B60  (ТВ «SAMSUNG»)
```

HDMI‑порт разведён на NVIDIA, а не на Intel (поэтому вопрос про Intel «CEC over LSPCON» для этого ноутбука
неактуален). `WmiMonitorConnectionParams`: у ТВ `VideoOutputTechnology = 5` (HDMI), у панели `0x80000000`
(внутренний).

EDID ТВ (реестр `HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY\SAM0B60\…\Device Parameters\EDID`, 256 байт,
1 расширение CEA‑861): имя `SAMSUNG`, неделя/год изготовления панели 48/2013, в HDMI VSDB (OUI `00‑0C‑03`)
**физический адрес CEC `2.0.0.0`** — ноутбук воткнут во вход HDMI 2. Это понадобится для CEC (переключение входа
на ноутбук = `Active Source`/`Set Stream Path` с адресом `2.0.0.0`).

## 2. HDMI‑CEC

### 2.1. Есть ли CEC у видеокарт ПК

- **Нет, как правило.** Pulse‑Eight прямо пишет: «Almost all graphics cards do not support CEC in any form»
  ([pulse-eight.com](https://www.pulse-eight.com/p/104/usb-hdmi-cec-adapter)). Линия CEC (контакт 13 HDMI)
  у видеокарт для ПК обычно не подключена к контроллеру.
- **NVIDIA GeForce:** CEC не поддерживается ни на Windows, ни на Linux; многолетняя тема с просьбой
  на форуме NVIDIA без официального ответа, CEC есть только у Tegra/Jetson
  ([forums.developer.nvidia.com](https://forums.developer.nvidia.com/t/hdmi-cec-support/31445)). Наш HDMI —
  на GTX 1050, значит штатного CEC нет.
- **Intel:** в Linux есть «CEC‑Tunneling‑over‑AUX» (DisplayPort 1.3) для адаптеров DP→HDMI и LSPCON‑подобных
  преобразователей (`drm_dp_cec.c`, i915); при этом многие преобразователи (Parade PS176) CEC‑контакт не
  разводят, хорошо работают на MegaChips 2900 ([patch i915](https://patchwork.kernel.org/patch/10066347/),
  [drm_dp_cec.c](https://cos.googlesource.com/third_party/kernel/+/96c2b8f3d7e33875c0d0b3a01f65fafdbacffa8a/drivers/gpu/drm/drm_dp_cec.c)).
  В Windows аналога в драйвере Intel и публичного API Windows для CEC нет — «не проверено» в первоисточнике
  (документации Microsoft о CEC‑API найти не удалось). Intel NUC с CEC используют встроенную плату Pulse‑Eight,
  а не GPU ([libCEC README](https://github.com/Pulse-Eight/libcec)). Для нашего ноутбука вопрос снят: HDMI на NVIDIA.
- Встроенная поддержка CEC в Windows: **нет** (не нашли ни API, ни класса устройств; «не проверено» как
  доказательство отсутствия).

### 2.2. Внешние адаптеры

**Pulse‑Eight USB‑CEC Adapter** (P8‑USBCECv1), ≈ **$48**
([pulse-eight.com](https://www.pulse-eight.com/p/104/usb-hdmi-cec-adapter)):

- Включается **в разрыв HDMI** между ноутбуком и ТВ (HDMI‑вход, HDMI‑выход) + USB к ноутбуку. Пассивный — без
  повторителя: суммарная длина кабелей ≈ 2 м для UHD и ≈ 5 м для 1080p; до 4K60 4:2:0 8 бит. Для нашего ТВ
  1080p — подходит. Альтернатива при длинном кабеле: адаптер на **другой** HDMI‑вход ТВ, а ноутбук напрямую.
- Видится в Windows как USB CDC (COM‑порт), драйвер — в установщике libCEC.
- Библиотека **libCEC**: Windows/Linux/macOS, привязки .NET (`LibCecSharp`, чистый C# поверх C API, x64/x86/arm64,
  примеры для .NET 8 в [cec-dotnet](https://github.com/Pulse-Eight/cec-dotnet)), Python, Node, Rust
  ([github.com/Pulse-Eight/libcec](https://github.com/Pulse-Eight/libcec)).
- **Лицензия: GPL‑2.0‑or‑later или коммерческая** от Pulse‑Eight
  ([LICENSE.md](https://github.com/Pulse-Eight/libcec/blob/master/LICENSE.md)). Встраивание `LibCecSharp` в
  компаньон делает компаньон производной работой GPL. Безопаснее запускать **`cec-client.exe` отдельным процессом**
  (поставляется с libCEC) и писать ему команды в stdin: `on 0`, `standby 0`, `as` (сделать ноутбук активным
  источником), `volup`, `voldown`, `mute`, `tx …` (сырые кадры). Граница процесса — обычный способ не тянуть GPL
  в свою программу (юридически «не проверено», но общепринято).
- Также libCEC умеет: Raspberry Pi (встроенный CEC), Pulse‑Eight NUC‑адаптеры, некоторые Exynos/Amlogic.

**Raspberry Pi как CEC‑мост** (Pi Zero 2 W ≈ $15 + mini‑HDMI кабель + питание): Pi втыкается в **свободный**
HDMI‑вход ТВ (CEC — общая шина для всех входов), на нём `cec-client`/`cec-ctl` и маленький HTTP‑сервис, компаньон
шлёт ему команды по сети. Плюс — нет разрыва HDMI‑кабеля ноутбука и ограничения длины; минус — ещё одно устройство
и его питание (от USB ТВ оно пропадёт при выключении ТВ — нужен отдельный блок, иначе не включить ТВ).
Переключение ТВ на вход ноутбука с Pi — широковещательный `Set Stream Path` (`0x86`) с адресом `2.0.0.0`
(«не проверено» на этом ТВ).

### 2.3. Что даёт CEC на этом ТВ

У Samsung CEC называется **Anynet+ (HDMI‑CEC)**, включается в меню «Система» (для 2014 г. путь начинается с
`Menu > System > …`; [samsung.com](https://www.samsung.com/ae/support/tv-audio-video/how-to-use-anynet-on-your-samsung-tv/)).

| Функция | Сообщение CEC | Ожидание |
|---|---|---|
| Выключить ТВ | `<Standby>` (0x36) на адрес 0 | работает у Samsung, стандартная функция Anynet+ |
| Включить ТВ | `<Image View On>`/`<Text View On>` + `<Active Source>` | работает, если ТВ в дежурном режиме с питанием (не выдернут из розетки) |
| Переключить вход на ноутбук | `<Active Source>` [2.0.0.0] | работает с адаптером в разрыв; с Pi — `Set Stream Path` |
| Громкость/mute | `<User Control Pressed>` Volume Up/Down/Mute | **обычно только при наличии аудиосистемы (ресивер/саундбар, ARC)**; сам ТВ на громкость от источника, как правило, не реагирует — «не проверено» на UE55H6200 |

Риски CEC: Anynet+ иногда сам включает/выключает ТВ вместе с другими устройствами
([samsung.com](https://www.samsung.com/pk/support/tv-audio-video/how-to-troubleshoot-the-samsung-tv-that-keeps-turning-off-by-itself));
адаптер в разрыв — ещё один разъём, где может пропадать сигнал.

## 3. DDC/CI по HDMI

Эксперимент (только чтение) в интерактивном сеансе пользователя (одноразовая задача планировщика; задача и файлы
удалены): `EnumDisplayMonitors` → `GetPhysicalMonitorsFromHMONITOR` → `GetCapabilitiesStringLength` и
`GetVCPFeatureAndVCPFeatureReply` для 0x10, 0x62, 0x8D, 0xD6, 0x60
([API](https://learn.microsoft.com/en-us/windows/win32/api/lowlevelmonitorconfigurationapi/nf-lowlevelmonitorconfigurationapi-getvcpfeatureandvcpfeaturereply)).
Сырые результаты:

```
== HMONITOR \\.\DISPLAY1 (Intel, встроенная панель) rect=0,0-1536,864 primary=True
  physical: 'Generic PnP Monitor'
  GetCapabilitiesStringLength failed err=0xC0262589 after 126 ms
  VCP 0x10/0x62/0x8D/0xD6/0x60: failed err=0xC0262589 (~60 ms каждый)
== HMONITOR \\.\DISPLAY4 (NVIDIA, ТВ SAMSUNG) rect=0,-864-1280,-144 primary=False
  physical: 'Generic PnP Monitor'
  GetCapabilitiesStringLength failed err=0xC0262582 after 0 ms
  VCP 0x10/0x62/0x8D/0xD6/0x60: failed err=0xC0262582 (0–5 ms каждый)
```

- `0xC0262582` = `ERROR_GRAPHICS_I2C_ERROR_TRANSMITTING_DATA` («ошибка передачи данных устройству по шине I²C»):
  на адрес DDC/CI (0x37) ТВ не отвечает. NVIDIA DDC/CI по HDMI в принципе передаёт (мониторы через неё
  управляются), так что вывод — **у этого Samsung DDC/CI нет**. Ни громкости (0x62), ни питания (0xD6), ни входа
  (0x60). Телевизоры, как правило, DDC/CI не реализуют.
- `0xC0262589` = `ERROR_GRAPHICS_DDCCI_INVALID_MESSAGE_COMMAND` у встроенной eDP‑панели — обычное дело,
  к ТВ отношения не имеет.

Итог: **DDC/CI — тупик для этого ТВ.**

## 4. Управление ТВ по сети

### 4.1. Поиск ТВ в сети

- ARP на обоих ПК: MAC `50‑85‑69‑FC‑3A‑97` (OUI Samsung) встречается на 192.168.0.24–.31, сейчас отвечает
  на ping **192.168.0.31**. В ARP ноутбука на .28/.29 был ещё `50‑56‑BF‑C0‑50‑13` (тоже Samsung; входит в UDN
  служб ТВ — вероятно, MAC проводного порта ТВ, «не проверено»). В ответе `/api/v2/` поле `ip` = `192.168.0.24`
  при обращении на .31 — **ТВ часто меняет адрес по DHCP**. Нужна резервация DHCP на роутере для
  `50:85:69:FC:3A:97` или поиск ТВ при каждом запуске (ниже).
- SSDP: широковещательный `M‑SEARCH` из PowerShell ответов не получил (ответы идут с адреса ТВ на наш порт, а
  не на групповой адрес, — их режет брандмауэр Windows; правила не меняли). Через системную службу SSDP
  (`UPnP.UPnPDeviceFinder`, `upnp:rootdevice`) ТВ найден — 4 корневых устройства `[TV]Samsung LED55 / UE55H6200`:
  - `urn:samsung.com:device:MainTVServer2:1` → служба `MainTVAgent2` (`:7676/smp_2_`)
  - `urn:samsung.com:device:RemoteControlReceiver:1` → `MultiScreenService` (`:7676/smp_7_`), capability
    `samsung:multiscreen:1` на `:8001/ms/1.0/`, в `ProductCap` — `Y2014,ENC`
  - `urn:schemas-upnp-org:device:MediaRenderer:1` (DLNA DMR, `:7676/smp_15_`) → `RenderingControl`,
    `ConnectionManager`, `AVTransport`
  - `urn:dial-multiscreen-org:device:dialreceiver:1` (DIAL, `:7676/smp_25_`)
- mDNS: одноадресный запрос `_samsungmsf._tcp.local` на `192.168.0.31:5353` → ответ, экземпляр
  `068e7781-006e-1000-bbbf-5056bfc05013`.
- Открытые TCP‑порты ТВ: **7676** (UPnP), **8000** (socket.io), **8001** (MSF REST), **8080** (сопряжение).
  Закрыты: 8002, 9197, 55000 (старый пульт до 2014), 52235, 1900/tcp.

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

Полей **`PowerState`, `TokenAuthSupport`, `wifiMac`, `OS` нет** — они появились у Tizen‑моделей 2016+. Модель
`14_X14` = 2014, Orsay. `GET /api/v2/applications/111299001912` (YouTube) → `"running": false` (DIAL‑статус
приложений читается без сопряжения).

### 4.3. Громкость по UPnP/DLNA (`RenderingControl`) — работает без сопряжения

SCPD `:7676/smp_16_` содержит `GetVolume`, `SetVolume`, `GetMute`, `SetMute`, `GetBrightness`/`SetBrightness`
и др.; управление — `POST :7676/smp_17_`, `SOAPACTION: "urn:schemas-upnp-org:service:RenderingControl:1#GetVolume"`,
тело `<InstanceID>0</InstanceID><Channel>Master</Channel>`. Замер (только Get):

```
GetVolume -> 200 <CurrentVolume>0</CurrentVolume>
GetMute   -> 200 <CurrentMute>0</CurrentMute>
```

Ответ пришёл без какого‑либо запроса разрешения. Громкость 0 — либо так выставлено, либо ТВ в этот момент был
в дежурном режиме/на другом входе («не проверено»: в момент замера состояние ТВ не видели). Что `SetVolume`
у Samsung меняет **основную громкость ТВ**, а не только DLNA‑плеера, — известно по SamyGO для моделей с
B‑серии (форумы, не первоисточник: [forum.samygo.tv](https://forum.samygo.tv/viewtopic.php?p=11678),
[iobroker](https://forum.iobroker.net/post/77038) — там же пример ответа 500 на `SetVolume` у другой модели); **на UE55H6200 `SetVolume` не вызывали — «не проверено»**.
Протокол — стандартный UPnP AV ([RenderingControl:1](https://upnp.org/specs/av/UPnP-av-RenderingControl-v1-Service.pdf)),
реализуется в .NET на `HttpClient` за ~50 строк, библиотек не нужно.

`MainTVAgent2` (`:7676/smp_4_`) объявляет `GetSourceList`, `GetCurrentExternalSource`, **`SetMainTVSource`**
(переключение входа), `GetDTVInformation` и т. п., но `GetSourceList` и `GetCurrentExternalSource` вернули
UPnP‑ошибку `501 Action Failed`. Возможно, требуют сопряжения или работают только во включённом ТВ —
«не проверено». Переключение входа по сети этим путём пока не подтверждено.

### 4.4. Пульт по сети у серии H (2014): зашифрованный протокол

H‑ и J‑серии (2014–2015, `ENC` в `ProductCap`) используют **зашифрованное сопряжение с PIN**, а не старый порт
55000 и не WebSocket 2016+. Порядок (по [PySmartCrypto в ha‑samsungtv‑encrypted](https://github.com/sermayoral/ha-samsungtv-encrypted)):

1. `GET :8080/ws/apps/CloudPINPage` — состояние страницы PIN;
2. `POST :8080/ws/apps/CloudPINPage` (тело `pin4`) — **ТВ показывает PIN**;
3. `GET :8080/ws/pairing?step=0&app_id=…&device_id=…&type=1`, `POST …step=1` (hello), `POST …step=2` (ack) —
   обмен ключами, на выходе ключ AES и `session_id`;
4. `DELETE :8080/ws/apps/CloudPINPage/run` — закрыть PIN;
5. команды: `GET :8000/socket.io/1/` → `ws://…:8000/socket.io/1/websocket/<sid>`, `1::/com.samsung.companion`,
   далее зашифрованные AES кадры с кодами клавиш (`KEY_POWEROFF`/`KEY_POWER`, `KEY_VOLUP`, `KEY_VOLDOWN`,
   `KEY_MUTE`, `KEY_SOURCE`, `KEY_HDMI`…).

Модель **H6200 указана как работающая** в [ha‑samsungtv‑encrypted](https://github.com/sermayoral/ha-samsungtv-encrypted);
там же предупреждение: токен на части моделей **протухает** (неделя‑месяц) и сбрасывается при отключении питания
ТВ — нужно повторное сопряжение по PIN. **Включения нет**: «turn on» только внешним действием. Шифрование
частично реверсировано (статический ключ из приложения SmartView); есть только Python/C‑реализации
([SmartCrypto/samsung_network_control](https://github.com/quan3t/samsung_network_control),
[samsungtvws `encrypted`](https://github.com/xchwarze/samsung-tv-ws-api)) — перенос на C# реален, но трудоёмок
(«не проверено»: объём работ — оценка).

Замечено при исследовании: `GET :8080/ws/pairing?step=0…` (без шага 2, показывающего PIN) вернул
`{"auth_data":""}`, `GET :8000/socket.io/1` — идентификатор сессии; это проверка доступности, PIN‑страница
не запрашивалась. Если на ТВ всё же что‑то всплыло — это от этих запросов.

### 4.5. Для справки: ТВ Samsung 2016+ (Tizen) — к этому ТВ не относится

- WebSocket `ws://<tv>:8001/api/v2/channels/samsung.remote.control?name=<base64 имени>` или
  `wss://<tv>:8002/…&token=<токен>`; при первом подключении ТВ показывает «Разрешить?», токен приходит в событии
  `ms.channel.connect` (`data.token`) и хранится у клиента
  ([samsungtvws connection.py](https://github.com/xchwarze/samsung-tv-ws-api/blob/master/samsungtvws/connection.py)).
- Клавиша: `{"method":"ms.remote.control","params":{"Cmd":"Click","DataOfCmd":"KEY_VOLUP","Option":"false","TypeOfRemote":"SendRemoteKey"}}`
  ([remote.py](https://github.com/xchwarze/samsung-tv-ws-api/blob/master/samsungtvws/remote.py)).
- Чтобы не спрашивал каждый раз — «Диспетчер подключений устройств → Уведомление о доступе → Только первый раз»;
  WebSocket не работает между подсетями/VLAN ([Home Assistant](https://www.home-assistant.io/integrations/samsungtv)).
- Включение — Wake‑on‑LAN на MAC ТВ при включённой настройке «Включение с мобильного»/«Network Standby»
  (там же). У нашей H‑серии такой функции нет (HA‑encrypted: «нет нативного включения»).

### 4.6. SmartThings (облако)

REST API `api.smartthings.com` с OAuth или персональным токеном; у ТВ capabilities `switch` (on/off),
`audioVolume` (`setVolume`, `volumeUp/Down`), `audioMute`, `mediaInputSource` (`setInputSource`, список в
`supportedInputSources`) ([grenton](https://knowledgebase.grenton.com/en/sterowanie-telewizorem-poprzez-smarthings),
[community.smartthings.com](https://community.smartthings.com/t/setinputsource-syntax-for-samsungtv/219538)).
Но: (1) ТВ 2014 года в SmartThings, судя по всему, **не подключается** (все примеры — 2016+; «не проверено»);
(2) Samsung сворачивает бесплатный доступ к API с октября 2026 — нужен Personal Plan $4.99/мес
([home-assistant.io](https://www.home-assistant.io/integrations/smartthings)); (3) зависимость от интернета.
**Для этого ТВ — не вариант.**

### 4.7. ИК‑бластер

Кнопка питания у пульта Samsung Smart Control 2014 — **ИК** (остальные — Bluetooth)
([samsung.com](https://www.samsung.com/uk/support/tv-audio-video/how-to-pair-your-smart-control-remote-to-your-2014-model-tv/)).
Сетевой ИК‑бластер (Broadlink RM4 mini, ≈ $20–30, локальный протокол, [python-broadlink](https://github.com/mjg59/python-broadlink))
или USB‑ИК‑передатчик умеет всё, что пульт: питание (вкл/выкл), громкость, `SOURCE`/`HDMI`. Минусы: нужна
прямая видимость ИК‑приёмника, нет обратной связи (неизвестно, включён ли ТВ, — питание‑тумблер), коды надо
выучить. Цены и протокол Broadlink — «не проверено» на практике в этом проекте.

## 5. Средства Windows

- **Громкость HDMI‑устройства вывода** (уже есть: `sys systemVolume`, CoreAudio `IAudioEndpointVolume`) —
  это цифровое ослабление сигнала до ТВ, громкость самого ТВ не меняется. Практичная схема без всего
  остального: выставить громкость ТВ один раз на комфортный максимум и регулировать с телефона Windows‑громкостью.
  Минус — при низком уровне теряется разрядность (на практике незаметно), и ТВ‑громкость на другом входе
  остаётся своей.
- **Выключение экрана** (уже есть: `sys displayPower`, `SC_MONITORPOWER`
  ([WM_SYSCOMMAND](https://learn.microsoft.com/en-us/windows/win32/menurc/wm-syscommand))) — гаснут оба
  дисплея; ТВ показывает «Нет сигнала». Samsung H‑серии сам уйдёт в дежурный режим, если включено
  «Эко‑решение → Откл. при отсутствии сигнала» (15/30/60 мин)
  ([samsung.com, H‑серия](https://www.samsung.com/in/support/tv-audio-video/what-is-eco-solution-in-samsung-h-series-tv/)).
  Включить ТВ обратно так нельзя — появление сигнала на HDMI ТВ без CEC не будит («не проверено» на этом ТВ).
- **Только экран ноутбука** (`SetDisplayConfig(SDC_TOPOLOGY_INTERNAL | SDC_APPLY)`, как Win+P «Только экран
  компьютера»; [SetDisplayConfig](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setdisplayconfig))
  — ТВ теряет сигнал (дальше как выше), а встроенная панель остаётся. Возврат — `SDC_TOPOLOGY_EXTEND`/
  `SDC_TOPOLOGY_CLONE` или сохранённая конфигурация. Риск: Windows переносит окна, VLC окажется на панели
  ноутбука — компаньон и так возвращает окно на предпочтительный монитор при его появлении.
- Итог: Windows‑средства дают «мягкое выключение» (через таймер ТВ) и цифровую громкость, но **не включение** и
  не настоящую громкость ТВ.

## 6. Сводная таблица

| Способ | Канал | Выкл | Вкл | Громкость ТВ | Вход | Стоимость | Трудоёмкость | Разовая настройка |
|---|---|---|---|---|---|---|---|---|
| DDC/CI | HDMI | нет | нет | нет | нет | 0 | — | проверено: ТВ не отвечает |
| CEC через GPU | HDMI | нет | нет | нет | нет | 0 | — | у NVIDIA/Intel в Windows нет |
| Pulse‑Eight USB‑CEC + `cec-client` | HDMI (в разрыв) | да | да | обычно нет (только с ARC‑аудиосистемой) | да | ≈ $48 | средняя | Anynet+ вкл, адаптер, libCEC |
| Raspberry Pi + CEC | HDMI (свободный вход) + LAN | да | да | обычно нет | да (Set Stream Path) | ≈ $25–40 | средняя‑высокая | Pi, питание отдельно, Anynet+ |
| UPnP `RenderingControl` | LAN | нет | нет | **да** (Get проверен, Set — нет) | нет | 0 | низкая | резервация IP или поиск SSDP |
| UPnP `MainTVAgent2` | LAN | нет | нет | — | возможно (`SetMainTVSource`, сейчас 501) | 0 | низкая | не проверено |
| Зашифрованный пульт H/J (8080/8000) | LAN | да | нет | да (клавиши) | да (`KEY_SOURCE`/`KEY_HDMI`) | 0 | высокая (крипто, порт с Python) | PIN на ТВ, повтор при протухании токена |
| ИК‑бластер (Broadlink) | ИК + LAN | да* | да* | да | да | ≈ $20–30 | средняя | обучение кодов, прямая видимость |
| SmartThings | облако | — | — | — | — | $4.99/мес | — | ТВ 2014 не поддерживается (не проверено) |
| `SC_MONITORPOWER` / `SetDisplayConfig` | Windows | косвенно (таймер ТВ) | нет | нет | нет | 0 | уже есть / низкая | «Откл. при отсутствии сигнала» на ТВ |
| CoreAudio HDMI | Windows | — | — | цифровая (не ТВ) | — | 0 | уже есть | громкость ТВ на максимум |

\* ИК питание — переключатель (toggle), без обратной связи о состоянии.

## 7. Рекомендации

### Что делать в компаньоне (по порядку)

1. **`tvVolume` по UPnP (сразу, бесплатно).** Обнаружение ТВ: SSDP через системную службу или собственный
   `M‑SEARCH` + одноадресный опрос, mDNS `_samsungmsf._tcp`, кэш последнего IP + UDN (`uuid:0a21fe80-…` у DMR);
   читать description `/smp_15_` → `controlURL` `RenderingControl` (не хардкодить `smp_17_`). Команды:
   `GetVolume`/`SetVolume`/`GetMute`/`SetMute`. Перед реализацией — **один ручной эксперимент с согласия
   пользователя**: при включённом ТВ сравнить `GetVolume` с громкостью на экране и сделать `SetVolume` +1.
   Если громкость ТВ меняется — это главный выигрыш: настоящая громкость ТВ без проводов и сопряжения.
   Учесть: Windows‑брандмауэр режет ответы на широковещательный `M‑SEARCH` (компаньону нужно правило входящего
   UDP для своего exe или использование `UPnPDeviceFinder`/`Windows.Devices.Enumeration` с AEP‑протоколом SSDP).
2. **`tvPower off` «мягко»** — уже есть `displayPower off`; добавить вариант «только экран ноутбука»
   (`SetDisplayConfig`) и подсказку в UI: включить на ТВ «Откл. при отсутствии сигнала».
3. **`tvPower on/off` + `tvInput` через HDMI‑CEC** — если пользователь купит Pulse‑Eight USB‑CEC: компаньон
   запускает `cec-client.exe -t p -d 1` (процесс держится открытым), команды в stdin `on 0`, `standby 0`, `as`;
   состояние — `pow 0`. Не линковать `LibCecSharp` (GPL), если нет коммерческой лицензии. Это единственный
   надёжный путь **включить** ТВ и переключить его на ноутбук.
4. **Зашифрованный пульт H/J** — только если CEC‑адаптер не устраивает: даёт выключение и клавиши громкости/
   источника, но требует порта крипто‑кода с Python, PIN‑сопряжения и периодического повторного сопряжения.
   Низкий приоритет.
5. SmartThings и DDC/CI — не делать.

### Протокол (предложение, в `protocol/README.md` ещё не внесено)

- `caps`: `tvControl` (+ в `state.system.tv`: `{"available":bool,"backend":"upnp"|"cec"|…,"power":"on"|"standby"|"unknown","volume":int?,"mute":bool?}`).
- `sys` команды: `tvPower` `{"on": bool}`; `tvVolume` `{"value": 0..100}` | `{"delta": int}` | `{"mute": bool}`;
  `tvInput` `{"source": "companion"}` (сделать ноутбук активным источником) — по мере появления CEC.
- Ошибки: `tv_unavailable` (ТВ не найден/выключен), `tv_unsupported` (нет нужного канала), тексты через
  `ProtocolText`.
- Изменения — одновременно в `protocol/`, `android/core/protocol`, `companion/VlcRemote.Core` (правило проекта).

### Что сделать пользователю один раз

- Роутер: **резервация DHCP** для ТВ (`50:85:69:FC:3A:97` → например, 192.168.0.31) — ТВ сейчас «гуляет» по
  адресам .24–.31.
- ТВ: «Эко‑решение → Откл. при отсутствии сигнала» (если нужен сценарий «выключить экран → ТВ уснёт сам»).
- Для включения ТВ: купить **Pulse‑Eight USB‑CEC** (≈ $48) и включить **Anynet+ (HDMI‑CEC)** в меню ТВ; либо
  ИК‑бластер. Без этого включить UE55H6200 программно нельзя.
- Разрешить один эксперимент `SetVolume` на ТВ (см. п. 1).

### Риски

- ТВ 2014 года: прошивки не обновляются, UPnP‑поведение может отличаться от описанного в форумах;
  `MainTVAgent2` отвечает `501`.
- UPnP — без аутентификации: любой в локальной сети может менять громкость ТВ (и так было, компаньон
  ничего не ухудшает).
- CEC: Anynet+ может «подхватывать» ТВ вместе с другими устройствами; адаптер в разрыв ограничивает длину кабеля.
- GPL у libCEC — держать границу процесса.
- Зашифрованный протокол: токен протухает, повторное PIN‑сопряжение у ТВ.

## Источники

- Pulse‑Eight USB‑CEC: https://www.pulse-eight.com/p/104/usb-hdmi-cec-adapter
- libCEC: https://github.com/Pulse-Eight/libcec, лицензия https://github.com/Pulse-Eight/libcec/blob/master/LICENSE.md,
  .NET: https://github.com/Pulse-Eight/cec-dotnet
- NVIDIA и CEC: https://forums.developer.nvidia.com/t/hdmi-cec-support/31445
- Linux CEC‑over‑AUX: https://patchwork.kernel.org/patch/10066347/
- DDC/CI API: https://learn.microsoft.com/en-us/windows/win32/api/lowlevelmonitorconfigurationapi/nf-lowlevelmonitorconfigurationapi-getvcpfeatureandvcpfeaturereply
- SetDisplayConfig: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setdisplayconfig
- WM_SYSCOMMAND / SC_MONITORPOWER: https://learn.microsoft.com/en-us/windows/win32/menurc/wm-syscommand
- UPnP RenderingControl: https://upnp.org/specs/av/UPnP-av-RenderingControl-v1-Service.pdf
- Громкость Samsung по SOAP: https://forum.samygo.tv/viewtopic.php?p=11678, https://forum.iobroker.net/post/77038
- H/J зашифрованный протокол: https://github.com/sermayoral/ha-samsungtv-encrypted, https://github.com/quan3t/samsung_network_control
- samsungtvws (2016+ и `encrypted`): https://github.com/xchwarze/samsung-tv-ws-api
- Home Assistant Samsung TV: https://www.home-assistant.io/integrations/samsungtv; SmartThings: https://www.home-assistant.io/integrations/smartthings
- Anynet+: https://www.samsung.com/ae/support/tv-audio-video/how-to-use-anynet-on-your-samsung-tv/
- Эко‑решение H‑серии: https://www.samsung.com/in/support/tv-audio-video/what-is-eco-solution-in-samsung-h-series-tv/
- Smart Control 2014 (ИК питание): https://www.samsung.com/uk/support/tv-audio-video/how-to-pair-your-smart-control-remote-to-your-2014-model-tv/
- Broadlink: https://github.com/mjg59/python-broadlink
