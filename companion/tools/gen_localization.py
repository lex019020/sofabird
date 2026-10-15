#!/usr/bin/env python3
"""Generates companion localization: .resx (English neutral + Russian) and typed C# accessors.

Edit the tables below, then run:  python companion/tools/gen_localization.py
Outputs:
  companion/Sofabird.Companion/Localization/Strings{,.ru}.resx + UiText.cs   (PC UI: Windows UI language)
  companion/Sofabird.Core/Localization/ProtocolText{,.ru}.resx + ProtocolText.cs   (err.msg: phone language)
To add a language, add a column here (and a Strings.<lang>.resx / ProtocolText.<lang>.resx is produced).
"""
import os
import re
from xml.sax.saxutils import escape

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
LANGS = ["", "ru"]  # "" = neutral (English)

UI = [
    ("AlreadyRunning", "Sofabird is already running (see the tray icon).", "Sofabird уже запущен (иконка в трее)."),
    ("FatalError", "Sofabird: fatal error.\n{0}", "Sofabird: критическая ошибка.\n{0}"),
    ("PortFailed", "Could not open port {0}: {1}\nChange wsPort in {2}.", "Не удалось открыть порт {0}: {1}\nИзмените wsPort в {2}."),
    ("PairingTitle", "Pair a phone", "Сопряжение телефона"),
    ("PairingBrand", "Sofabird · connecting a phone to this PC", "Sofabird · подключение телефона к этому ПК"),
    ("PairingAddress", "Enter this code in Sofabird on your phone. This PC: {0}", "Введите этот код в Sofabird на телефоне. Этот ПК: {0}"),
    ("PairingHeaderGeneric", "Sofabird pairing code", "Код сопряжения Sofabird"),
    ("PairingHeaderDevice", "Phone “{0}” wants to connect", "Телефон «{0}» хочет подключиться"),
    ("PairingTimer", "The code is valid for {0}. Click to close this window.", "Код действует ещё {0}. Нажмите, чтобы закрыть окно."),
    ("PairingDone", "Phone “{0}” connected", "Телефон «{0}» подключён"),
    ("PairingClosing", "This window will close in a few seconds", "Окно закроется через несколько секунд"),
    ("TrayPhonesConnected", "Phones connected: {0}", "Телефонов подключено: {0}"),
    ("TrayTipPhones", "Phones: {0}", "Телефонов: {0}"),
    ("TrayBluetoothOn", "Bluetooth: on", "Bluetooth: включён"),
    ("TrayBluetoothStarting", "Bluetooth: starting…", "Bluetooth: запускается…"),
    ("TrayBluetoothRadioOff", "Bluetooth: turned off on this PC", "Bluetooth: выключен на этом ПК"),
    ("TrayBluetoothUnavailable", "Bluetooth: unavailable", "Bluetooth: недоступен"),
    ("TrayBluetoothDisabled", "Bluetooth: disabled in settings", "Bluetooth: выключен в настройках"),
    ("TrayPair", "Pair a phone…", "Сопряжение телефона…"),
    ("TrayStartVlc", "Start VLC", "Запустить VLC"),
    ("TrayRestartVlc", "Restart VLC with phone control", "Перезапустить VLC с управлением с телефона"),
    ("TrayQuitVlc", "Close VLC", "Закрыть VLC"),
    ("TrayMoveToDisplay", "Move VLC to display", "Перенести VLC на экран"),
    ("TrayDisplayHint", "VLC opens on the checked display", "VLC открывается на отмеченном экране"),
    ("TrayDisplayBuiltIn", "{0} (built-in)", "{0} (встроенный)"),
    ("TrayDisplayDisconnected", "{0} (not connected)", "{0} (не подключён)"),
    ("TrayDisplayUnmanaged", "Don't manage the display", "Не управлять экраном"),
    ("TrayInstallLua", "Install Lua pages", "Установить Lua-страницы"),
    ("TrayInstallLuaPending", "Install Lua pages (after VLC closes)", "Установить Lua-страницы (после закрытия VLC)"),
    ("TrayAutostart", "Start with Windows", "Автозапуск"),
    ("TrayKeepAwake", "Keep the PC awake during a movie", "Не давать ПК уснуть во время фильма"),
    ("TrayTestBuilds", "Get test builds", "Получать тестовые сборки"),
    ("TrayAbout", "About Sofabird…", "О программе…"),
    ("AboutText", "Sofabird {0}\n\nFree software under the GNU General Public License v3 or later, without any "
                  "warranty.\nSource code: https://github.com/lex019020/sofabird\n\nPosters and descriptions: TMDB. This "
                  "product uses the TMDB API but is not endorsed or certified by TMDB.",
     "Sofabird {0}\n\nСвободная программа под лицензией GNU General Public License v3 или более поздней "
     "версии, без каких-либо гарантий.\nИсходный код: https://github.com/lex019020/sofabird\n\nПостеры и описания: "
     "TMDB. Этот продукт использует TMDB API, но не одобрен и не сертифицирован TMDB."),
    ("TrayOpenLog", "Open log", "Открыть журнал"),
    ("TrayOpenSettings", "Open settings folder", "Открыть папку настроек"),
    ("TrayExit", "Exit", "Выход"),
    ("VlcNotFound", "VLC not found", "VLC не найден"),
    ("VlcNotRunning", "VLC is not running", "VLC не запущен"),
    ("VlcStarting", "VLC is starting…", "VLC запускается…"),
    ("VlcNoControl", "VLC is open without phone control", "VLC открыт без управления с телефона"),
    ("PlaybackConnected", "connected", "подключён"),
    ("PlaybackPlaying", "playing", "воспроизведение"),
    ("PlaybackPaused", "paused", "пауза"),
    ("PlaybackStopped", "stopped", "остановлен"),
    ("BalloonNoControl", "VLC is open without phone control. Restart it, and from then on VLC will always start with phone control.",
     "VLC открыт без управления с телефона. Перезапустите его — дальше VLC будет запускаться с управлением сам."),
    ("BalloonPaired", "Phone “{0}” paired", "Телефон «{0}» сопряжён"),
    ("BalloonUpdated", "The companion was updated to {0}", "Компаньон обновлён до версии {0}"),
    ("BalloonLuaInstalled", "Lua pages were installed to %APPDATA%\\vlc\\lua\\http. They take effect the next time VLC starts.",
     "Lua-страницы установлены в %APPDATA%\\vlc\\lua\\http. Они заработают при следующем запуске VLC."),
    ("BalloonLuaDeferred", "VLC is running: the pages will be installed automatically after it closes.",
     "VLC запущен — страницы будут установлены автоматически после его закрытия."),
    ("BalloonLuaFailed", "Could not install the Lua pages (see the log).", "Не удалось установить Lua-страницы (см. журнал)."),
    ("BalloonAutostartFailed", "Could not change autostart: {0}", "Не удалось изменить автозапуск: {0}"),
    ("BalloonFirewallBlocked", "Windows Firewall blocks the phone from connecting. Click to fix it.",
     "Брандмауэр Windows не пускает телефон. Нажмите, чтобы исправить."),
    ("BalloonFirewallMissing", "Network access for the phone is not set up. Click to allow it.",
     "Доступ телефона по сети не настроен. Нажмите, чтобы разрешить."),
    ("TraySetup", "Setup and permissions…", "Настройка и разрешения…"),
    # Setup and permissions window (Setup/SetupForm.cs). Every permission or system change gets an item there.
    ("SetupTitle", "Sofabird: setup and permissions", "Sofabird: настройка и разрешения"),
    ("SetupHeader", "Set up this PC for the remote", "Подготовим ПК к работе с пультом"),
    ("SetupIntro", "So that the phone can control VLC, the companion needs a few permissions and settings on this PC. "
                   "Here is what each of them does and why it is needed. Nothing changes until you press Apply.",
     "Чтобы телефон мог управлять VLC, компаньону нужны несколько разрешений и настроек на этом ПК. "
     "Ниже — что делает каждое из них и зачем оно нужно. Пока вы не нажмёте «Применить», ничего не меняется."),
    ("SetupChecking", "Checking…", "Проверяем…"),
    ("SetupOn", "✓ On", "✓ Включено"),
    ("SetupOff", "Off", "Выключено"),
    ("SetupFooterNote", "You can open this window at any time from the tray icon menu: “Setup and permissions…”.",
     "Это окно всегда можно открыть из меню значка в трее: «Настройка и разрешения…»."),
    ("SetupApply", "Apply", "Применить"),
    ("SetupLater", "Later", "Позже"),
    ("SetupClose", "Close", "Закрыть"),
    ("SetupFirewallTitle", "Network access (Windows Firewall)", "Доступ по сети (брандмауэр Windows)"),
    ("SetupFirewallText", "The phone connects to this PC over Wi‑Fi, and Windows blocks incoming connections until you allow them. "
                          "The companion will allow itself and VLC to accept connections on private (home) networks only. "
                          "Windows will ask for administrator permission once.",
     "Телефон подключается к ПК по Wi‑Fi, а Windows блокирует входящие подключения, пока их не разрешить. "
     "Компаньон разрешит себе и VLC принимать подключения только в частных (домашних) сетях. "
     "Windows один раз запросит права администратора."),
    ("SetupFirewallOk", "✓ Allowed on private networks", "✓ Разрешено в частных сетях"),
    ("SetupFirewallOff", "✓ Windows Firewall is off for private networks: nothing to allow",
     "✓ Брандмауэр Windows выключен для частных сетей — разрешать нечего"),
    ("SetupFirewallMissing", "Not allowed yet: Windows will block the phone", "Ещё не разрешено — Windows будет блокировать телефон"),
    ("SetupFirewallBlocked", "⚠ A firewall rule blocks {0} (usually left by “Cancel” in a Windows prompt). Apply removes it.",
     "⚠ Правило брандмауэра блокирует {0} (обычно остаётся после «Отмена» в запросе Windows). «Применить» его удалит."),
    ("SetupProgramCompanion", "the companion", "компаньон"),
    ("SetupProgramVlc", "VLC", "VLC"),
    ("SetupAnd", "{0} and {1}", "{0} и {1}"),
    ("SetupFirewallUnknown", "Could not read the firewall rules: {0}", "Не удалось прочитать правила брандмауэра: {0}"),
    ("SetupFirewallWaiting", "Waiting for administrator permission: confirm the Windows prompt…",
     "Ждём права администратора — подтвердите запрос Windows…"),
    ("SetupFirewallCancelled", "⚠ Administrator permission was not given. Press Apply to try again, or allow access when Windows asks.",
     "⚠ Права администратора не получены. Нажмите «Применить» ещё раз или разрешите доступ, когда Windows спросит сама."),
    ("SetupFirewallFailed", "✗ Could not change the firewall rules ({0}). Details are in the log.",
     "✗ Не удалось изменить правила брандмауэра ({0}). Подробности в журнале."),
    ("SetupFirewallNotManaged", "Not managed by the companion: allow access yourself when Windows asks",
     "Компаньон не настраивает — разрешите доступ сами, когда Windows спросит"),
    ("SetupPublicNetwork", "⚠ The network “{0}” is public. Windows does not let the phone in on public networks: make it private in the network settings.",
     "⚠ Сеть «{0}» отмечена как общедоступная. В таких сетях Windows не пускает телефон — сделайте её частной в параметрах сети."),
    ("SetupOpenNetworkSettings", "Open network settings", "Открыть параметры сети"),
    ("SetupAutostartTitle", "Start with Windows", "Запуск вместе с Windows"),
    ("SetupAutostartText", "The companion starts when you sign in to Windows, so the phone can control VLC without anyone touching the PC. "
                           "It uses your own startup list; no administrator rights are needed.",
     "Компаньон запускается при входе в Windows, и телефоном можно управлять, не подходя к ПК. "
     "Используется ваш список автозагрузки, права администратора не нужны."),
    ("SetupAutostartFailed", "✗ Could not change autostart: {0}", "✗ Не удалось изменить автозапуск: {0}"),
    ("SetupVlcTitle", "Configure VLC for phone control", "Настроить VLC для управления с телефона"),
    ("SetupVlcText", "VLC gets its web interface turned on with a password made by the companion, so VLC opened in any way "
                     "(even by a double-click on a movie) can be controlled from the phone. The companion edits VLC's settings file "
                     "(a backup copy is kept) and installs helper pages for audio tracks and subtitles. VLC's own "
                     "“continue playback?” question is turned off: the phone asks instead.",
     "В VLC включается веб‑интерфейс с паролем, который создал компаньон, — так VLC, открытый как угодно "
     "(даже двойным щелчком по фильму), управляется с телефона. Компаньон правит файл настроек VLC "
     "(резервная копия сохраняется) и ставит вспомогательные страницы для звуковых дорожек и субтитров. "
     "Собственный вопрос VLC «продолжить воспроизведение?» отключается — вместо него спрашивает телефон."),
    ("SetupVlcConfigured", "✓ VLC is configured", "✓ VLC настроен"),
    ("SetupVlcNotConfigured", "Not configured yet", "Ещё не настроен"),
    ("SetupVlcRunning", "VLC is running: its settings will be written after it closes",
     "VLC запущен — настройки будут записаны после его закрытия"),
    ("SetupVlcNotInstalled", "VLC is not installed. Install VLC 3.x, then open this window again from the tray menu.",
     "VLC не установлен. Установите VLC 3.x и снова откройте это окно из меню в трее."),
    ("SetupVlcOff", "Off: VLC's settings stay as they are", "Выключено — настройки VLC остаются как есть"),
    ("SetupVlcFailed", "✗ Could not configure VLC (details are in the log)", "✗ Не удалось настроить VLC (подробности в журнале)"),
    ("SetupKeepAwakeTitle", "Keep the PC awake during a movie", "Не давать ПК уснуть во время фильма"),
    ("SetupKeepAwakeText", "While a movie is playing, Windows won't put the PC to sleep or turn off the screen. "
                           "When playback stops, your usual power settings apply again.",
     "Пока идёт фильм, Windows не переведёт ПК в сон и не погасит экран. "
     "После остановки снова действуют обычные настройки электропитания."),
    ("SetupMediaTitle", "Media folders", "Медиапапки"),
    ("SetupMediaText", "Folders with your movies and series, on this PC's disks or network shares. The companion recognizes "
                       "the titles by the file names and fetches posters and descriptions, and the phone shows them as a "
                       "library. Other folders can still be opened from the drives.",
     "Папки с вашими фильмами и сериалами — на дисках этого ПК или в сетевых папках. Компаньон узнаёт названия по "
     "именам файлов и загружает постеры и описания, а телефон показывает их как медиатеку. Остальные папки по-прежнему "
     "открываются через диски."),
    ("SetupMediaAdd", "Add a folder…", "Добавить папку…"),
    ("SetupMediaRemove", "Remove", "Убрать"),
    ("SetupMediaPick", "Choose a folder with movies or series", "Выберите папку с фильмами или сериалами"),
    ("SetupMediaNone", "No folders yet: the phone browses the drives only", "Папок пока нет: телефон показывает только диски"),
    ("SetupMediaCount", "Folders: {0}", "Папок: {0}"),
    ("SetupMediaTmdb", "Posters and descriptions: TMDB. This product uses the TMDB API but is not endorsed or certified by TMDB.",
     "Постеры и описания: TMDB. Этот продукт использует TMDB API, но не одобрен и не сертифицирован TMDB."),
    ("SetupMediaStationTitle", "This PC is a media station", "Этот ПК — медиастанция"),
    ("SetupMediaStationText", "For a PC or laptop that lives at the TV: it keeps working with the lid closed and never goes to sleep "
                              "or hibernates by itself — plugged in and on battery, so a power cut does not stop it either. "
                              "The companion changes these settings of the current Windows power plan; unticking this later does "
                              "not change them back. The screen may still turn off, and the TV may go to standby.",
     "Для компьютера или ноутбука у телевизора: он продолжает работать с закрытой крышкой и сам не засыпает "
     "и не уходит в гибернацию — и от сети, и от батареи, чтобы его не остановило даже отключение света. "
     "Компаньон меняет эти настройки текущей схемы электропитания Windows; если потом снять галочку, они не вернутся назад. "
     "Экран по‑прежнему может гаснуть, а телевизор — уходить в дежурный режим."),
    ("SetupMediaStationOk", "✓ The lid can be closed; the PC does not go to sleep", "✓ Крышку можно закрывать, ПК не засыпает"),
    ("SetupMediaStationOff", "Off: the usual Windows power settings apply", "Выключено — действуют обычные настройки электропитания Windows"),
    ("SetupMediaStationNow", "Now: {0}", "Сейчас: {0}"),
    ("SetupMediaStationDrift", "⚠ The power settings were changed: {0}. Press Apply to set them again.",
     "⚠ Настройки электропитания изменились: {0}. Нажмите «Применить», чтобы вернуть их."),
    ("SetupMediaStationUnknown", "Could not read the Windows power plan", "Не удалось прочитать схему электропитания Windows"),
    ("SetupMediaStationFailed", "✗ Could not change the power plan ({0}). Details are in the log.",
     "✗ Не удалось изменить схему электропитания ({0}). Подробности в журнале."),
    ("PowerPluggedIn", "plugged in", "от сети"),
    ("PowerOnBattery", "on battery", "от батареи"),
    ("PowerLidStops", "closing the lid stops it ({0})", "закрытие крышки его останавливает ({0})"),
    ("PowerSleepAfter", "sleep after {0} min ({1})", "сон через {0} мин ({1})"),
    ("PowerHibernateAfter", "hibernation after {0} min ({1})", "гибернация через {0} мин ({1})"),
    ("BalloonMediaStationDrift", "The power settings of the media station were changed: the PC may go to sleep. Click to fix.",
     "Настройки электропитания медиастанции изменились — ПК может уснуть. Нажмите, чтобы исправить."),
    ("SetupBluetoothTitle", "Bluetooth (optional)", "Bluetooth (необязательно)"),
    ("SetupBluetoothText", "Without Wi‑Fi the phone can control the PC over Bluetooth. Windows asks for no permission for this, "
                           "but the phone has to be paired with this PC once in the Windows Bluetooth settings.",
     "Без Wi‑Fi телефон может управлять ПК по Bluetooth. Разрешений Windows для этого не нужно, "
     "но телефон надо один раз сопрячь с этим ПК в параметрах Bluetooth Windows."),
    ("SetupBluetoothOn", "✓ Bluetooth is on: the phone can connect once it is paired",
     "✓ Bluetooth включён — телефон сможет подключиться после сопряжения"),
    ("SetupBluetoothStarting", "Bluetooth is starting…", "Bluetooth запускается…"),
    ("SetupBluetoothRadioOff", "Bluetooth is turned off on this PC", "Bluetooth выключен на этом ПК"),
    ("SetupBluetoothNoAdapter", "No Bluetooth adapter: the phone will connect over Wi‑Fi only",
     "Адаптера Bluetooth нет — телефон будет подключаться только по Wi‑Fi"),
    ("SetupBluetoothDisabled", "Turned off in the companion settings (bluetooth: false)",
     "Выключен в настройках компаньона (bluetooth: false)"),
    ("SetupOpenBluetoothSettings", "Open Bluetooth settings", "Открыть параметры Bluetooth"),
]

