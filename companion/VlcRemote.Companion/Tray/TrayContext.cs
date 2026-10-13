using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using VlcRemote.Companion.Net;
using VlcRemote.Companion.Services;
using VlcRemote.Companion.Setup;
using VlcRemote.Core.Vlc;
using VlcRemote.Companion.Vlc;
using VlcRemote.Core.Displays;
using VlcRemote.Core.Pairing;
using VlcRemote.Core.Power;
using VlcRemote.Core.Setup;
using VlcRemote.Companion.Localization;

namespace VlcRemote.Companion.Tray;

/// <summary>Application context without a main window: the tray icon and the context menu.</summary>
public sealed class TrayContext : ApplicationContext
{
    private readonly AppPaths _paths;
    private readonly Settings _settings;
    private readonly VlcManager _vlc;
    private readonly PairingService _pairing;
    private readonly DisplayService _display;
    private readonly SessionManager _sessions;
    private readonly CompanionHost _host;
    private readonly BluetoothRfcommServer _bluetooth;
    private readonly SetupBackend _setup;
    private readonly PowerPlanService _powerPlan;
    private readonly System.Windows.Forms.Timer _powerTimer = new() { Interval = (int)TimeSpan.FromMinutes(30).TotalMilliseconds };
    private string? _powerWarned;
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
    private SetupForm? _setupForm;
    private Action? _balloonClick;
    private bool _exiting;

