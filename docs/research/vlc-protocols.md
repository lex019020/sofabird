# Протоколы управления VLC 3.0.23 (Windows 11) — справочник по первоисточникам

Дата: 2026-10-12. Цель: спроектировать мобильный пульт для VLC на ПК.
Целевая установка: VLC 3.0.23 Vetinari, `C:\Program Files\VideoLAN\VLC` (проверено:
`vlc.exe` FileVersion 3.0.23; локальный `lua\http\requests\README.txt` побайтно совпадает
с веткой `3.0.x`, отличаются только переводы строк).

Все ссылки вида `file:line` указывают на файлы ветки `3.0.x` репозитория
`videolan/vlc` (скачаны 2026-10-12 с `raw.githubusercontent.com`), если не сказано
`master` (= будущий VLC 4). Локальные копии: `scratchpad/vlc-src/{3.0.x,master}/...`.
Строки Lua-файлов проверялись по исходникам ветки; в установленной VLC те же модули
лежат скомпилированными (`lua\intf\http.luac`, `lua\intf\modules\httprequests.luac`,
`lua\modules\common.luac`), а страницы `lua\http\requests\*.json|xml` и `custom.lua` —
обычным текстом.

Пометка **«не проверено»** означает: утверждение выведено из кода, но не прогонялось на
живой VLC. Несколько фактов дополнительно проверены живым запуском VLC 3.0.23 на этом ПК
(`-I dummy --extraintf http --http-host 127.0.0.1`), они помечены **«проверено вживую»**.
Сосед по папке `live-probe.md` содержит результаты другого живого прогона; в одном
пункте (объединение пользовательской папки Lua с системной) он **ошибается**, см. §7.1.

---

## 0. Карта документа

1. Источники и метод
2. HTTP-интерфейс (Lua `http`): включение, сервер, авторизация, команды, `status.json`,
   дорожки, `playlist.json`, `browse.json`, `/art`, ошибки, ограничения
3. RC-интерфейс (`modules/control/oldrc.c`): включение на Windows, формат, команды, ловушки
4. Имена hotkey-действий и как их дергать через HTTP и RC
5. Отличия VLC 4 (ветка `master`) и статус релиза
6. Прочие пути управления на Windows (командная строка, one-instance, `vlc://`, медиаклавиши)
7. Расширение HTTP-интерфейса своими Lua-страницами (`%APPDATA%\vlc\lua\http`)
8. Выводы для дизайна пульта

---

## 1. Источники и метод

Ключевые файлы ветки `3.0.x`:

| Область | Файл |
|---|---|
| HTTP-интерфейс (скрипт) | `share/lua/intf/http.lua`, `share/lua/intf/modules/httprequests.lua`, `share/lua/modules/common.lua`, `share/lua/http/requests/*.json|xml`, `share/lua/http/requests/README.txt` |
| Lua-биндинги | `modules/lua/vlc.c`, `modules/lua/intf.c`, `modules/lua/libs/{httpd,variables,misc,input,playlist,volume,configuration,strings,equalizer}.c`, `share/lua/README.txt` |
| HTTP-сервер ядра | `src/network/httpd.c`, `include/vlc_httpd.h` |
| RC | `modules/control/oldrc.c` (в 3.0.x файл называется `oldrc.c`, модуль `oldrc`, шорткат `rc`), `modules/control/intromsg.h` |
| Hotkeys | `include/vlc_actions.h`, `src/misc/actions.c` (в 3.0.x вместо `src/config/keys.c`), `modules/control/hotkeys.c`, `bin/vlc.c` |
| Дорожки/ES | `src/input/es_out.c`, `src/input/var.c` |
| Звук | `src/audio_output/output.c`, `include/vlc_aout.h`, `modules/audio_output/{mmdevice,directsound}.c` |
| Windows-специфика | `src/win32/specific.c`, `modules/control/win_msg.c`, `modules/access/idummy.c`, `modules/gui/qt/main_interface_win32.cpp`, `modules/control/globalhotkeys/win32.c`, `src/win32/dirs.c` |
| Опции | `src/libvlc-module.c`, `modules/gui/qt/qt.cpp` |
| VLC 4 | `master`: `share/lua/intf/modules/httprequests.lua`, `share/lua/intf/cli.lua`, `modules/control/cli/{cli,player,playlist}.c`, `modules/lua/libs/input.c`, `share/lua/README.txt`, `NEWS` |

Пути, которые в 3.0.x дают 404 (упомянуты в задании): `modules/control/rc.c`
(→ `oldrc.c`), `src/config/keys.c` (→ `src/misc/actions.c`),
`share/lua/intf/modules/common.lua` (→ `share/lua/modules/common.lua`),
`share/lua/http/.hosts` (ACL-файла в 3.0.x нет). На `master` нет `modules/lua/libs/player.c`
(player-биндинги живут в `modules/lua/libs/input.c`) и нет `modules/control/oldrc.c`/`rc.c`
(→ `modules/control/cli/`).

---

## 2. HTTP-интерфейс

### 2.1 Включение и параметры

Модуль: Lua-интерфейс `http` (`modules/lua/vlc.c:96-104`, шорткаты `luahttp`, `http`).
Опции модуля (`vlc.c:98-100`): `http-password` (password), `http-src` (string, каталог
страниц), `http-index` (bool, строить листинги каталогов). Хост/порт — опции ядра:
`http-host` (string, по умолчанию NULL = все интерфейсы) и `http-port` (int, **8080**)
(`src/libvlc-module.c:1798-1799`). Есть `https-port` 8443 (`:1801`), но Lua-интерфейс 3.0.x
создает только обычный HTTP-хост (`http.lua:337` → `vlc.httpd()` →
`vlc_http_HostNew`, `modules/lua/libs/httpd.c:82-86`); HTTPS появился только в `master`
(`NEWS:271`, опция `--http-cert`).

Способы включить:

```
vlc.exe --extraintf http --http-host 0.0.0.0 --http-port 8080 --http-password secret
```

или в `%APPDATA%\vlc\vlcrc`:

```
extraintf=http
http-host=0.0.0.0
http-port=8080
http-password=secret
```

(«Extra interface modules» = `extraintf`; `src/libvlc.c:301-335` разбирает `extraintf`
и `control`, разделитель `:`, каждый модуль поднимается как `"<name>,none"`.)
В GUI: Инструменты → Настройки → Все → Интерфейс → Основные интерфейсы → «Веб»
и Lua → Lua HTTP → Пароль. Пароль обязателен (см. §2.3).

Как `--http-src` попадает в скрипт: `modules/lua/intf.c:67-87` формирует
`config={http={dir='<src>',no_index=<bool>}}`; `http.lua:281` берет `config.dir`, иначе
ищет каталог `http` через `vlc.config.datadir_list` (см. §7).

### 2.2 Транспортные свойства сервера (`src/network/httpd.c`)

| Свойство | Факт | Ссылка |
|---|---|---|
| Привязка | `net_ListenTCP(hostname, port)`, hostname из `http-host` (NULL → все адреса, IPv4+IPv6) | `httpd.c:958-960` |
| Версии HTTP | Разбираются HTTP/1.0 и 1.1 (`:1373-1375`, `HTTP/0.9` — `HTTPD_PROTO_HTTP0`) | `httpd.c:1301-1310, 1373-1381` |
| Статус-строка ответа | Для страниц (`httpd_FileNew`) `HTTP/1.1 200 OK` (`i_version=1`, `:313-314`); для «handler»-ответов (`/art`, пустой пароль) `HTTP/1.0 <код>` (`:466-469`) | `httpd.c:302-350, 448-470` |
| Заголовки страницы | `Content-type: <mime>`, `Cache-Control: no-cache`, `Content-Length` | `httpd.c:319-320, 347` |
| Keep-alive | Соединение остается открытым, только если запрос HTTP/1.1 **и** клиент не прислал заголовок `Connection` вообще. Любой присланный `Connection:` (в т.ч. `keep-alive`) → сервер отвечает `Connection: close` и закрывает. HTTP/1.0 → всегда закрывается | `httpd.c:344-345, 1918-1941`; **проверено вживую** (curl без заголовка: `Connection #0 left intact`; с `Connection: keep-alive`: ответ `Connection: close`) |
| Таймаут простоя | 10 с (`vlc_http_HostNew(..., 10)`), отключается только скрытой переменной `http-no-timeout` | `httpd.c:867-869, 954-956, 1738-1747` |
| Потоки | Один поток `httpd_HostThread` на хост; все колбэки (включая выполнение Lua-страниц) выполняются последовательно под `host->lock`. Параллельные запросы сериализуются | `httpd.c:978, 1984` |
| CORS | Заголовков `Access-Control-*` нет нигде (grep) → браузерный клиент с другого origin невозможен без прокси | — |
| gzip, chunked, range | Нет | — |
| Лимит запроса | Тело > буфера → `413 Request Entity Too Large`; разумный лимит URL ~ единицы КБ (**не проверено** точное число) | `httpd.c:1519` |
| Задержка | На localhost ~20 мс на `status.json` (замер соседнего `live-probe.md`); по Wi-Fi ожидаемо 30-100 мс. Push-уведомлений нет — только опрос. Штатный веб-UI опрашивает `status.xml` раз в **1000 мс** (`share/lua/http/js/controllers.js:87`) и раз в 500 мс после действий (`:128`) | — |

Вывод для клиента: использовать HTTP/1.1 **без** заголовка `Connection` (OkHttp так и
делает; `HttpURLConnection` шлет `Connection: keep-alive` — будет пересоединение на каждый
запрос), держать одно соединение, опрашивать `status.json` раз в 500-1000 мс, не слать
запросы параллельно (они все равно сериализуются).

### 2.3 Авторизация

