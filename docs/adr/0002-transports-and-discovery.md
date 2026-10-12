# ADR-0002. Транспорты и обнаружение

Статус: принято (2026-10-12). Основание: [research/transport-and-companion.md](../research/transport-and-companion.md), [research/prior-art.md](../research/prior-art.md).

## Решение

1. **LAN без компаньона** — HTTP к VLC (`/requests/*.json`, Basic с пустым логином). Обнаружение:
   сохранённый адрес + имя хоста, параллельный HTTP‑свип `/24` от своего IP наружу с таймаутом ~1 с
   (узнаём VLC по `401` с realm `VLC stream`), ручной ввод `host[:port]`, `имя-пк.local`.
   mDNS без компаньона бесполезен: VLC себя не анонсирует.
2. **LAN с компаньоном** — WebSocket `ws://pc:47555`, NDJSON, токен. Обнаружение: DNS‑SD
   `_vlcremote._tcp` (на Windows — встроенный responder через `DnsServiceRegister`; на Android —
   `NsdManager`) плюс UDP‑маяк на `:47556` как запасной путь, плюс QR с ТВ.
3. **Bluetooth** — Classic RFCOMM (SPP) к компаньону: Windows `RfcommServiceProvider` (работает из
   обычного .NET‑приложения без прав администратора — проверено), Android
   `createRfcommSocketToServiceRecord` + `BLUETOOTH_CONNECT`, выбор устройства через
   `CompanionDeviceManager`. Тот же NDJSON, тот же `Session` на ПК.
4. **Bluetooth PAN** (tethering телефона → Windows «Join PAN») — документированный запасной путь
   без компаньона: обычный IP‑маршрут, HTTP к VLC. В приложении — подсказка, не автоматизация.
5. **Выбор канала**: WS, если ПК найден в LAN за ~3 с; иначе RFCOMM, если есть сопряжённый ПК;
   иначе экран «Подключение». Переключение без потери состояния, команды буферизуются 5 с.

## Отвергнуто

- **BLE GATT‑периферия на Windows** — работает, но сообщения 20–512 байт, своя фрагментация,
  нет API отключения; оставлено как план Б.
- **Эмуляция клавиатуры/хоткеев как транспорт** — требует фокуса окна, не даёт состояния.
- **mDNS‑библиотеки для .NET** (Makaretu, Zeroconf, Tmds.MDns, Mono.Zeroconf) — мертвы или только browse.
- **TLS в v1** — домашняя сеть, токен достаточен; в QR зарезервировано поле `fp` для пиннинга.
