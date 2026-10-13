using System.Diagnostics;
using Microsoft.Extensions.Logging;
using VlcRemote.Companion.Net;
using VlcRemote.Companion.Services;
using VlcRemote.Companion.Vlc;
using VlcRemote.Core.Pairing;
using VlcRemote.Core.Protocol;

namespace VlcRemote.Companion.Tray;

/// <summary>Контекст приложения без главного окна: иконка в трее и контекстное меню.</summary>
public sealed class TrayContext : ApplicationContext
{
    private readonly AppPaths _paths;
    private readonly Settings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly VlcManager _vlc;
    private readonly PairingService _pairing;
    private readonly DisplayService _display;
    private readonly SessionManager _sessions;
    private readonly CompanionHost _host;
    private readonly PowerService _power;
    private readonly Func<Task> _shutdown;
    private readonly ILogger _log;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _phonesItem;
    private readonly ToolStripMenuItem _pairItem;
    private readonly ToolStripMenuItem _launchItem;
    private readonly ToolStripMenuItem _restartItem;
    private readonly ToolStripMenuItem _quitVlcItem;
    private readonly ToolStripMenuItem _displaysItem;
    private readonly ToolStripMenuItem _luaItem;
    private readonly ToolStripMenuItem _autostartItem;
    private readonly ToolStripMenuItem _keepAwakeItem;
    private readonly Icon _iconActive;
    private readonly Icon _iconIdle;
    private readonly Control _ui;
    private PairingForm? _pairingForm;
    private bool _exiting;