- Схема: HTTP Basic. `httpdAuthOk(user, pass, "Authorization")` (`httpd.c:1659-1697`):
  если у URL пустые и user, и password — доступ свободный; иначе требуется `Basic base64(user:pass)`,
  сравнение `strcmp` и user, и pass. Lua регистрирует страницы с `user=nil` → `""`
  (`http.lua:224`, `httpd.c:1067-1068`), поэтому логин **пустой**: `Authorization: Basic base64(":secret")`.
- Неудача → `401` + `WWW-Authenticate: Basic realm="VLC stream"` (`httpd.c:1888-1891`);
  неизвестный URL → `404` (`:1892`).
- Пустой пароль: `http.lua:338-342` — страницы вообще не регистрируются, единственный
  handler `/` отвечает `Status: 403` (`callback_nopassword`, `:109-114`), остальные URL → 404.
  Дополнительно `modules/lua/libs/httpd.c:199,300,270-279` подменяют любой вывод страницы
  HTML-заглушкой «Password for Web interface has not been set», если пароль пустой.
  В журнале: «Password unset, insecure web interface disabled» (`http.lua:339`).
- ACL `.hosts` (был в VLC ≤ 2.0) в 3.0.x **отсутствует**: файл 404 в ветке, в `http.lua`
  нет обработки. Единственная защита — пароль и `http-host`.
- `--http-host` в `lua-config` игнорируется с ошибкой (`http.lua:329-333`); задавать только
  через `--http-host`/`--http-port`.

### 2.4 Общая модель запросов