    public TrayContext(Control uiMarshaller, AppPaths paths, Settings settings, VlcManager vlc,
        PairingService pairing, DisplayService display, SessionManager sessions, CompanionHost host,
        BluetoothRfcommServer bluetooth, SetupBackend setup, PowerPlanService powerPlan, Func<Task> shutdown, ILogger<TrayContext> log)
    {
        _powerPlan = powerPlan;
        _ui = uiMarshaller;
        _paths = paths;
        _settings = settings;
        _vlc = vlc;
        _pairing = pairing;
        _display = display;
        _sessions = sessions;
        _host = host;
        _bluetooth = bluetooth;
        _setup = setup;
        _shutdown = shutdown;
        _log = log;

        _iconActive = TrayIconFactory.Create(active: true);
        _iconIdle = TrayIconFactory.Create(active: false);

        _statusItem = new ToolStripMenuItem("VLC: …") { Enabled = false };
        _phonesItem = new ToolStripMenuItem(UiText.TrayPhonesConnected(0)) { Enabled = false };
        _bluetoothItem = new ToolStripMenuItem(BluetoothText()) { Enabled = false };
        _pairItem = new ToolStripMenuItem(UiText.TrayPair, null, (_, _) => BeginPairing());
        _launchItem = new ToolStripMenuItem(UiText.TrayStartVlc, null, (_, _) => RunAsync(ct => _vlc.LaunchAsync(null, null, ct)));
        _restartItem = new ToolStripMenuItem(UiText.TrayRestartVlc, null, (_, _) => RestartVlc());
        _quitVlcItem = new ToolStripMenuItem(UiText.TrayQuitVlc, null, (_, _) => RunAsync(_vlc.QuitAsync));
        _displaysItem = new ToolStripMenuItem(UiText.TrayMoveToDisplay);
        _luaItem = new ToolStripMenuItem(UiText.TrayInstallLua, null, (_, _) => InstallLuaPages());
        _autostartItem = new ToolStripMenuItem(UiText.TrayAutostart, null, (_, _) => ToggleAutostart()) { CheckOnClick = false };
        _keepAwakeItem = new ToolStripMenuItem(UiText.TrayKeepAwake, null, (_, _) => ToggleKeepAwake());
        var setupItem = new ToolStripMenuItem(UiText.TraySetup, null, (_, _) => ShowSetup(firstRun: false));
        var logItem = new ToolStripMenuItem(UiText.TrayOpenLog, null, (_, _) => OpenLogs());
        var settingsItem = new ToolStripMenuItem(UiText.TrayOpenSettings, null, (_, _) => OpenFolder(_paths.DataDir));
        var exitItem = new ToolStripMenuItem(UiText.TrayExit, null, (_, _) => Exit());

        _menu = new ContextMenuStrip();
        _menu.Items.AddRange(
        [
            _statusItem, _phonesItem, _bluetoothItem, new ToolStripSeparator(),
            _pairItem, new ToolStripSeparator(),
            _launchItem, _restartItem, _quitVlcItem, _displaysItem, new ToolStripSeparator(),
            setupItem, _luaItem, _autostartItem, _keepAwakeItem, new ToolStripSeparator(),
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
        _vlc.StateChanged += _ => Invoke(RefreshStatus);
        _sessions.CountChanged += () => Invoke(RefreshStatus);
        _bluetooth.StatusChanged += () => Invoke(RefreshStatus);
        _pairing.Paired += d => Invoke(() => OnPaired(d));
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
        _launchItem.Enabled = _vlc.RunState is VlcRunState.NotRunning;
        _restartItem.Visible = _vlc.RunState is VlcRunState.RunningNoApi or VlcRunState.Running;
        _quitVlcItem.Enabled = _vlc.IsProcessRunning;
        RefreshDisplaysMenu();
        _luaItem.Text = _vlc.LuaInstallPending ? UiText.TrayInstallLuaPending : UiText.TrayInstallLua;
        _luaItem.Enabled = _vlc.Installation is not null;
        _autostartItem.Checked = AutostartService.IsEnabled();
        _keepAwakeItem.Checked = _settings.KeepAwake;
        _pairItem.Enabled = _pairingForm is null;
    }

    /// <summary>
    /// "Move VLC to display ▸": a click on a monitor moves VLC there (if running) and remembers it — every new VLC
    /// window opens there (same as the phone). The check mark is the preferred monitor; "Don't manage" clears it.
    /// </summary>
    private void RefreshDisplaysMenu()
    {
        _displaysItem.DropDownItems.Clear();
        _displaysItem.DropDownItems.Add(new ToolStripMenuItem(UiText.TrayDisplayHint) { Enabled = false });
        IReadOnlyList<MonitorInfo> monitors;
        try { monitors = _display.GetMonitors(); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Listing monitors failed");
            monitors = [];
        }
        var preferred = _host.Displays.PreferredId;
        var preferredConnected = false;
        foreach (var m in monitors)
        {
            var name = m.DisplayName(CultureInfo.CurrentUICulture);
            if (m.Kind == MonitorKind.Internal && m.NameText is null) name = UiText.TrayDisplayBuiltIn(name);
            var isPreferred = preferred is not null && string.Equals(preferred, m.Id, StringComparison.OrdinalIgnoreCase);
            preferredConnected |= isPreferred;
            var item = new ToolStripMenuItem(name) { Checked = isPreferred };
            var id = m.Id;
            item.Click += (_, _) => RunAsync(ct => _host.Displays.MoveToDisplayAsync(id, null, ct));
            _displaysItem.DropDownItems.Add(item);
        }
        if (preferred is not null && !preferredConnected)
        {
            var name = _settings.PreferredDisplayName ?? preferred;
            _displaysItem.DropDownItems.Add(new ToolStripMenuItem(UiText.TrayDisplayDisconnected(name)) { Checked = true, Enabled = false });
        }
        _displaysItem.DropDownItems.Add(new ToolStripSeparator());
        var unmanaged = new ToolStripMenuItem(UiText.TrayDisplayUnmanaged) { Checked = preferred is null };
        unmanaged.Click += (_, _) => _host.Displays.SetPreferred(null);
        _displaysItem.DropDownItems.Add(unmanaged);
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
            form.Close(); // the code in the window is no longer valid (attempts used up)
        }
        ShowPairingForm(_pairing.BeginPairing(), requestingDevice: null);
    }

    /// <summary>A phone without a token knocked without a code (<see cref="CompanionHost.RequestPairing"/>).</summary>
    private void OnPairingPrompt(PairingPrompt prompt)
    {
        if (_pairingForm is { IsDisposed: false } stale && prompt.Action == PairingPromptAction.ShowNew)
        {
            // The window has a stale code (attempts used up) — replace the window with a new one.
            stale.Close();
        }
        if (_pairingForm is { IsDisposed: false } form)
        {
            form.SetRequester(prompt.DeviceName);
            form.BringToFrontTopMost();
            _log.LogInformation("Pairing window brought to front for {Device}", prompt.DeviceName ?? "?");
            return;
        }
        // No window: show the active code (or the one created for this request).
        var code = _pairing.ActiveCode ?? prompt.Code;
        if (code is null) return; // the code has already expired — the phone will knock again
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
            // Cancel only our own code: the window may have been closed while being replaced by a window with a new code.
            if (_pairing.ActiveCode == code) _pairing.CancelPairing();
        };
        form.Show();
        form.BringToFrontTopMost();
        _log.LogInformation("Pairing window shown at {Address} on {Screen} (requested by {Device})",
            address, screen.DeviceName, requestingDevice ?? "tray");
    }

    /// <summary>Screen for the pairing code window: where the VLC window is, else the preferred monitor (TV), else the primary.</summary>
    private Screen PickPairingScreen()
    {
        Screen? screen = null;
        try
        {
            var monitors = _display.GetMonitors();
            screen = DisplayService.GetScreen(_host.Displays.FindVlcMonitor(monitors))
                ?? DisplayService.GetScreen(MonitorTopology.FindById(monitors, _host.Displays.PreferredId));
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Monitor for the pairing window not found");
        }
        return screen ?? Screen.PrimaryScreen ?? Screen.AllScreens[0];
    }

