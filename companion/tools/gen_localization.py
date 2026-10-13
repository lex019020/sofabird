#!/usr/bin/env python3
"""Generates companion localization: .resx (English neutral + Russian) and typed C# accessors.

Edit the tables below, then run:  python companion/tools/gen_localization.py
Outputs:
  companion/VlcRemote.Companion/Localization/Strings{,.ru}.resx + UiText.cs   (PC UI: Windows UI language)
  companion/VlcRemote.Core/Localization/ProtocolText{,.ru}.resx + ProtocolText.cs   (err.msg: phone language)
To add a language, add a column here (and a Strings.<lang>.resx / ProtocolText.<lang>.resx is produced).
"""
import os
import re
from xml.sax.saxutils import escape

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
LANGS = ["", "ru"]  # "" = neutral (English)

UI = [
    ("AlreadyRunning", "VLC Remote Companion is already running (see the tray icon).", "VLC Remote Companion уже запущен (иконка в трее)."),
    ("FatalError", "VLC Remote Companion: fatal error.\n{0}", "VLC Remote Companion: критическая ошибка.\n{0}"),
    ("PortFailed", "Could not open port {0}: {1}\nChange wsPort in {2}.", "Не удалось открыть порт {0}: {1}\nИзмените wsPort в {2}."),
    ("DisplayPrimary", "{0} (primary, {1}×{2})", "{0} (основной, {1}×{2})"),
    ("DisplayNormal", "{0} ({1}×{2})", "{0} ({1}×{2})"),
    ("PairingTitle", "Pair a phone", "Сопряжение телефона"),
    ("PairingAddress", "Enter this code in the app on your phone. PC address: {0}", "Введите код в приложении на телефоне. Адрес ПК: {0}"),
    ("PairingHeaderGeneric", "VLC Remote pairing code", "Код сопряжения VLC Remote"),
    ("PairingHeaderDevice", "Phone “{0}” wants to connect", "Телефон «{0}» хочет подключиться"),
    ("PairingTimer", "The code is valid for {0}. Click to close this window.", "Код действует ещё {0}. Нажмите, чтобы закрыть окно."),
    ("PairingDone", "Phone “{0}” connected", "Телефон «{0}» подключён"),
    ("PairingClosing", "This window will close in a few seconds", "Окно закроется через несколько секунд"),
    ("TrayPhonesConnected", "Phones connected: {0}", "Телефонов подключено: {0}"),
    ("TrayTipPhones", "Phones: {0}", "Телефонов: {0}"),
    ("TrayPair", "Pair a phone…", "Сопряжение телефона…"),
    ("TrayStartVlc", "Start VLC", "Запустить VLC"),
    ("TrayRestartVlc", "Restart VLC with phone control", "Перезапустить VLC с управлением с телефона"),
    ("TrayQuitVlc", "Close VLC", "Закрыть VLC"),
    ("TrayMoveToDisplay", "Move VLC to display", "Перенести VLC на экран"),
    ("TrayInstallLua", "Install Lua pages", "Установить Lua-страницы"),
    ("TrayInstallLuaPending", "Install Lua pages (after VLC closes)", "Установить Lua-страницы (после закрытия VLC)"),
    ("TrayAutostart", "Start with Windows", "Автозапуск"),
    ("TrayKeepAwake", "Keep the PC awake during a movie", "Не давать ПК уснуть во время фильма"),
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
    ("BalloonLuaInstalled", "Lua pages were installed to %APPDATA%\\vlc\\lua\\http. They take effect the next time VLC starts.",
     "Lua-страницы установлены в %APPDATA%\\vlc\\lua\\http. Они заработают при следующем запуске VLC."),
    ("BalloonLuaDeferred", "VLC is running: the pages will be installed automatically after it closes.",
     "VLC запущен — страницы будут установлены автоматически после его закрытия."),
    ("BalloonLuaFailed", "Could not install the Lua pages (see the log).", "Не удалось установить Lua-страницы (см. журнал)."),
    ("BalloonAutostartFailed", "Could not change autostart: {0}", "Не удалось изменить автозапуск: {0}"),
]

PROTO = [
    ("VlcWindowNotFound", "VLC window not found", "Окно VLC не найдено"),
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
    ("FolderNotFound", "Folder not found: {0}", "Папка не найдена: {0}"),
    ("FolderUnavailable", "Folder is not available: {0}", "Папка недоступна: {0}"),
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


gen(UI, "companion/VlcRemote.Companion/Localization/Strings")
gen(PROTO, "companion/VlcRemote.Core/Localization/ProtocolText")

lines = [
    "// <auto-generated>companion/tools/gen_localization.py</auto-generated>",
    "using System.Globalization;",
    "using System.Resources;",
    "",
    "namespace VlcRemote.Companion.Localization;",
    "",
    "/// <summary>Companion UI strings (tray, windows, notifications) in the Windows UI language; English fallback.</summary>",
    "internal static class UiText",
    "{",
    '    private static readonly ResourceManager Rm = new("VlcRemote.Companion.Localization.Strings", typeof(UiText).Assembly);',
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
write("companion/VlcRemote.Companion/Localization/UiText.cs", "\n".join(lines) + "\n")

lines = [
    "// <auto-generated>companion/tools/gen_localization.py</auto-generated>",
    "namespace VlcRemote.Core.Localization;",
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
write("companion/VlcRemote.Core/Localization/ProtocolText.cs", "\n".join(lines) + "\n")
print(f"generated: {len(UI)} UI strings, {len(PROTO)} protocol strings, languages: {', '.join(l or 'en' for l in LANGS)}")