    public TrayContext(Control uiMarshaller, AppPaths paths, Settings settings, SettingsStore settingsStore, VlcManager vlc,
        PairingService pairing, DisplayService display, SessionManager sessions, CompanionHost host, PowerService power,
        Func<Task> shutdown, ILogger<TrayContext> log)
    {
        _ui = uiMarshaller;
        _paths = paths;
        _settings = settings;
        _settingsStore = settingsStore;
        _vlc = vlc;
        _pairing = pairing;
        _display = display;
        _sessions = sessions;
        _host = host;
        _power = power;
        _shutdown = shutdown;
        _log = log;

        _iconActive = TrayIconFactory.Create(active: true);
        _iconIdle = TrayIconFactory.Create(active: false);

        _statusItem = new ToolStripMenuItem("VLC: …") { Enabled = false };
        _phonesItem = new ToolStripMenuItem("Телефонов подключено: 0") { Enabled = false };
        _pairItem = new ToolStripMenuItem("Сопряжение телефона…", null, (_, _) => BeginPairing());
        _launchItem = new ToolStripMenuItem("Запустить VLC", null, (_, _) => RunAsync(ct => _vlc.LaunchAsync(null, _settings.PreferredDisplay, ct)));
        _restartItem = new ToolStripMenuItem("Перезапустить VLC с веб-интерфейсом", null, (_, _) => RunAsync(ct => _vlc.RestartAsync(null, ct)));
        _quitVlcItem = new ToolStripMenuItem("Закрыть VLC", null, (_, _) => RunAsync(_vlc.QuitAsync));
        _displaysItem = new ToolStripMenuItem("Перенести VLC на экран");
        _luaItem = new ToolStripMenuItem("Установить Lua-страницы", null, (_, _) => InstallLuaPages());
        _autostartItem = new ToolStripMenuItem("Автозапуск", null, (_, _) => ToggleAutostart()) { CheckOnClick = false };
        _keepAwakeItem = new ToolStripMenuItem("Не давать ПК уснуть во время фильма", null, (_, _) => ToggleKeepAwake());
        var logItem = new ToolStripMenuItem("Открыть журнал", null, (_, _) => OpenLogs());
        var settingsItem = new ToolStripMenuItem("Открыть папку настроек", null, (_, _) => OpenFolder(_paths.DataDir));
        var exitItem = new ToolStripMenuItem("Выход", null, (_, _) => Exit());

        _menu = new ContextMenuStrip();
        _menu.Items.AddRange(
        [
            _statusItem, _phonesItem, new ToolStripSeparator(),
            _pairItem, new ToolStripSeparator(),
            _launchItem, _restartItem, _quitVlcItem, _displaysItem, new ToolStripSeparator(),
            _luaItem, _autostartItem, _keepAwakeItem, new ToolStripSeparator(),
            logItem, settingsItem, new ToolStripSeparator(),
            exitItem,
        ]);
        _menu.Opening += (_, _) => RefreshMenu();

        _icon = new NotifyIcon
        {
            Icon = _iconIdle,
            Text = "VLC Remote Companion",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => BeginPairing();

        _vlc.RunStateChanged += () => Invoke(RefreshStatus);
        _vlc.StateChanged += s => Invoke(() => OnState(s));
        _sessions.CountChanged += () => Invoke(RefreshStatus);
        _pairing.Paired += d => Invoke(() => OnPaired(d));
        _pairing.PairingEnded += () => Invoke(OnPairingEnded);

        RefreshStatus();
    }

    private void Invoke(Action action)
    {
        if (_exiting || _ui.IsDisposed) return;
        try
        {
            if (_ui.InvokeRequired) _ui.BeginInvoke(action);
            else action();
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private void OnState(StateMessage state)
    {
        _power.OnPlaybackState(state.Playback.State == "playing");
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        var vlc = _vlc.RunState switch
        {
            VlcRunState.NotInstalled => "VLC не найден",
            VlcRunState.NotRunning => "VLC не запущен",
            VlcRunState.Starting => "VLC запускается…",
            VlcRunState.RunningNoApi => "VLC запущен без веб-интерфейса",
            VlcRunState.Running => $"VLC {_vlc.Info.Version ?? ""} — {PlaybackText()}".TrimEnd(),
            _ => "VLC: ?",
        };
        _statusItem.Text = vlc;
        _phonesItem.Text = $"Телефонов подключено: {_sessions.Count}";
        _icon.Icon = _vlc.RunState == VlcRunState.Running ? _iconActive : _iconIdle;
        var tip = $"VLC Remote {AppVersion.Informational}\n{vlc}\nТелефонов: {_sessions.Count}";
        _icon.Text = tip.Length > 127 ? tip[..127] : tip;
    }

    private string PlaybackText()
    {
        var s = _vlc.LastState;
        if (s is null) return "подключён";
        var state = s.Playback.State switch { "playing" => "воспроизведение", "paused" => "пауза", _ => "остановлен" };
        var title = s.Item?.Title;
        return string.IsNullOrEmpty(title) ? state : $"{state}: {Truncate(title, 40)}";
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    private void RefreshMenu()
    {
        RefreshStatus();
        var running = _vlc.RunState == VlcRunState.Running;
        _launchItem.Enabled = _vlc.RunState is VlcRunState.NotRunning;
        _restartItem.Visible = _vlc.RunState is VlcRunState.RunningNoApi or VlcRunState.Running;
        _quitVlcItem.Enabled = _vlc.IsProcessRunning;
        _displaysItem.Enabled = running;
        _displaysItem.DropDownItems.Clear();
        foreach (var d in _display.ListDisplays())
        {
            var item = new ToolStripMenuItem(d.Name) { Checked = _settings.PreferredDisplay == d.Index, Tag = d.Index };
            var index = d.Index;
            item.Click += (_, _) => RunAsync(ct => _host.MoveToDisplayAsync(index, ct));
            _displaysItem.DropDownItems.Add(item);
        }
        _luaItem.Text = _vlc.LuaInstallPending ? "Установить Lua-страницы (после закрытия VLC)" : "Установить Lua-страницы";
        _luaItem.Enabled = _vlc.Installation is not null;
        _autostartItem.Checked = AutostartService.IsEnabled();
        _keepAwakeItem.Checked = _settings.KeepAwake;
        _pairItem.Enabled = _pairingForm is null;
    }

    private void BeginPairing()
    {
        if (_pairingForm is not null && !_pairingForm.IsDisposed)
        {
            _pairingForm.Activate();
            return;
        }
        var code = _pairing.BeginPairing();
        var address = $"{NetworkUtil.GetPrimaryLanAddress() ?? Environment.MachineName}:{_settings.WsPort}";
        var screen = (_settings.PreferredDisplay is int idx ? _display.GetScreen(idx) : null) ?? Screen.PrimaryScreen ?? Screen.AllScreens[0];
        _pairingForm = new PairingForm(code, address, _pairing.ExpiresAt ?? DateTimeOffset.UtcNow.AddMinutes(5), screen);
        _pairingForm.FormClosed += (_, _) =>
        {
            _pairingForm = null;
            if (_pairing.IsPairingActive) _pairing.CancelPairing();
        };
        _pairingForm.Show();
        _pairingForm.Activate();
        _log.LogInformation("Pairing window shown at {Address}", address);
    }

    private void OnPaired(PairedDevice device)
    {
        _sessions.PublishEvent(new EventMessage { Name = EventNames.Paired, Device = device.DeviceName });
        if (_pairingForm is { IsDisposed: false }) _pairingForm.ShowPaired(device.DeviceName);
        _icon.ShowBalloonTip(3000, "VLC Remote", $"Телефон «{device.DeviceName ?? "?"}» сопряжён", ToolTipIcon.Info);
    }

    private void OnPairingEnded()
    {
        // Истёк срок/исчерпаны попытки — окно закроется само по таймеру; при успехе ShowPaired уже вызван.
    }

    private void InstallLuaPages()
    {
        var installed = _vlc.TryInstallLuaPages(initial: false, force: true);
        if (installed)
            _icon.ShowBalloonTip(3000, "VLC Remote", "Lua-страницы установлены в %APPDATA%\\vlc\\lua\\http. Они заработают при следующем запуске VLC.", ToolTipIcon.Info);
        else if (_vlc.LuaInstallPending)
            _icon.ShowBalloonTip(4000, "VLC Remote", "VLC запущен — страницы будут установлены автоматически после его закрытия.", ToolTipIcon.Warning);
        else
            _icon.ShowBalloonTip(4000, "VLC Remote", "Не удалось установить Lua-страницы (см. журнал).", ToolTipIcon.Error);
    }

    private void ToggleAutostart()
    {
        try
        {
            AutostartService.SetEnabled(!AutostartService.IsEnabled());
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Autostart toggle failed");
            _icon.ShowBalloonTip(3000, "VLC Remote", "Не удалось изменить автозапуск: " + ex.Message, ToolTipIcon.Error);
        }
    }

    private void ToggleKeepAwake()
    {
        _settings.KeepAwake = !_settings.KeepAwake;
        _settingsStore.Save(_settings);
        _power.KeepAwakeEnabled = _settings.KeepAwake;
        _power.OnPlaybackState(_vlc.LastState?.Playback.State == "playing");
        _vlc.Poke();
    }

    private void OpenLogs() => OpenFolder(_paths.LogsDir);

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose();
        }
        catch
        {
            // ignore
        }
    }

    private void RunAsync(Func<CancellationToken, Task> action)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await action(cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Tray action failed");
                Invoke(() => _icon.ShowBalloonTip(4000, "VLC Remote", ex.Message, ToolTipIcon.Warning));
            }
            Invoke(RefreshStatus);
        });
    }

    /// <summary>Потокобезопасный запрос на выход (например, от <c>--shutdown</c>).</summary>
    public void RequestExit()
    {
        try { _ui.BeginInvoke(Exit); } catch (InvalidOperationException) { Environment.Exit(0); }
    }

    private void Exit()
    {
        if (_exiting) return;
        _exiting = true;
        _icon.Visible = false;
        _pairingForm?.Close();
        _ = Task.Run(async () =>
        {
            try { await _shutdown().ConfigureAwait(false); }
            catch (Exception ex) { _log.LogWarning(ex, "Shutdown failed"); }
            try { _ui.BeginInvoke(ExitThread); } catch { Environment.Exit(0); }
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _icon.Dispose();
            _menu.Dispose();
            _iconActive.Dispose();
            _iconIdle.Dispose();
        }
        base.Dispose(disposing);
    }
}
