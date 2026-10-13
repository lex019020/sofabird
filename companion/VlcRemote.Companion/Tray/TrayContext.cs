using System.Diagnostics;
using Microsoft.Extensions.Logging;
using VlcRemote.Companion.Net;
using VlcRemote.Companion.Services;
using VlcRemote.Companion.Vlc;
using VlcRemote.Core.Pairing;
using VlcRemote.Core.Protocol;
using VlcRemote.Companion.Localization;

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
    private readonly BluetoothRfcommServer _bluetooth;
    private readonly Func<Task> _shutdown;
    private readonly ILogger _log;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _phonesItem;
    private readonly ToolStripMenuItem _bluetoothItem;
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
    private Action? _balloonClick;
    private bool _exiting;

    public TrayContext(Control uiMarshaller, AppPaths paths, Settings settings, SettingsStore settingsStore, VlcManager vlc,
        PairingService pairing, DisplayService display, SessionManager sessions, CompanionHost host, PowerService power,
        BluetoothRfcommServer bluetooth, Func<Task> shutdown, ILogger<TrayContext> log)
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
        _bluetooth = bluetooth;
        _shutdown = shutdown;
        _log = log;

        _iconActive = TrayIconFactory.Create(active: true);
        _iconIdle = TrayIconFactory.Create(active: false);

        _statusItem = new ToolStripMenuItem("VLC: …") { Enabled = false };
        _phonesItem = new ToolStripMenuItem(UiText.TrayPhonesConnected(0)) { Enabled = false };
        _bluetoothItem = new ToolStripMenuItem(BluetoothText()) { Enabled = false };
        _pairItem = new ToolStripMenuItem(UiText.TrayPair, null, (_, _) => BeginPairing());
        _launchItem = new ToolStripMenuItem(UiText.TrayStartVlc, null, (_, _) => RunAsync(ct => _vlc.LaunchAsync(null, _settings.PreferredDisplay, ct)));
        _restartItem = new ToolStripMenuItem(UiText.TrayRestartVlc, null, (_, _) => RestartVlc());
        _quitVlcItem = new ToolStripMenuItem(UiText.TrayQuitVlc, null, (_, _) => RunAsync(_vlc.QuitAsync));
        _displaysItem = new ToolStripMenuItem(UiText.TrayMoveToDisplay);
        _luaItem = new ToolStripMenuItem(UiText.TrayInstallLua, null, (_, _) => InstallLuaPages());
        _autostartItem = new ToolStripMenuItem(UiText.TrayAutostart, null, (_, _) => ToggleAutostart()) { CheckOnClick = false };
        _keepAwakeItem = new ToolStripMenuItem(UiText.TrayKeepAwake, null, (_, _) => ToggleKeepAwake());
        var logItem = new ToolStripMenuItem(UiText.TrayOpenLog, null, (_, _) => OpenLogs());
        var settingsItem = new ToolStripMenuItem(UiText.TrayOpenSettings, null, (_, _) => OpenFolder(_paths.DataDir));
        var exitItem = new ToolStripMenuItem(UiText.TrayExit, null, (_, _) => Exit());

        _menu = new ContextMenuStrip();
        _menu.Items.AddRange(
        [
            _statusItem, _phonesItem, _bluetoothItem, new ToolStripSeparator(),
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
        _icon.BalloonTipClicked += (_, _) =>
        {
            var action = _balloonClick;
            _balloonClick = null;
            action?.Invoke();
        };

        _vlc.RunStateChanged += () => Invoke(RefreshStatus);
        _vlc.StateChanged += s => Invoke(() => OnState(s));
        _sessions.CountChanged += () => Invoke(RefreshStatus);
        _bluetooth.StatusChanged += () => Invoke(RefreshStatus);
        _pairing.Paired += d => Invoke(() => OnPaired(d));
        _pairing.PairingEnded += () => Invoke(OnPairingEnded);
        _vlc.NoControlDetected += _ => Invoke(OnNoControl);
        _host.PairingPromptRequested += p => Invoke(() => OnPairingPrompt(p));

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
            VlcRunState.NotInstalled => UiText.VlcNotFound,
            VlcRunState.NotRunning => UiText.VlcNotRunning,
            VlcRunState.Starting => UiText.VlcStarting,
            VlcRunState.RunningNoApi => UiText.VlcNoControl,
            VlcRunState.Running => $"VLC {_vlc.Info.Version ?? ""} — {PlaybackText()}".TrimEnd(),
            _ => "VLC: ?",
        };
        _statusItem.Text = vlc;
        _phonesItem.Text = UiText.TrayPhonesConnected(_sessions.Count);
        _bluetoothItem.Text = BluetoothText();
        _icon.Icon = _vlc.RunState == VlcRunState.Running ? _iconActive : _iconIdle;
        var tip = $"VLC Remote {AppVersion.Informational}\n{vlc}\n{UiText.TrayTipPhones(_sessions.Count)}";
        _icon.Text = tip.Length > 127 ? tip[..127] : tip;
    }

    private string BluetoothText() => _bluetooth.Status switch
    {
        BluetoothServerStatus.Advertising => UiText.TrayBluetoothOn,
        BluetoothServerStatus.Starting => UiText.TrayBluetoothStarting,
        BluetoothServerStatus.RadioOff => UiText.TrayBluetoothRadioOff,
        BluetoothServerStatus.Disabled => UiText.TrayBluetoothDisabled,
        _ => UiText.TrayBluetoothUnavailable,
    };

    private string PlaybackText()
    {
        var s = _vlc.LastState;
        if (s is null) return UiText.PlaybackConnected;
        var state = s.Playback.State switch { "playing" => UiText.PlaybackPlaying, "paused" => UiText.PlaybackPaused, _ => UiText.PlaybackStopped };
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
        _luaItem.Text = _vlc.LuaInstallPending ? UiText.TrayInstallLuaPending : UiText.TrayInstallLua;
        _luaItem.Enabled = _vlc.Installation is not null;
        _autostartItem.Checked = AutostartService.IsEnabled();
        _keepAwakeItem.Checked = _settings.KeepAwake;
        _pairItem.Enabled = _pairingForm is null;
    }

    private void BeginPairing()
    {
        if (_pairingForm is { IsDisposed: false } form)
        {
            if (_pairing.ActiveCode is not null)
            {
                form.BringToFrontTopMost();
                return;
            }
            form.Close(); // код в окне уже недействителен (исчерпаны попытки)
        }
        ShowPairingForm(_pairing.BeginPairing(), requestingDevice: null);
    }

    /// <summary>Телефон без токена постучался без кода (<see cref="CompanionHost.RequestPairing"/>).</summary>
    private void OnPairingPrompt(PairingPrompt prompt)
    {
        if (_pairingForm is { IsDisposed: false } stale && prompt.Action == PairingPromptAction.ShowNew)
        {
            // В окне устаревший код (попытки исчерпаны) — заменяем окно новым.
            stale.Close();
        }
        if (_pairingForm is { IsDisposed: false } form)
        {
            form.SetRequester(prompt.DeviceName);
            form.BringToFrontTopMost();
            _log.LogInformation("Pairing window brought to front for {Device}", prompt.DeviceName ?? "?");
            return;
        }
        // Окна нет: показываем действующий код (или созданный для этого запроса).
        var code = _pairing.ActiveCode ?? prompt.Code;
        if (code is null) return; // код успел истечь — телефон постучится снова
        ShowPairingForm(code, prompt.DeviceName);
    }

    private void ShowPairingForm(string code, string? requestingDevice)
    {
        var address = $"{NetworkUtil.GetPrimaryLanAddress() ?? Environment.MachineName}:{_settings.WsPort}";
        var screen = PickPairingScreen();
        _pairingForm = new PairingForm(code, address, _pairing.ExpiresAt ?? DateTimeOffset.UtcNow.AddMinutes(5), screen, requestingDevice);
        var form = _pairingForm;
        form.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_pairingForm, form)) _pairingForm = null;
            // Отменяем только свой код: окно могли закрыть, заменяя его окном с новым кодом.
            if (_pairing.ActiveCode == code) _pairing.CancelPairing();
        };
        form.Show();
        form.BringToFrontTopMost();
        _log.LogInformation("Pairing window shown at {Address} on {Screen} (requested by {Device})",
            address, screen.DeviceName, requestingDevice ?? "tray");
    }

    /// <summary>Экран для окна с кодом: где сейчас окно VLC, иначе предпочитаемый (ТВ), иначе основной.</summary>
    private Screen PickPairingScreen()
    {
        Screen? screen = null;
        if (_vlc.ProcessId is int pid && _vlc.IsProcessRunning)
        {
            try
            {
                if (_display.FindDisplayOfProcess(pid) is int vlcDisplay) screen = _display.GetScreen(vlcDisplay);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Display of VLC window not found");
            }
        }
        screen ??= _settings.PreferredDisplay is int idx ? _display.GetScreen(idx) : null;
        return screen ?? Screen.PrimaryScreen ?? Screen.AllScreens[0];
    }

    private void OnNoControl()
    {
        ShowBalloon(6000, UiText.BalloonNoControl,
            ToolTipIcon.Warning, () =>
            {
                // Уведомление могло пролежать в центре уведомлений — перезапускаем, только если VLC всё ещё без управления.
                if (_vlc.RunState == VlcRunState.RunningNoApi) RestartVlc();
            });
    }

    private void RestartVlc() => RunAsync(ct => _vlc.RestartAsync(null, null, ct));

    private void ShowBalloon(int timeoutMs, string text, ToolTipIcon icon, Action? onClick = null)
    {
        _balloonClick = onClick;
        _icon.ShowBalloonTip(timeoutMs, "VLC Remote", text, icon);
    }

    private void OnPaired(PairedDevice device)
    {
        _sessions.PublishEvent(new EventMessage { Name = EventNames.Paired, Device = device.DeviceName });
        if (_pairingForm is { IsDisposed: false }) _pairingForm.ShowPaired(device.DeviceName);
        ShowBalloon(3000, UiText.BalloonPaired(device.DeviceName ?? "?"), ToolTipIcon.Info);
    }

    private void OnPairingEnded()
    {
        // Истёк срок/исчерпаны попытки — окно закроется само по таймеру; при успехе ShowPaired уже вызван.
    }

    private void InstallLuaPages()
    {
        var installed = _vlc.TryInstallLuaPages(initial: false, force: true);
        if (installed)
            ShowBalloon(3000, UiText.BalloonLuaInstalled, ToolTipIcon.Info);
        else if (_vlc.LuaInstallPending)
            ShowBalloon(4000, UiText.BalloonLuaDeferred, ToolTipIcon.Warning);
        else
            ShowBalloon(4000, UiText.BalloonLuaFailed, ToolTipIcon.Error);
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
            ShowBalloon(3000, UiText.BalloonAutostartFailed(ex.Message), ToolTipIcon.Error);
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
                Invoke(() => ShowBalloon(4000, ex.Message, ToolTipIcon.Warning));
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