Страницы (локальный каталог `lua\http\requests\`): `status.json`, `status.xml`,
`playlist.json`, `playlist.xml`, `playlist_jstree.xml` (внутренняя, README просит не
полагаться), `browse.json`, `browse.xml`, `vlm.xml`, `vlm_cmd.xml`; плюс `/art` (не в
`requests/`). Все они — Lua-шаблоны `<?vlc ... ?>` (`http.lua:36-37, 65-104`), перечитываются
при изменении mtime файла (`process()`, `:87-104`).

- MIME по расширению (`http.lua:40-51`): **`.json` → `text/plain`** (`:42`), `.xml` → `text/xml`.
  Клиент не должен требовать `application/json`. **Проверено вживую.**
- Разбор query (`parse_url_request`, `http.lua:248-269`): пары `k=v` по `&`, декодирование
  `vlc.strings.decode_uri` = только `%XX` (`src/text/url.c`, `vlc_uri_decode`). **`+` не
  превращается в пробел** — вопреки README (`requests/README.txt:12`); пробел кодировать
  как `%20`. Повторяющийся ключ становится массивом (используется для `option=`).
- **Каждая** страница `requests/*` сначала вызывает `httprequests.processcommands()`
  (`status.json:31`, `playlist.json:31`, `browse.json:32`, `status.xml:33`, `playlist.xml:84`,
  `browse.xml:32`) — то есть команду можно отправить с любой из них и сразу получить
  соответствующий ответ. Команда выполняется **до** формирования статуса, но многие
  действия асинхронны (плейлист/вход), поэтому ответ может еще не отражать эффект.
- Неизвестная `command` молча игнорируется; ответ — обычный статус с кодом 200.

### 2.5 Команды (`httprequests.lua:71-210`)

Параметры читаются из `_GET` (`:73-80`): `input`, `command`, `id` (число, по умолчанию -1),
`val`, `option` (может повторяться), `band`, `name`, `duration`.

| `command=` | Параметры | Что делает (код) | Примечания |
|---|---|---|---|
| `in_play` | `input=<uri или путь>`, `option=...`*, `name=`, `duration=` | `vlc.playlist.add({{path=make_uri(input), options, name, duration}})` (`:85-93`) — добавляет и запускает | `make_uri` пропускает строки с `://` как есть, путь Windows `C:\x.mkv` → `file:///C:/x.mkv` (`modules/lua/libs/strings.c:79-92`, `src/text/url.c` `vlc_path2uri`). Опции вида `noaudio`, `novideo`, любые `:opt=val` без двоеточия. `name` по наблюдению `live-probe.md` игнорируется |
| `in_enqueue` | те же | `vlc.playlist.enqueue` (`:96-97`) — добавить без запуска | |
| `addsubtitle` | `val=<путь>` | `vlc.input.add_subtitle(val)` (`:94-95`) — путь (не URI), `input_AddSlave(SLAVE_TYPE_SPU, ..., autoselect=false)` (`libs/input.c:233-260`) | Без текущего входа → Lua-ошибка → страница-ошибка (§2.13) |
| `pl_play` | `id=` | `id==-1` → `playlist.play()`, иначе `gotoitem(id)` (`:98-103`) | `id` = id элемента плейлиста из `playlist.json` |
| `pl_pause` | `id=` | если `status()=="stopped"` → play/gotoitem, иначе `playlist.pause()` = **toggle** (`:104-113`, `playlist_TogglePause`) | |
| `pl_forcepause` | — | pause только если `playing` (`:114-117`) — идемпотентно | |
| `pl_forceresume` | — | toggle только если `paused` (`:118-121`) — идемпотентно | |
| `pl_stop` | — | `playlist.stop()` | |
| `pl_next` / `pl_previous` | — | `playlist.next()/prev()` | |
| `pl_delete` | `id=` | `playlist.delete(id)` (`:128-129`) | README называет «UNSUPPORTED», но код есть; работает для leaf-id |
| `pl_empty` | — | `playlist.clear()` | |
| `pl_sort` | `val=<ключ>`, `id=0|1` | `playlist.sort(val, id>0)` (`:132-133`) | Ключи по `share/lua/README.txt:325-328`: `id, title, title nodes first, artist, genre, random, duration, title numeric, album` (README страницы со списком «0 Id, 1 Name…» устарел) |
| `pl_random` | — | toggle random | |
| `pl_loop` | — | toggle loop; если loop включился — выключает repeat (`:136-140`) | |
| `pl_repeat` | — | toggle repeat; если включился — выключает loop (`:141-145`) | |
| `pl_sd` | `val=<имя SD>` | toggle service discovery (`:146-151`) | Удалено в `master` |
| `fullscreen` | — | toggle, только если есть vout (`:152-155`) | |
| `snapshot` | — | `common.snapshot()` → `video-snapshot` на vout (`common.lua:36-40`) | Нет в README |
| `volume` | `val=` | `common.volume(val)` (`:158-159`) — см. §2.6 | |
| `seek` | `val=` | `common.seek(val)` — см. §2.6 | |
| `key` | `val=<имя действия>` | `common.hotkey("key-"..val)` (`:162-163`) — см. §2.6 и §4 | Нет в README |
| `audiodelay` | `val=<сек, float>` | `var.set(input,"audio-delay", val*1e6)` только при наличии входа (`:164-168`) | Отрицательные допустимы |
| `subdelay` | `val=<сек>` | `var.set(input,"spu-delay", val*1e6)` (`:174-178`) | |
| `rate` | `val=<float ≥ 0>` | `var.set(input,"rate", val)` (`:169-173`) | VLC квантует: `1.5` читается как `1.5015…` (`live-probe.md`) |
| `aspectratio` | `val=` | `var.set(vout,"aspect-ratio", val)` (`:179-182`) | Строки `16:9`, `4:3`, …; «default» = пустая строка; невалидное → сброс |
| `preamp` | `val=<dB>` | `vlc.equalizer.preampset` | |
| `equalizer` | `band=`, `val=<dB>` | `equalizerset(band, val)` | Полосы 0..9 |
| `enableeq` | `val=0|1` | `equalizer.enable(val ~= '0')` (`:189-190`) | |
| `setpreset` | `val=<id пресета>` | `equalizer.setpreset(val)` | id из `status.json` → `equalizer.presets` |
| `title` | `val=<индекс>` | `var.set(input,"title", val)` (`:193-194`) | **0-based индекс** (`src/input/var.c:331`) |
| `chapter` | `val=<индекс>` | `var.set(input,"chapter", val)` (`:195-196`) | 0-based (`var.c:395`) |
| `audio_track` | `val=<ES id>` | `var.set(input,"audio-es", val)` (`:197-198`) | §2.9 |
| `video_track` | `val=<ES id>` | `var.set(input,"video-es", val)` (`:199-200`) | §2.9 |
| `subtitle_track` | `val=<ES id>` | `var.set(input,"spu-es", val)` (`:201-202`) | `-1` = выключить |

Важно: `title/chapter/audio_track/video_track/subtitle_track/addsubtitle` **не проверяют**
наличие входа; при `stopped` `vlc.object.input()` = nil → `luaL_checkudata` падает →
HTML-страница ошибки вместо JSON (§2.13). `audiodelay/rate/subdelay/seek` защищены.

### 2.6 Грамматики `volume`, `seek`, `key`

**volume** (`common.lua:203-209` + `libs/volume.c:48-64`):
- `val=N` — абсолютно, целое в шкале **0..512, 256 = 100 %** (`AOUT_VOLUME_DEFAULT 256`,
  `AOUT_VOLUME_MAX 512`, `include/vlc_aout.h:294-295`); `<0` обрезается до 0 (`volume.c:52-53`).
- `val=+N` / `val=-N` (URL: `%2B10`) — относительно текущего.
- `val=N%` из README (`:126-129`) — **не реализовано**: `vlc.volume.set("50%")` →
  `luaL_checkint` не может разобрать строку → Lua-ошибка → страница ошибки, громкость не
  меняется (**проверено вживую** соседом: значение не изменилось). Не использовать; пересчитывать
  проценты в `round(p*2.56)` на клиенте.
- Шаг `volup/voldown` в RC и hotkey `vol-up/down`: `volume-step` = 12.8 (`libvlc-module.c:154,1499`)
  = 5 % (**не проверено** точное поведение `playlist_VolumeUp`).

**seek** (`common.lua:150-201`):
- `val=N` — абсолютно, секунды; `+N`/`-N` — относительно (`time ± N`).
- `val=N%`, `+N%`, `-N%` — доля длительности (`position`); десятичные — через точку (`us_tonumber`).
- Формат с буквами: `[+|-][<h>H:][<m>M:][<s>[S]]`, буквы `h/H`, `m/M/'`, `s/S/"`
  (`parsetime`, `:152-176`). Ловушка: паттерны ищутся по всей строке независимо, а секунды —
  только как хвост `(%d+)[sS"]?$`; строка `1:30` без букв даст **30 с**, а `90` даст 90 с.
  Безопасно слать только целые секунды.
- Без текущего входа — тихо ничего (`:181`).

**key** (`httprequests.lua:162-163`, `common.lua:25-33`, `libs/misc.c:149-156`,
`src/misc/actions.c:587-597`):

```lua
function common.hotkey(arg)
    local id = vlc.misc.action_id( arg )           -- vlc_actions_get_id(arg)
    if id ~= nil then
        vlc.var.set( vlc.object.libvlc(), "key-action", id )
```

`vlc_actions_get_id` **требует префикс `key-`** и ищет остаток в таблице
`s_names2actions` (`actions.c:264-379`, bsearch). HTTP сам приписывает `key-`, поэтому
`val` — голое имя: `val=nav-up`, `val=play-pause`, `val=subtitle-track`. Неизвестное имя →
`action_id` возвращает nil → `false`, HTTP-ответ все равно 200. Действие исполняет модуль
`hotkeys` (`modules/control/hotkeys.c`), который `vlc.exe` поднимает всегда
(`bin/vlc.c:240`, `libvlc_add_intf(vlc, "hotkeys,none")`). Список имен — §4.

### 2.7 `status.json` — поля (`httprequests.lua:458-568`, `getstatus(true)`)

`status.json` вызывает `getstatus(true)` (`status.json:33`) → включает `information` и
`stats`; `status.xml` вызывает `getstatus(false)` и дописывает `<information>` сам
(`status.xml:35-80`, формат с атрибутами `name`). Ниже — JSON-вариант.

| Ключ | Тип | Единицы / значения | Откуда | Поведение при `stopped` |
|---|---|---|---|---|
| `apiversion` | int | `3` | `:470` | всегда |
| `version` | string | напр. `"3.0.23 Vetinari"` (`VERSION_MESSAGE`) | `:471`, `libs/misc.c:94-98` | всегда |
| `volume` | int | 0..512, 256 = 100 % (`lroundf(playlist_VolumeGet*256)`). Если aout еще ни разу не создавался, `playlist_VolumeGet` возвращает `-1` (`src/playlist/aout.c`) → `volume = -256` (**не проверено**); после stop aout обычно живет и значение сохраняется (в живом образце `stopped` → `266`) | `:472`, `volume.c:58-64` | всегда |
| `seek_sec` | int | `short-jump-size` (по умолчанию 10) | `:473`, `libvlc-module.c:2671` | всегда |
| `time` | int | секунды, `floor(time_us/1e6)` | `:476` | `0` |
| `position` | float | 0..1 | `:477` | `0` |
| `currentplid` | int | id элемента плейлиста | `:478`, `libs/playlist.c:331-344` | `-1` |
| `audiodelay` | float | секунды (внутри µs/1e6) | `:479` | `0` |
| `rate` | float | 1.0 = нормальная | `:480` | `1` |
| `subtitledelay` | float | секунды | `:481` | `0` |
| `length` | int | секунды, `floor(item:duration())`; неизвестная длительность → `-1` (duration = -1 µs → `-0.000001` → floor) (**не проверено** граничный случай) | `:491-495`, `libs/input.c:347-352` | `0` |
| `fullscreen` | **bool или 0** | при наличии vout — `true/false` (переменная bool), без vout — число `0` | `:497-503` | `0` (number) |
| `aspectratio` | string | значение `aspect-ratio` vout; пустое → `"default"` | `:499-500` | **ключ отсутствует** |
| `audiofilters` | object | `{"filter_0": "<имя>", ...}` — `audio-filter` aout, разбитый по `:`; без фильтров `{"filter_0": ""}` | `:505-514` | отсутствует только если нет объекта aout; в живом образце `stopped` ключ есть (aout переживает stop) |
| `videoeffects` | object | `hue, brightness, contrast, saturation, gamma` — **из конфигурации**, не из живого фильтра | `:516-521` | всегда |
| `state` | string | `"playing"` / `"paused"` / `"stopped"` / `"unknown"` (`playlist_Status`) | `:523`, `libs/playlist.c:387-407` | `"stopped"` |
| `random`, `loop`, `repeat` | bool | переменные плейлиста | `:524-526` | всегда |
| `equalizer` | object или `[]` | `{preamp: float, bands: {...}, presets: [...]}`; если эквалайзер выключен — `equalizerget()` → пустая таблица, а dkjson печатает пустую таблицу как `[]` (**проверено вживую** соседом) | `:528-534`, `libs/equalizer.c:123-170` | как при игре |
| `equalizer.bands` | object | ключи вида `"band id=\"0\""` → gain dB (`equalizer.c:164-169`) — уродливый, но так | | |
| `equalizer.presets` | object/array | `{"preset id=\"N\"": "Flat", ...}` (`:280-289`) | | |
| `information.category.meta` | object | метаданные элемента: `filename, title, artist, genre, copyright, album, track_number, description, rating, date, setting, url, language, now_playing, publisher, encoded_by, artwork_url, track_id, track_total, director, season, episode, show_name, actors` + произвольные extra-теги контейнера (`libs/input.c:108-171`); пустые значения — ключ отсутствует | `:536-539` | **нет `information`** |
| `information.category["Stream N"]` | object | потоковые поля, см. §2.8 | `:541-550` | нет |
| `information.chapter`, `information.title` | int | текущие индексы (0-based) | `:560-561` | нет |
| `information.chapters`, `information.titles` | array<int> | **только индексы** (первое возвращаемое `get_list`; названия теряются) | `:563-564` | нет |
| `stats` | object | счетчики `input_item_t.p_stats`, ключи без подчеркиваний: `readpackets, readbytes, inputbitrate, averageinputbitrate, demuxreadpackets, demuxreadbytes, demuxbitrate, averagedemuxbitrate, demuxcorrupted, demuxdiscontinuity, decodedaudio, decodedvideo, displayedpictures, lostpictures, sentpackets, sentbytes, sendbitrate, playedabuffers, lostabuffers` | `:552-558`, `libs/input.c:190-231` | нет |

Чего в `status.json` **нет**: mute (переменная `mute` aout не экспортируется), текущие
выбранные дорожки (`audio-es/spu-es/video-es`), названия глав/титров, список
аудиоустройств, `can-seek/can-pause`, имя/URI текущего элемента напрямую (есть
`meta.filename`/`meta.title` и `currentplid` → искать в `playlist.json`).

Формат чисел: dkjson (`share/lua/modules/dkjson.lua`); float печатаются как есть
(`0.41573333740234`), булевы — `true/false`.

### 2.8 `information.category`: `meta` и `Stream N`

Категории берутся из `input_item_t.pp_categories` (`libs/input.c:73-97`). Ключи внутри
категорий в JSON получают `_` вместо пробелов (`httprequests.lua:545`:
`string.gsub(k2," ","_")`); имена категорий остаются со пробелами (`"Stream 0"`).
**Все имена категорий и ключей — gettext-строки** (`es_out.c` использует `_("...")`),
т.е. при русском языке интерфейса VLC они будут «Поток 0», «Кодек», «Язык», «Тип».
Пульт должен либо допускать локализованные ключи, либо рекомендовать `--language=en`
(**не проверено** на русской локали).

Ключи `Stream N` (английская локаль; `src/input/es_out.c`, `EsOutUpdateInfo`):

| Ключ (JSON) | Когда | Источник |
|---|---|---|
| `Original_ID` | только если `i_meta_id != i_id` (см. §2.9) | `es_out.c:3148-3150` |
| `Codec` | всегда; `"<fourcc> (<описание>)"` / описание | `:3152-3157` |
| `Language` | если известен | `:3159-3161` |
| `Description` | если есть (имя дорожки из контейнера) | `:3162-3165` |
| `Bitrate` | если > 0, `"N kb/s"` | `:3167-3169` |
| `Type` | `Audio` / `Video` / `Subtitle` | `:3174`, `:3246`, `:3436` |
| аудио: `Channels` (из формата ES), `Sample_rate` (`"48000 Hz"`), `Bits_per_sample`, `Decoded_format`, `Decoded_channels` (от декодера, напр. `3F2R/LFE`), replay-gain поля | | `:3176-3243`; `Decoded_channels` виден в живом образце `samples/status-playing.json` |
| видео: `Video_resolution` (`WxH`), `Buffer_dimensions`, `Frame_rate` (`"23.976024"`), `Decoded_format`, `Orientation`, `Color_primaries`, `Color_transfer_function`, `Color_space`, `Chroma_location`, `Projection`, `Yaw/Pitch/Roll`, `Field_of_view`, `Max_luminance`, `MaxCLL`, `MaxFALL` | | `:3245-3433` |

Категория называется `_("Stream %d")` от `es->i_meta_id` (`es_out.c:1206-1214`).

### 2.9 Дорожки: какие ID принимает `audio_track` и где взять список

1. **Что принимает `val`.** `vlc.var.set(input, "audio-es", val)` → `vlclua_var_set`
   (`libs/variables.c:185-198`) → `vlclua_tovalue` по типу переменной: `audio-es` —
   `VLC_VAR_INTEGER` (`src/input/var.c:192`), значит `luaL_checkinteger` — строка `"3"`
   приводится к 3, нечисловая строка → Lua-ошибка → страница ошибки.
2. **Что это за числа.** Это **ES id** = `es->i_id` (`es_out.c:1559-1565`):

   ```c
   if( es->fmt.i_id < 0 )  es->fmt.i_id = p_sys->i_id;   // демуксер не дал id → счетчик
   es->i_id = es->fmt.i_id;
   es->i_meta_id = p_sys->i_id++;                          // всегда инкремент, сквозной по всем типам
   ```

   Выборы переменной `audio-es` строятся в `EsOutESVarUpdateGeneric` (`:904-978`): первым
   добавляется `-1` с текстом `Disable` (`:936-937`), далее `(i_id, text)`, где text =
   `"<Description> - [<Language>]"` или `"Track N - [Language]"` / `"Track N"` (`:942-963`).
   Какой `fmt.i_id` дает демуксер, зависит от контейнера (MPEG-TS — PID, MP4 — track ID и
   т.п.; **не проверено** по демуксерам). Для MKV в живом прогоне id совпали со сквозным
   счетчиком (`Stream N`).
3. **Где взять список по HTTP.** В `status.json` списка `audio-es` **нет**. Но есть
   категории `Stream N`, где `N = i_meta_id`, и ключ `Original_ID`, который появляется
   **ровно тогда**, когда `i_id != i_meta_id` (`es_out.c:3148-3150`). Алгоритм клиента:

   ```
   for each category "Stream N" with Type in {Audio, Subtitle, Video}:
       es_id = category.Original_ID ?? N
       label = category.Description or ("Track …"), category.Language, category.Codec
   ```

   Это дает полный список дорожек с валидными ID и языками из чистого HTTP (для MKV
   подтверждено вживую соседним прогоном: `audio_track&val=2` выбрал `Stream 2`).
   Ограничения: имена категорий/ключей локализованы (см. §2.8); **текущая выбранная дорожка
   по HTTP недоступна** — ее знает только `vlc.var.get(input,"audio-es")` (своя Lua-страница,
   §7) или RC `atrack`.
4. `title`/`chapter`: значение — **индекс** 0-based (`var.c:331, 395`); тексты выбора —
   `"Title N [h:mm:ss]"` / имя, `"Chapter N"` / имя (`var.c:316-328, 340-352`), в `status.json`
   не попадают (только массив индексов).
5. Циклический выбор без ID: `key&val=audio-track`, `subtitle-track`, `subtitle-revtrack`,
   `subtitle-toggle` (`hotkeys.c:721-801`).

### 2.10 `playlist.json` (`httprequests.lua:301-377`, `libs/playlist.c:221-270`)

Корень — узел. Структура:

```json
{ "type":"node", "id":"1", "name":"Undefined", "ro":"rw", "children":[
   { "type":"node", "id":"2", "name":"Playlist", "ro":"ro", "children":[
       { "type":"leaf", "id":"4", "uri":"file:///D:/x.mkv", "name":"x.mkv",
         "ro":"rw", "duration":3600, "current":"current" } ] },
   { "type":"node", "id":"3", "name":"Media Library", "ro":"ro", "children":[] } ] }
```

- `id` — **строка** (`tostring(item.id)`, `:330, 359`), id элемента плейлиста
  (`playlist_item_t.i_id`); это то, что нужно `pl_play&id=`, `pl_delete`, `/art?item=`.
- `name` — `input_item_GetTitleFbName` (title из метаданных, иначе имя файла) (`playlist.c:244-247`).
- `uri` — `psz_uri` элемента (`:248-249`). `duration` — секунды `floor`, `-1` если неизвестно.
- `ro` — `"ro"`/`"rw"` по флагу `PLAYLIST_RO_FLAG`; `disabled`-элементы пропускаются (`:323`).
- `current: "current"` только у текущего leaf (`:349-356`); иначе ключа нет.
- `?search=<строка>` → `vlc.playlist.search` (`:304-311`), фильтр по имени; пустая строка
  сбрасывает.
- Конкретные id узлов (1/2/3) — типичные, **не проверено**; ориентироваться на `name`.

### 2.11 `browse.json` (`httprequests.lua:379-455`)

- Параметры: `uri=file:///D:/dir/` (предпочтительно; `file://~` = домашний каталог) или
  устаревший `dir=D:\dir`. `uri` нормализуется (`common.realpath`), добавляется `/`.
  `dir=` с `X:\..` → пустая строка → список дисков (`:399-409`); с `uri=file:///` по
  наблюдению соседа тоже возвращается список дисков `C:\`, `D:\`.
- Результат: `{"element":[ {...}, ... ]}`; поля элемента = поля `vlc.net.stat` (`share/lua/README.txt:222-230`):
  `type` (`"dir"`, `"file"`, `"unknown"` если stat не удался), `mode`, `uid`, `gid`, `size`,
  `access_time`, `modification_time`, `creation_time` (unix-секунды) + `path` (локальный путь),
  `name`, `uri` (`file:///...`).
- Скрытые (`.`-файлы) пропускаются; `..` включается (`:428-429`), для пустого каталога
  добавляется искусственно (`:424-426`). Сортировка по имени (`table.sort`).
- Только локальная ФС (`vlc.net.opendir`); сетевые/SMB-каталоги — только если смонтированы
  как путь (UNC `\\host\share` → `make_uri` дает `file://host/share/...`, `url.c:41-46`;
  **не проверено**).

### 2.12 `/art` (`http.lua:155-196, 344`)

- `GET /art` — обложка текущего элемента; `GET /art?item=<plid>` — элемента плейлиста
  (`vlc.playlist.get(num).item`). Требует тот же Basic-пароль (`:344`).
- Берет `metas["artwork_url"]` (обычно `file:///C:/Users/<u>/AppData/Roaming/vlc/art/...`),
  отдает тело с `Content-Type` по расширению (`mimes[ext]`, иначе `application/octet-stream`)
  и `Content-Length`; это handler, статус-строка `HTTP/1.0 200`.
- Нет обложки/ошибка → `Status: 404`, тело `Error` (`:187-194`).

### 2.13 Ошибки и граничные случаи

- Ошибка Lua при генерации страницы (nil-вход, нечисловой `val`, `val=50%`) → `pcall` ловит,
  `callback_error` (`http.lua:116-131, 216-221`) возвращает **HTML** «Error loading …» с
  текстом ошибки, статус **200**, `Content-type` той же страницы (`text/plain` для `.json`).
  Клиент обязан валидировать JSON и трактовать «не JSON» как ошибку команды.
  Файловые колбэки не могут задать статус — только handler'ы (`httpd.c:453-456`).
- Неизвестная команда, неизвестное имя hotkey — тихо, 200.
- При `stopped`: `vlc.object.input()` = nil, `vlc.input.item()` = nil → см. таблицу §2.7;
  `vlc.object.vout()`/`aout()` могут быть nil → `fullscreen=0`, нет `aspectratio`/`audiofilters`.
- `pl_pause` при `stopped` запускает последний активный элемент (`playlist_Play`).
- Запуск: сразу после старта VLC интерфейс может ответить пустым телом (наблюдение
  `live-probe.md`) — нужен ретрай.
- `in_play&input=vlc://quit` закроет VLC (`modules/access/idummy.c:158-166`) — любой,
  кто знает пароль, может это сделать; см. §6.3.

### 2.14 Ограничения HTTP, которые нельзя обойти без расширений

Нет push, нет текущих дорожек, нет mute, нет списка/переключения аудиоустройств, нет
названий глав, нет CORS/HTTPS (3.0.x), один поток, keep-alive с нюансом `Connection`,
локализованные ключи `information`.

---

## 3. RC-интерфейс (`modules/control/oldrc.c`)

### 3.1 Включение на Windows

- Модуль `oldrc` («Remote control interface», `oldrc.c:186-209`), capability `interface` 20,
  **на Windows имеет шорткат `rc`** (`:206-208`). Lua-реализация `cli.lua` на Windows шорткат
  `rc` **не** регистрирует (`modules/lua/vlc.c:113-117`: `#ifndef _WIN32 … "cli","rc"`),
  поэтому `--extraintf rc` на Windows = `oldrc.c`. (Lua-вариант доступен как
  `--extraintf luacli --lua-config "cli={host='…'}"` — поведение другое, здесь не описывается.)
- Опции (`:191-201`): `rc-show-pos` (печатать `pos: N%`), `rc-quiet` (**только Windows**:
  не открывать консоль), `rc-host` (TCP), на не-Windows еще `rc-unix`, `rc-fake-tty`.
- Пример: `vlc.exe --extraintf rc --rc-host 0.0.0.0:4212 --rc-quiet` или в `vlcrc`:
  `extraintf=rc`, `rc-host=0.0.0.0:4212`, `rc-quiet=1`. Хост парсится `vlc_UrlParse`
  (`:303-319`); форма без схемы допустима (`"//"` подставляется). Комбинация с HTTP:
  `--extraintf http:rc`.
- **Консольное окно**: `Activate` (`:361-365`) вызывает `intf_consoleIntroMsg` при
  `rc-quiet=false` **независимо** от `rc-host`; `intromsg.h:22-29` делает `AllocConsole()` и
  перенаправляет stdout/stderr/stdin. Без `--rc-quiet` у GUI-VLC появится черное окно консоли,
  и RC будет читать команды из нее (`ReadCommand`, `:1864-1874`). С `--rc-quiet` и без
  подключенного клиента поток просто спит (`:1870-1874`).
- **Одно соединение за раз**: `i_socket` один; `net_Accept` вызывается только когда
  `i_socket == -1` (`:519-524`); второй клиент висит в backlog до `logout`/разрыва первого.
- **Аутентификации нет вообще.** Привязка к `0.0.0.0` открывает полное управление
  (включая `quit`, `add <любой URL>`) всей сети. Для компаньона — только `127.0.0.1`.
- Строка команды ≤ 1024 байт (`MAX_LINE_LENGTH`, `:63`), терминатор `\r` или `\n` (`:1899-1900`).
- Кодировка: UTF-8 (`net_vaPrintf`/`utf8_vfprintf`, `:148-151`).

### 3.2 Формат вывода и асинхронные строки

- Каждая строка ответа заканчивается `\r\n` (`msg_rc`, `:140-153`).
- Для «зарегистрированных» команд (список в `RegisterCallbacks`, `:412-474`) после выполнения
  печатается `"<cmd>: returned <N> (<текст ошибки>)"` (`:621-632`), напр. `play: returned 0 (no error)`;
  для команд, обрабатываемых прямо в `Run` (`info`, `get_time`, `key`, …) — нет.
- Асинхронные события печатаются из колбэков других потоков с префиксом `status change: `
  (`STATUS_CHANGE`, `:64`), могут вклиниться в любой момент (под `status_lock`, но между
  строками ответа):
  - `( new input: <uri> )` (`:537`, `:1364`)
  - `( play state: 3 ): Play` / `( pause state: 4 ): Pause` / `( stop state: 0 )` и
    `( <cmd> state: <N> ): <Name>` из `StateChanged` (`:863-896`; имена `Initializing, Opening,
    Play, Pause, End, Error`, `:67-74`); в `Run` дополнительно `( play state: 3 )`,
    `( pause state: 4 )`, `( stop state: 5 )` без суффикса (`:565-581`), `( stop state: 0 )`
    при конце/ошибке входа (`:544-555`)
  - `( audio volume: <0..512> )` (`:849-861`)
  - `( new rate: 1.500 )` (`:897-904`)
  - `( time: <s>s )` после буферизации (`:905-920`)
  - `( quit )` при остановке интерфейса (`:778`)
  - `pos: N%` при `rc-show-pos` (`:585-593`)
- Блочные ответы: `+----[ <Имя> ]`, строки `| <значение> - <текст>` (у текущего суффикс ` *`),
  `+----[ end of <Имя> ]` (`:1138-1151`, `:1646-1657`). Имя блока для дорожек — локализованный
  текст переменной (`VLC_VAR_GETTEXT`, напр. `Audio Track`), для `adev` — `adev`.
- Парсить можно регулярками, но протокол не машинный: нет разделителя «конец ответа»,
  порядок строк событий и ответов не гарантирован. Практика: читать построчно, строки
  `status change:` обрабатывать как события, остальное относить к последней команде по
  эвристике (блок `+----[` … `end of`).

### 3.3 Команды (`:412-474`, `:621-771`, `:954-1776`)

| Команда | Семантика | Ссылка |
|---|---|---|
| `add <MRL> [ :opt ...]` | `playlist_AddInput(..., play=true)`; MRL + опции через ` :name=value` (`parse_MRL`) | `:1307-1322`, `:1922+` |
| `enqueue <MRL>` | то же, без запуска | `:1323-1338` |
| `playlist` | дерево плейлиста: `|  - <name> (<h:mm:ss>)` с отступами | `:1339-1344`, `:1160-1176` |
| `play`, `stop`, `next`, `prev` | очевидно; `clear` = stop + очистка | `:1200-1212`, `:1298-1306` |
| `goto N` | **1-based** индекс в плоском списке `items`; `N<=0` → ошибка; `> size` → «Playlist has only N elements» | `:1276-1297` |
| `repeat|loop|random [on|off]` | без аргумента — toggle; печатает `Setting repeat to true` | `:1213-1275` |
| `sort` | только по исполнителю | `:1346-1352` |
| `status` | переигрывает `status change` строки: new input, audio volume, state (`stop 5/play 3/pause 4`) | `:1353-1393` |
| `pause` | toggle pause (`playlist_TogglePause`) | `:974-978` |
| `seek X` | `X` секунд (abs) или `X%`; **относительных форм нет** | `:980-993` |
| `fastforward` / `rewind` | удвоение rate (если `can-rate`/`can-rewind`), иначе jump ±extrashort | `:994-1021` |
| `faster` / `slower` / `normal` | `rate-faster`/`rate-slower` плейлиста, `rate=1` | `:1022-1036` |
| `frame` | следующий кадр | `:1037-1041` |
| `title [X]`, `title_n`, `title_p` | get: `Currently playing title N/M.`; set: индекс | `:1068-1092` |
| `chapter [X]`, `chapter_n`, `chapter_p` | аналогично | `:1042-1067` |
| `atrack [X]`, `vtrack [X]`, `strack [X]` | **без аргумента — список** `| <ES id> - <текст> [*]` включая `-1 - Disable`; с аргументом — `var_SetInteger(input, "audio-es", atoi(X))` (тот же ES id, что в HTTP) | `:1093-1154` |
| `vratio [X]`, `vcrop [X]`, `vzoom [X]` | get → список выборов `aspect-ratio`/`crop`/`zoom` vout; set строкой (zoom — float) | `:1503-1625` |
| `snapshot` | `video-snapshot` на vout | `:1534-1537`, `:1555-1558` |
| `volume [X]` | get → `status change: ( audio volume: N )`; set abs 0..512, `0` дополнительно включает mute (`playlist_MuteSet(volume==0)`) | `:1425-1467` |
| `volup [N]`, `voldown [N]` | ±N шагов (`volume-step`) | `:1469-1500` |
| `adev [id]` | **без аргумента — список аудиоустройств** `| <id> - <name> [*]` через `aout_DevicesList` + `aout_DeviceGet`; с аргументом `aout_DeviceSet(id)` — реальное переключение. Для `mmdevice` id = endpoint-строка `{0.0.0.00000000}.{GUID}` (наблюдение `live-probe.md`), пустой id = по умолчанию. Требует живого aout (`playlist_GetAout`) — после `stop` может быть «нет объекта» | `:1627-1668`, `src/audio_output/output.c` |
| `achan [N]` | список/установка `stereo-mode` (Mono/Stereo/Original/Headphones…) | `:1670-1712` |
| `info` | категории потока как `+----[ <cat> ]` / `| Key: value` (те же данные, что `information`) | `:659-687` |
| `stats` | текстовая статистика | `:1714-1776` |
| `get_time`, `get_length` | целые секунды; `0` без входа | `:699-712` |
| `is_playing` | `1`/`0` (есть ли вход, а не играет ли) | `:688-698` |
| `get_title` | имя элемента (`psz_name`) | `:713-723` |
| `key <name>` / `hotkey <name>` | `var_SetInteger(libvlc, "key-action", vlc_actions_get_id(arg))` — **аргумент должен быть с префиксом `key-`** (`key key-nav-up`); без префикса → `ACTIONID_NONE` = 0, молча ничего | `:729-733`, `actions.c:587-597` |
| `f [on|off]` / `fullscreen` | toggle/set `fullscreen` плейлиста и vout. **Любая команда, начинающаяся на `f`/`F`**, попадает сюда (`switch(psz_cmd[0])`) | `:736-758` |
| `intf <module>` | поднять еще один интерфейс | `:1416-1423` |
| `logout` | закрыть TCP-сессию | `:650-658` |
| `quit` | `libvlc_Quit` — закрыть VLC | `:1406-1414` |
| `help`, `longhelp`, `h…`, `H…`, `?` | справка | `:724-728`, `:785-844` |
| `shutdown`, `rate X`, `vdeinterlace` | **отсутствуют в 3.0.x**: `shutdown`/любая команда на `s` молча игнорируется (`:760-763`), `rate` → `Unknown command` | `:769` |
| глобальные команды libvlc | если имя совпадает с переменной-командой libvlc (`var_Type(libvlc, cmd) & ISCOMMAND`), вызывается она | `:633-649` |

### 3.4 Ловушки RC

- **В паузе почти все отвергается**: `Input()` (`:966-971`) при `state == PAUSE_S` на любую
  команду, кроме `pause`/`frame`, печатает «Press pause to continue.» и ничего не делает —
  `seek`, `atrack`, `title`, `faster` в паузе не работают. То же для `Playlist()` (`next`,
  `prev`, `play`, `goto`, `add`…) — «Type 'pause' to continue.» (`:1192-1197`), и для
  `volume`/`volup`/`voldown` (`:1439-1445`, `:1483-1489`). Исключения: `adev`, `achan`,
  `vratio/vcrop/vzoom/snapshot`, `key`, `info`, `get_*`. Для пульта: перед `seek` снять
  паузу или использовать HTTP.
- `key` без префикса `key-` — тихий no-op; `help` показывает `key [hotkey name]` без
  уточнения.
- `volume` требует наличия входа (`VLC_ENOOBJ`, `:1434-1435`).
- `goto` — индекс, а не id; `playlist` не печатает ни индексы, ни id.
- Один клиент; события приходят только подключенному.

### 3.5 RC против HTTP

| Возможность | HTTP | RC |
|---|---|---|
| Авторизация | Basic | нет |
| Несколько клиентов | да (последовательно) | один |
| Push-события (state, volume, new input, rate) | нет | да (`status change:`) |
| Список дорожек с ID | косвенно (`Stream N` + `Original_ID`) | прямо, с `*` текущей |
| Текущая дорожка | нет | да |
| Аудиоустройства: список/переключение | нет | да (`adev`) |
| Стерео-режим | нет | да (`achan`) |
| Относительный seek, `%`-seek, h/m/s | да | только abs секунды и `%` |
| Громкость | abs/относительно | abs, шаги; mute при 0 |
| Работа в паузе | да | большинство команд — нет |
| Плейлист как данные | JSON с id | текст без id |
| Обзор файлов | `browse.json` | нет |
| Обложка | `/art` | нет |
| Эквалайзер, задержки, rate=X | да | задержек/`rate X` нет (`faster/slower`) |
| Закрыть VLC | через `key&val=quit` (сосед наблюдал, что не сработало) или `in_play&input=vlc://quit` | `quit` |
| Hotkey по имени | `key&val=<name>` | `key key-<name>` |
| Машиночитаемость | JSON | текст, регулярки |

---

## 4. Имена hotkey-действий (`src/misc/actions.c:264-379`, `include/vlc_actions.h`)

Полная таблица 3.0.x (ASCII-отсортирована в коде). Полезные для пульта с семантикой из
`modules/control/hotkeys.c`:

| Имя (`val=` в HTTP; `key key-<имя>` в RC) | Действие |
|---|---|
| `play-pause` | toggle pause (`hotkeys.c:544-553`) |
| `play` | play; если rate≠1 — сначала вернуть rate=1 (`:555-565`) |
| `pause`, `stop`, `next`, `prev` | очевидно |
| `quit` | `libvlc_Quit` (`:357`) |
| `toggle-fullscreen`, `leave-fullscreen` | `:1001-1018` |
| `vol-up`, `vol-down`, `vol-mute` | шаг `volume-step`; mute = toggle `playlist_MuteGet/Set` (`:487-510`) — **единственный путь к mute через HTTP** |
| `jump-extrashort`/`jump+extrashort`, `jump-short`/`jump+short`, `jump-medium`/`jump+medium`, `jump-long`/`jump+long` | назад/вперед на `extrashort/short/medium/long-jump-size` = 3/10/60/300 с по умолчанию (`:903-952`, `libvlc-module.c:2669-2675`); `-` назад, `+` вперед (в URL `+` кодировать как `%2B`) |
| `nav-up`, `nav-down`, `nav-left`, `nav-right`, `nav-activate` | DVD/BD меню (`:974-994`) |
| `disc-menu` | меню диска |
| `title-prev`, `title-next`, `chapter-prev`, `chapter-next` | `:954-968` |
| `audio-track` | следующая аудиодорожка по кругу, OSD «Audio track: …» (`:721-756`) |
| `subtitle-track`, `subtitle-revtrack`, `subtitle-toggle` | цикл субтитров вперед/назад; toggle запоминает последнюю (`spu-choice`) (`:758-850`) |
| `subdelay-up`/`subdelay-down`, `audiodelay-up`/`audiodelay-down` | ±50 мс (`:676-720`); требуют активной дорожки |
| `subsync-markaudio`, `subsync-marksub`, `subsync-apply`, `subsync-reset` | синхронизация субтитров по меткам |
| `subpos-up`, `subpos-down` | позиция субтитров (`:1305-1337`) |
| `subtitle-text-scale-normal/up/down` | масштаб текста субтитров 25..500 %, шаг 10/25 (`:1339-1357`) |
| `rate-normal`, `faster`, `slower`, `rate-faster-fine`, `rate-slower-fine` | скорость (`:422-440`) |
| `frame-next` | покадрово |
| `position` | показать OSD-позицию (`:1361-1364`) |
| `aspect-ratio`, `crop`, `zoom`, `unzoom`, `zoom-quarter/half/original/double`, `crop-top/…`, `uncrop-…` | видео (`:1019-1200`) |
| `deinterlace`, `deinterlace-mode` | |
| `toggle-autoscale`, `incr-scalefactor`, `decr-scalefactor` | масштаб видео в окне (`:1137-1176`) |
| `wallpaper` | режим «обои» (DirectX) |
| `snapshot`, `record` | |
| `random`, `loop` | toggle (`loop` циклит off→all→one, `:376-420`) |
| `clear-playlist` | |
| `audiodevice-cycle` | **есть**: перебирает `aout_DevicesList`, применяет `aout_DeviceSet`, OSD «Audio Device: …» (`:514-530`). Единственный способ сменить аудиоустройство через HTTP — но только по кругу, без выбора |
| `intf-show` (= `ACTIONID_INTF_TOGGLE_FSC`), `intf-boss`, `intf-popup-menu` | показать/скрыть FS-контроллер, «босс-клавиша» (скрыть VLC), контекстное меню |
| `program-sid-next/prev` | смена программы (TS) |
| `viewpoint-fov-in/out`, `viewpoint-roll-clock/anticlock` | 360° |
| `set-bookmark1..10`, `play-bookmark1..10` | закладки |

В `master` переименованы enum'ы (`ACTIONID_FASTER`→`RATE_FASTER`), но строковые имена
`faster`/`slower` сохранены; добавлены `frame-previous`, `projection-toggle`,
`subtitle-control-secondary` (`master src/misc/actions.c:304-372`).

---

## 5. VLC 4 (ветка `master`) — отличия и статус

**Статус релиза (октябрь 2026).** `master/NEWS:1`: «Changes between 3.0.x and 4.0.0-dev» —
релиза 4.0 нет. Актуальная стабильная версия для Windows — **3.0.23** (8 января 2026;
[videolan.org/news](https://www.videolan.org/news/2026-01-08-vlc-3-0-23/),
[linuxiac](https://linuxiac.com/vlc-3-0-23-media-player-released-with-windows-fixes-and-security-updates/)).
По сообщениям июля 2026 бета-сборки 4.0 были только для Apple; для Windows — nightly
([digitec](https://www.digitec.ch/en/page/open-source-tip-vlc-the-player-for-just-about-anything-43212)).
Старые обещания «4.0 в этом году» ([itsfoss 2021](https://itsfoss.com/news/vlc-4-features/),
[linuxiac](https://linuxiac.com/vlc-4-0-coming-this-year-with-completely-redesigned-ui/)) не сбылись.

**HTTP (Lua) в master** — модуль остался (`master modules/lua/vlc.c:673-681`, те же опции),
страницы те же, `apiversion` по-прежнему 3; отличия `httprequests.lua`:

| Что | 3.0.x | master |
|---|---|---|
| Ядро | `vlc.object.input()` + `vlc.var` | `vlc.player.*` (новый `vlc_player`), `share/lua/README.txt` «Player» |
| `audio_track/video_track/subtitle_track` | `var.set(input,"audio-es",id)` — выбор | `vlc.player.toggle_audio_track(id)` — **toggle** (`:181-186`): если дорожка выбрана — снимает, иначе выбирает (`modules/lua/libs/input.c:363-383`); `-1` снимает категорию. `id` — целое `vlc_es_id_GetInputId(es_id)` (`input.c:318`), не строка вида `"video/0"` (строковые id есть только в C API `vlc_es_id_GetStrId`) |
| `title/chapter` | индекс через var | `player.title_goto(i)`/`chapter_goto(i)` (`:177-180`) |
| `audiodelay/subdelay/rate` | var на входе | `player.set_audio_delay(s)` и т.д. (`:152-162`), без проверки входа |
| `seek` | var `time/position` | `player.seek_by_*` (`common.lua`), остальная грамматика та же |
| `pl_loop/pl_repeat` | взаимоисключающие | просто toggle (`:136-139`) |
| `pl_sd` | есть | **удалено** |
| новые | — | `set_renderer&id=`, `unset_renderer` (Chromecast/renderer discovery, `:187-194`), `renderers` в выводе (`get_renderers`) |
| `status.information` | `chapters/titles` массивы индексов | `chapters_count`, `titles_count` (`:520-524`) |
| `playlist.json` | дерево node/leaf | **плоский массив** leaf-объектов `{type:"leaf", id, uri, name, duration, current}` без `ro` и без узлов (`:314-338`, `vlc.playlist.list()`) |
| `volume` | 0..512 | то же |
| HTTPS | нет | `--http-cert` (`NEWS:271`) |
| `key` | `common.hotkey("key-"..val)` | то же (`:150-151`) |

Lua-API master для своих страниц (`share/lua/README.txt`, раздел Player): `player.get_audio_tracks()`
→ `{ {id, name, selected}, … }`, `get_spu_tracks`, `get_video_tracks`, `get_time()` (µs),
`get_position()`, `get_rate()`, `get_title_index()`, `get_chapter_index()`, `seek_by_*`,
`get/set_audio_delay` (секунды, float). Это закрывает проблему «текущая дорожка» штатно.

**RC в master.** Lua-подмодуль `cli` из `modules/lua/vlc.c` **убран**; RC переписан на C:
`modules/control/cli/cli.c` с шорткатами `cli`, `rc`, `oldrc` (`cli.c:1087`), опции
`rc-host`, `rc-quiet`, `rc-unix`, `rc-show-pos`, `rc-fake-tty` (`:1070-1082`). Команды
(`cli.c:267-279`, `cli/player.c:1016-1060`, `cli/playlist.c:567-581`): как в 3.0 плюс
`rate`, `record`, `fs`, `move` (но без `delete`); `atrack/vtrack/strack N` теперь принимают
**0-based индекс в списке дорожек**, `-1` снимает выбор; без аргумента печатают
`| <index> - <name> [*]` (`player.c:429-497`) — **не ES id**. `adev` сохранен (`player.c:771-793`).
`key`/`hotkey` по-прежнему требуют `key-` (`cli.c:253-265`, `actions.c:617`).
Отдельно существует `share/lua/intf/cli.lua` (для `--extraintf luaintf --lua-intf cli`):
там `hotkey` принимает оба написания (`cli.lua:547-552`), `adev` закомментирован (`:604`),
`atrack` печатает `vlc.player.get_audio_tracks()` (id = input id) и вызывает
`toggle_audio_track` (`:519-531`).

Вывод: клиент под 3.0.x должен закладывать слой абстракции над «выбор дорожки»
(select vs toggle, id vs index) и над формой `playlist.json`.

---

## 6. Прочие пути управления на Windows

### 6.1 Командная строка и one-instance

- `--one-instance` (`libvlc-module.c:2090`), `--one-instance-when-started-from-file` (по умолчанию
  вкл., `:2095`) + `--started-from-file` (`:2092`, ставится ассоциацией файлов), `--playlist-enqueue`
  (`:2098`). Механизм (`src/win32/specific.c:90-169`): мьютекс и окно `"VLC ipc <версия>"`
  (`FindWindow`, `:126`), данные через `WM_COPYDATA` (`:169`); приемник `modules/control/win_msg.c:60-115`
  добавляет каждый аргумент как элемент (`playlist_AddExt`), **первый — с запуском**, если не
  `playlist-enqueue` (`:101-107`); аргументы, начинающиеся с `:`, прикрепляются как опции элемента
  (`:88-93`). Т.е. `vlc.exe --one-instance "D:\x.mkv" :start-time=600` из любого процесса на ПК
  передаст файл работающему VLC. Через сеть — нет (только локальный компаньон).
- Псевдо-URL `vlc://` (`modules/access/idummy.c:148-198`, шорткаты `dummy`, `vlc`): `vlc://quit`
  (закрыть VLC, `:158-166`), `vlc://pause` (вечная пауза-заглушка, `:168-175`), `vlc://pause:N`
  (пауза N секунд как элемент плейлиста, `:177-198`), `vlc://nop`. Работают как элементы плейлиста,
  в т.ч. через `in_play`/`add`. Для пульта `in_play&input=vlc://quit` — рабочий «выключить VLC»
  (**не проверено** вживую), но и дыра: закрывать по паролю может кто угодно.
- Полезные флаги запуска (`libvlc-module.c`, `modules/gui/qt/qt.cpp`):
  `--fullscreen` (`:1557`), `--no-video-title-show` (`:1585`), `--video-title-timeout` (`:1588`),
  `--video-x/--video-y` (`:1620-1622`), `--video-on-top` (`:1578`), `--aspect-ratio` (`:1628`),
  `--start-time/--stop-time/--run-time` (секунды), `--sub-file=<путь>` (`:1684`),
  `--audio-track=N`/`--sub-track=N` (**индекс**, `:1720-1723`), `--audio-track-id`/`--sub-track-id`
  (ES id, `:1738-1741`), `--audio-language=ru,en`/`--sub-language` (`:1726-1730`), `--rate` (`:1770`),
  `--random/--loop/--repeat` (`:2071-2077`), `--play-and-exit/--play-and-stop/--play-and-pause`
  (`:2080-2083`), `--no-auto-preparse` (`:2109`);
  Qt: `--qt-fullscreen-screennumber=N` (`qt.cpp:297`, -1 = текущий), `--qt-minimal-view` (`:230`),
  `--qt-start-minimized` (`:240`), `--qt-system-tray` (`:233`), `--qt-notification` (`:235`),
  `--qt-continue` (`:261`, «продолжить с места»), `--qt-pause-minimized` (`:242`),
  `--no-qt-privacy-ask` (`:293`), `--qt-disable-volume-keys` (`:274`), `--qt-auto-raise` (`:322`).
- Звук: модули вывода — `mmdevice` (WASAPI, приоритет 150, `modules/audio_output/mmdevice.c:1534-1549`),
  `directsound` (`directx`, 100, `directsound.c:73-81`), `waveout`. Выбор устройства при старте:
  `--aout=mmdevice --mmdevice-audio-device=<endpoint id>` (`mmdevice.c:1547`) или
  `--directx-audio-device=<id>` (`directsound.c:80`). ID endpoint'ов можно получить только из
  `adev` (RC) / libvlc `libvlc_audio_output_device_enum` / WinAPI `IMMDeviceEnumerator`.
  Опции `mmdevice-passthrough`, `mmdevice-volume` (`:1543-1549`).

### 6.2 Медиаклавиши, SMTC, D-Bus

- Qt-интерфейс обрабатывает `WM_APPCOMMAND` (`main_interface_win32.cpp:254-312`): play/pause,
  play, pause, prev/next, stop, volume up/down/mute (если не `qt-disable-volume-keys`), rewind/ff.
  Это стандартные медиаклавиши клавиатуры — работают, когда сообщение доходит до окна VLC.
  Дополнительно `vlc.exe` всегда поднимает `globalhotkeys` (`bin/vlc.c:242`) — глобальные
  `RegisterHotKey` для действий, у которых задана «глобальная» клавиша (`globalhotkeys/win32.c:253`).
- **SMTC (System Media Transport Controls) VLC 3.0.x не регистрирует**: в дереве 3.0.x и в `master`
  нет ни `SystemMediaTransportControls`, ни упоминаний в `NEWS`. Существует сторонний плагин
  [vlc-win10smtc](https://github.com/spmn/vlc-win10smtc) (MSVC, VLC 3.0.x, последнее обновление
  2021). Следствие: Windows-оверлей «сейчас играет» и Bluetooth-AVRCP через SMTC от VLC не
  получить; компаньон при желании может сам зарегистрировать SMTC и транслировать команды в VLC.
- D-Bus/MPRIS — только `HAVE_DBUS` (Linux); на Windows не собирается (`libvlc-module.c:2101-2102`).

### 6.3 libvlc как «компаньон»

`libvlc` (`libvlc_audio_output_device_enum/set`, `libvlc_media_player_*`) управляет **своим**
экземпляром плеера, а не запущенным `vlc.exe`; прикрепиться к GUI-процессу нельзя. Компаньон на
libvlc = свой плеер (без Qt-окна/меню VLC), а не удаленка для VLC. Поэтому для управления
работающим VLC компаньон может использовать: RC на `127.0.0.1`, HTTP, one-instance IPC, свои
Lua-страницы, WinAPI (окно/монитор/сон), SMTC-мост.

---

## 7. Расширение HTTP-интерфейса своими Lua-страницами

### 7.1 Откуда `http.lua` берет страницы и как переопределить без Program Files

`http.lua:271-281`:

```lua
local function find_datadir(name)
    local list = vlc.config.datadir_list(name)
    for _, l in ipairs(list) do
        local s = vlc.net.stat(l)
        if s then return l end          -- ПЕРВЫЙ существующий каталог
    end
    error("Unable to find the `"..name.."' directory.")
end
http_dir = config.dir or find_datadir("http")
```

`vlc.config.datadir_list("http")` → `vlclua_dir_list` (`modules/lua/vlc.c:214-241`,
`libs/configuration.c:148-166`), порядок:

1. `config_GetUserDir(VLC_DATA_DIR)\lua\http` = **`%APPDATA%\vlc\lua\http`** (`src/win32/dirs.c:281-300`:
   `CSIDL_APPDATA` + `\vlc`);
2. `config_GetLibDir()\lua\http` = `C:\Program Files\VideoLAN\VLC\lua\http`;
3. `config_GetDataDir()\lua\http` — на Windows совпадает с libdir (`dirs.c:241-244`) и пропускается
   (`vlc.c:228-237`, `both == false`).

Далее `load_dir(http_dir)` (`http.lua:294-327, 343`) регистрирует **только файлы выбранного
каталога** (рекурсивно): текстовые (`text/*` по таблице `mimes`) — как Lua-шаблоны, остальные —
как raw; `index.html` → `/`; при отсутствии `index.html` и `no_index=false` добавляется
автолистинг. `custom.lua` грузится из того же `http_dir` (`:283-293`, отсутствие — только warning).

**Следствие: объединения нет.** Если `%APPDATA%\vlc\lua\http` существует, сервер отдает только его
содержимое; штатные `requests/status.json` и веб-UI из Program Files **перестают отвечать (404)**.
**Проверено вживую** (2026-10-12, VLC 3.0.23, `-I dummy --extraintf http --http-host 127.0.0.1`):
при единственном файле `%APPDATA%\vlc\lua\http\requests\probe.json` → `GET /requests/probe.json`
= 200 `{"probe":true}`, `GET /requests/status.json` = **404 Not found**, `GET /` = 200
(автолистинг). Утверждение в `live-probe.md` («пользовательская папка объединяется с
системной») **неверно** — вероятно, там VLC не перезапускался между проверками или проверялся
только новый файл.

Как правильно подкладывать свои страницы, не трогая Program Files:

1. Создать `%APPDATA%\vlc\lua\http\` и **скопировать туда** из `C:\Program Files\VideoLAN\VLC\lua\http\`
   как минимум `requests\*.json|xml` (это обычные текстовые Lua-шаблоны, GPL) и, если нужен штатный
   веб-UI, весь каталог (`index.html`, `js`, `css`, `images`, `dialogs`, `custom.lua`, …).
2. Добавить свои `requests\tracks.json` и т.п. Перезапустить VLC (регистрация URL — только при старте;
   содержимое уже зарегистрированных файлов перечитывается по mtime, `http.lua:87-104`).
3. Альтернатива без APPDATA: `--http-src=<каталог>` → `config.dir` (`intf.c:67-80`) — та же
   семантика «только этот каталог».
4. `require "httprequests"`/`require "common"` в своих страницах работают: `package.path`
   строится от местоположения загруженного `http.luac` (`intf.c:290`, `vlc.c vlclua_add_modules_path`:
   `<каталог скрипта>/modules`, `<родитель>/modules` и далее каталоги `datadir_list("intf")`,
   начиная с того, где найден скрипт). Для системного `http.luac` это только
   `C:\Program Files\VideoLAN\VLC\lua\intf\modules` и `...\lua\modules`, т.е. системные
   `.luac`-модули остаются доступны, а вот свои модули в `%APPDATA%\vlc\lua\intf\modules`
   не найдутся, пока не переопределен сам `intf\http.lua` (п. 5).
5. Пользовательский каталог ищется и для самих интерфейсных скриптов (`vlclua_find_file("intf","http")`,
   `vlc.c:309-339`, расширения `.luac`, `.lua`, `.vle`): файл `%APPDATA%\vlc\lua\intf\http.lua` целиком
   подменит системный `http.luac`. Это путь для глубокой кастомизации, но хрупкий при обновлениях VLC.
6. В `master` логика `http.lua` та же (diff касается только `/art`), т.е. подход переносим.

Права: `%APPDATA%` пишется пользователем без UAC; приложение-пульт само это сделать не может
(нет доступа к ФС ПК), нужен установочный скрипт/компаньон.

### 7.2 Что может вернуть своя страница сверх `status.json` (API 3.0.x)

Доступно из интерфейсного скрипта (`share/lua/README.txt`): `vlc.object.{input,playlist,aout,vout,libvlc}`,
`vlc.var.{get,set,get_list,inherit,create,trigger_callback,toggle_bool,count_choices}`,
`vlc.playlist.*`, `vlc.input.item()`, `vlc.volume.*`, `vlc.config.*`, `vlc.misc.action_id`,
`vlc.equalizer.*`, `vlc.net.*`, `vlc.io.*`, `vlc.sd.*`, `vlc.vlm()`.

| Задача | Вызов | Результат | Статус |
|---|---|---|---|
| Список аудио/суб/видео дорожек **с ID и текущей** | `local ids, names = vlc.var.get_list(input, "audio-es")`; `cur = vlc.var.get(input, "audio-es")` (`variables.c:236-252`, `var_Change GETCHOICES`) | `ids` = `{-1, id1, id2…}`, `names` = `{"Disable", "Описание - [Russian]", "Track 2 - [English]"…}` (`es_out.c:936-963`); `cur` — текущий ES id | **проверено вживую** соседом (`tracks-custom-page.json`) |
| Выбор дорожки | `vlc.var.set(input, "audio-es", id)` | то же, что HTTP `audio_track` | проверено соседом |
| Главы/титры с именами | `get_list(input, "chapter")`, `get_list(input, "title")` | индексы + `"Chapter N"`/имя, `"Title N [h:mm:ss]"` (`var.c:316-352`) | проверено соседом |
| Mute | `vlc.var.get(aout, "mute")` (bool, `output.c:223`) | текущее состояние | **не проверено** |
| Установить mute | `vlc.var.set(aout,"mute",true)` — только копирует в `playlist`-переменную (`output.c:57-63`), сам aout не трогает; надежно — `common.hotkey("key-vol-mute")` | | не проверено |
| Состояние входа | `vlc.var.get(input,"state")` (1..6), `"can-seek"`, `"can-pause"`, `"can-rate"`, `"length"` (µs) | | по `var.c:95-215` |
| Стерео-режим | `get_list(aout,"stereo-mode")` / `set` | Mono/Stereo/Original/Headphones | проверено соседом |
| Аспект/кроп/зум/деинтерлейс | `get_list(vout, "aspect-ratio"|"crop"|"zoom"|"deinterlace"|"deinterlace-mode")` / `set` | списки допустимых значений с подписями | проверено соседом (частично) |
| **Аудиоустройства: список** | `get_list(aout, "device")` | **пусто**: `device` — обычная строковая переменная без choices (`output.c:225`); список живет в `owner->dev.list` и доступен только C-API `aout_DevicesList` (`output.c`, без Lua-биндинга) | **проверено вживую** соседом (пусто) + код |
| Текущее аудиоустройство | `vlc.var.get(aout, "device")` | id текущего (переменная-отчет, `aout_DeviceNotify`, `output.c:101-104`); пусто = по умолчанию | не проверено |
| **Переключить аудиоустройство** | `vlc.var.set(aout, "device", id)` | **не переключает**: срабатывает только `var_CopyDevice` → `audio-device` родителя (`output.c:67-74`); реальное переключение идет через `aout_DeviceSet` → `owner->req.device` → `aout_OutputDeviceSet` (`output.c:833-835, 695-699`), у которого нет Lua-биндинга | не проверено, но по коду невозможно |
| Обходной путь для устройства | `vlc.config.set("mmdevice-audio-device", id)` + перезапуск aout (stop/play) | устройство применится при следующем создании aout (`mmdevice.c:1351`); `config.set` не пишет `vlcrc` на диск | не проверено |
| Цикл устройств | `common.hotkey("key-audiodevice-cycle")` | по кругу, OSD | — |
| Push/long-polling | блокирующая страница с `vlc.misc.mwait` | **нельзя**: хост однопоточный (§2.2) — заблокирует все запросы | — |
| Громкость | `vlc.volume.get/set/up/down` | 0..512 | — |
| Текущий элемент | `vlc.playlist.current()`, `vlc.input.item():uri()/name()/metas()` | | — |

Пример минимальной страницы `requests/tracks.json`:

```lua
<?vlc
local httprequests = require "httprequests"
httprequests.processcommands()          -- чтобы команды работали и с этой страницы
local out = { audio = {}, spu = {}, video = {} }
local input = vlc.object.input()
if input then
  for key, var in pairs({audio="audio-es", spu="spu-es", video="video-es"}) do
    local ids, names = vlc.var.get_list(input, var)
    local cur = vlc.var.get(input, var)
    local list = { _array = {} }
    for i, id in ipairs(ids) do
      table.insert(list._array, { id = id, name = names[i], selected = (id == cur) })
    end
    out[key] = list
  end
end
httprequests.printTableAsJson(out)
?>
```

(`_array` — соглашение `printTableAsJson`/`removeArrayIndicators`, `httprequests.lua:228-252`,
чтобы пустые таблицы печатались как массивы.)

### 7.3 Отображение `key` в hotkey: сводка

| Канал | Что передать | Код |
|---|---|---|
| HTTP 3.0.x / master | `command=key&val=nav-up` (без `key-`) | `httprequests.lua:162-163` / master `:150-151` → `common.hotkey("key-"..val)` → `vlc.misc.action_id` → `vlc_actions_get_id` (требует `key-`) |
| RC 3.0.x (`oldrc.c`) | `key key-nav-up` (с `key-`) | `oldrc.c:729-733` |
| RC master (`cli.c`) | `key key-nav-up` | `cli.c:253-265` |
| `cli.lua` (luaintf) | `hotkey nav-up` или `hotkey key-nav-up` | `cli.lua:547-552` |
| Своя Lua-страница | `common.hotkey("key-nav-up")` | `common.lua:25-33` |

---

## 8. Выводы для дизайна пульта

1. **Базовый транспорт — HTTP (`status.json`/`playlist.json`/`browse.json`)**. Он покрывает
   play/pause/stop/seek/rate/volume/fullscreen/главы/задержки/эквалайзер/плейлист/обзор ФС/обложку
   и даже выбор дорожек (ID восстанавливаются из `Stream N` + `Original_ID`). Клиент: HTTP/1.1 без
   заголовка `Connection`, одно соединение, опрос 500-1000 мс, валидация JSON (ошибки приходят
   HTML-ом со статусом 200, MIME `text/plain`), терпимость к `fullscreen` bool/0, к отсутствию
   `information` в `stopped`, к локализованным ключам `information`.
2. **Через `key`** закрываются mute (`vol-mute`), циклы дорожек, навигация по меню, jump на
   фиксированные интервалы, `audiodevice-cycle` (единственный «переключатель звука» в чистом HTTP).
   Не полагаться на `volume&val=N%` и на `+`-как-пробел.
3. **Чего HTTP не дает**: текущая дорожка, названия глав, mute-состояние, список/выбор
   аудиоустройства, push-события, несколько интерфейсов — это ровно зона компаньона.
4. **«Легкий компаньон» = свои Lua-страницы в `%APPDATA%\vlc\lua\http`** (с копией штатных
   `requests/*`): дает текущие дорожки с именами, главы с именами, mute, списки аспект/кроп/
   деинтерлейс, состояние входа. Не дает: список/переключение аудиоустройств (нет Lua-биндинга
   `aout_DevicesList/DeviceSet`), push. Требует установки файлов на ПК и перезапуска VLC;
   помнить, что папка **подменяет**, а не дополняет системную.
5. **Полный компаньон (процесс на ПК)**: держит RC на `127.0.0.1:4212` (единственный клиент,
   `--rc-quiet`), получает push `status change:`, списки `atrack/strack/adev` с `*`, переключает
   устройство `adev <id>`; обходит «в паузе ничего нельзя» (RC) тем, что для seek/next использует
   HTTP; добавляет WinAPI (окно, монитор `--qt-fullscreen-screennumber` только при старте,
   сон/пробуждение), запуск VLC с нужными флагами (`--extraintf http:rc …`), one-instance IPC,
   при желании — мост SMTC/AVRCP, собственный WebSocket для телефона. Не строить компаньон на
   libvlc — он не управляет запущенным `vlc.exe`.
6. **Безопасность**: HTTP только с паролем (Basic, пустой логин); RC без авторизации — только
   localhost; `in_play&input=vlc://quit`/`add` произвольных URL доступны любому с паролем.
7. **VLC 4**: страницы те же, но `audio_track` станет toggle, `playlist.json` — плоским, RC —
   индексы вместо ES id; абстрагировать «выбор дорожки» и «структура плейлиста» за интерфейсом,
   определять версию по `status.version`.