    private void OnNoControl()
    {
        ShowBalloon(6000, UiText.BalloonNoControl,
            ToolTipIcon.Warning, () =>
            {
                // The notification may have sat in the notification center — restart only if VLC is still without control.
                if (_vlc.RunState == VlcRunState.RunningNoApi) RestartVlc();
            });
    }

    /// <summary>The setup window is due at startup and the firewall needs nothing first: show it once the tray runs.</summary>
    public void ShowSetupOnStart() => _ui.BeginInvoke(() => ShowSetup(firstRun: true));

    /// <summary>
    /// Every start: if a firewall rule blocks the companion or VLC (e.g. "Cancel" in the Windows prompt), or the allow
    /// rules disappeared after setup was completed, offer to fix it (a click opens the setup window).
    /// </summary>
    public void CheckFirewallOnStart()
    {
        if (!_settings.ManageFirewall) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            var status = _setup.InspectFirewall();
            var notice = SetupPolicy.Decide(_settings.SetupCompleted, _settings.ManageFirewall, status);
            if (notice == StartupNotice.None) return;
            _log.LogWarning("Firewall needs attention: {Notice} ({Programs})", notice,
                string.Join(", ", status!.Programs.Where(p => !p.Ok).Select(p => $"{p.Program.Key}: allowed={p.Allowed}, blocking={p.BlockingRules.Count}")));
            Invoke(() => ShowBalloon(10000,
                notice == StartupNotice.FirewallBlocked ? UiText.BalloonFirewallBlocked : UiText.BalloonFirewallMissing,
                ToolTipIcon.Warning, () => ShowSetup(firstRun: false)));
        });
    }

    /// <summary>
    /// Media station: a Windows update or the user may change the power plan back (lid → sleep). Checked shortly
    /// after the start and every 30 minutes; each new set of changes is reported once, a click opens the setup window.
    /// </summary>
    public void WatchMediaStationPower()
    {
        _powerTimer.Tick += (_, _) => CheckMediaStationPower();
        _powerTimer.Start();
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Invoke(CheckMediaStationPower);
        });
    }

    private void CheckMediaStationPower()
    {
        if (_settings.MediaStation != true || _setupForm is { IsDisposed: false })
        {
            _powerWarned = null;
            return;
        }
        var deviations = _powerPlan.CheckMediaStation();
        if (deviations is null) return;
        if (deviations.Count == 0)
        {
            _powerWarned = null;
            return;
        }
        var signature = MediaStationPower.Signature(deviations);
        if (signature == _powerWarned) return;
        _powerWarned = signature;
        _log.LogWarning("Media station power settings were changed: {Deviations}", signature);
        ShowBalloon(10000, UiText.BalloonMediaStationDrift, ToolTipIcon.Warning, () => ShowSetup(firstRun: false));
    }

    private void ShowSetup(bool firstRun)
    {
        if (_exiting) return;
        if (_setupForm is { IsDisposed: false } open)
        {
            open.BringToFrontActivated();
            _ = open.RefreshStateAsync();
            return;
        }
        var form = _setupForm = new SetupForm(_setup, firstRun);
        form.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_setupForm, form)) _setupForm = null; // a modeless form disposes itself
        };
        form.Show();
        form.BringToFrontActivated();
        _log.LogInformation("Setup window shown ({Reason})", firstRun ? "first run" : "tray");
    }

    private void RestartVlc() => RunAsync(ct => _vlc.RestartAsync(null, null, ct));

    private void ShowBalloon(int timeoutMs, string text, ToolTipIcon icon, Action? onClick = null)
    {
        _balloonClick = onClick;
        _icon.ShowBalloonTip(timeoutMs, "VLC Remote", text, icon);
    }

    private void OnPaired(PairedDevice device)
    {
        if (_pairingForm is { IsDisposed: false }) _pairingForm.ShowPaired(device.DeviceName);
        ShowBalloon(3000, UiText.BalloonPaired(device.DeviceName ?? "?"), ToolTipIcon.Info);
    }

    private void InstallLuaPages()
    {
        var installed = _vlc.TryInstallLuaPages(force: true);
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

    private void ToggleKeepAwake() => _host.SetKeepAwake(!_settings.KeepAwake);

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

    /// <summary>Thread-safe request to exit (for example, from <c>--shutdown</c>).</summary>
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
        _setupForm?.CloseForExit();
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
            _powerTimer.Dispose();
            _icon.Dispose();
            _menu.Dispose();
            _iconActive.Dispose();
            _iconIdle.Dispose();
        }
        base.Dispose(disposing);
    }
}