PROTO = [
    ("VlcWindowNotFound", "VLC window not found", "Окно VLC не найдено"),
    ("DisplayFallbackName", "Display {0}", "Экран {0}"),
    ("DisplayBuiltIn", "Built-in display", "Встроенный экран"),
    ("DisplayNotConnected", "This display is not connected", "Этот экран не подключён"),
    ("VlcNotFoundInstall", "VLC was not found on the PC. Install VLC 3.x or set its path in the companion settings.",
     "VLC не найден на компьютере. Установите VLC 3.x или укажите путь в настройках компаньона."),
    ("VlcStartFailed", "Could not start VLC", "Не удалось запустить VLC"),
    ("VlcExitedImmediately", "VLC exited right after starting (code {0}). One-instance mode may be on.",
     "VLC завершился сразу после запуска (код {0}). Возможно, включён режим одного экземпляра."),
    ("VlcCloseFailed", "Could not close VLC", "Не удалось закрыть VLC"),
    ("VlcNotInstalled", "VLC is not installed on the PC", "VLC не установлен на компьютере"),
    ("VlcNoControl", "VLC is open without phone control; restart it from the app",
     "VLC открыт без управления с телефона — перезапустите его из приложения"),
    ("VlcStarting", "VLC is starting", "VLC запускается"),
    ("VlcNotRunning", "VLC is not running", "VLC не запущен"),
    ("MoonlightNotInstalled", "Moonlight is not installed on the PC", "Moonlight не установлен на компьютере"),
    ("MoonlightNoHost", "Moonlight on the PC is not paired with a computer to stream from. Pair it in Moonlight first.",
     "Moonlight на компьютере не сопряжён ни с одним компьютером для трансляции. Сначала выполните сопряжение в Moonlight."),
    ("MoonlightStartFailed", "Moonlight could not start the stream (code {0})", "Moonlight не смог начать трансляцию (код {0})"),
    ("FolderNotFound", "Folder not found: {0}", "Папка не найдена: {0}"),
    ("FolderUnavailable", "Folder is not available: {0}", "Папка недоступна: {0}"),
    ("HistoryNotAFile", "Only files on this PC are kept in the watch history: {0}",
     "В истории просмотра хранятся только файлы на этом компьютере: {0}"),
]

HEAD = """<?xml version="1.0" encoding="utf-8"?>
<!-- Generated by companion/tools/gen_localization.py — edit the tables there, not this file. -->
<root>
  <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>
  <resheader name="version"><value>2.0</value></resheader>
  <resheader name="reader"><value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
  <resheader name="writer"><value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
"""


def write(rel, text):
    path = os.path.join(ROOT, rel)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


def resx(rows, col):
    body = "".join(f'  <data name="{r[0]}" xml:space="preserve"><value>{escape(r[1 + col])}</value></data>\n' for r in rows)
    return HEAD + body + "</root>\n"


def arg_count(text):
    nums = [int(n) for n in re.findall(r"\{(\d+)\}", text)]
    return max(nums) + 1 if nums else 0


def doc(text):
    return escape(text.replace("\n", " "))


def gen(rows, base):
    for col, lang in enumerate(LANGS):
        write(f"{base}{'.' + lang if lang else ''}.resx", resx(rows, col))


gen(UI, "companion/Sofabird.Companion/Localization/Strings")
gen(PROTO, "companion/Sofabird.Core/Localization/ProtocolText")

lines = [
    "// <auto-generated>companion/tools/gen_localization.py</auto-generated>",
    "using System.Globalization;",
    "using System.Resources;",
    "",
    "namespace Sofabird.Companion.Localization;",
    "",
    "/// <summary>Companion UI strings (tray, windows, notifications) in the Windows UI language; English fallback.</summary>",
    "internal static class UiText",
    "{",
    '    private static readonly ResourceManager Rm = new("Sofabird.Companion.Localization.Strings", typeof(UiText).Assembly);',
    "",
    "    private static string S(string key) => Rm.GetString(key, CultureInfo.CurrentUICulture) ?? key;",
    "    private static string F(string key, params object[] args) => string.Format(CultureInfo.CurrentCulture, S(key), args);",
    "",
]
for key, en, *_ in UI:
    n = arg_count(en)
    lines.append(f"    /// <summary>{doc(en)}</summary>")
    if n:
        params = ", ".join(f"object a{i}" for i in range(n))
        args = ", ".join(f"a{i}" for i in range(n))
        lines.append(f'    public static string {key}({params}) => F("{key}", {args});')
    else:
        lines.append(f'    public static string {key} => S("{key}");')
lines.append("}")
write("companion/Sofabird.Companion/Localization/UiText.cs", "\n".join(lines) + "\n")

lines = [
    "// <auto-generated>companion/tools/gen_localization.py</auto-generated>",
    "namespace Sofabird.Core.Localization;",
    "",
    "/// <summary>Messages sent to the phone in <c>err.msg</c>, rendered in the phone's language (<c>hello.device.locale</c>).</summary>",
    "public static class ProtocolText",
    "{",
]
for key, en, *_ in PROTO:
    n = arg_count(en)
    lines.append(f"    /// <summary>{doc(en)}</summary>")
    if n:
        params = ", ".join(f"object a{i}" for i in range(n))
        args = ", ".join(f"a{i}" for i in range(n))
        lines.append(f'    public static LocalizableText {key}({params}) => new("{key}", {args});')
    else:
        lines.append(f'    public static LocalizableText {key} => new("{key}");')
lines.append("}")
write("companion/Sofabird.Core/Localization/ProtocolText.cs", "\n".join(lines) + "\n")
print(f"generated: {len(UI)} UI strings, {len(PROTO)} protocol strings, languages: {', '.join(l or 'en' for l in LANGS)}")
