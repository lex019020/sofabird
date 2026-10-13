using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VlcRemote.Core.Localization;
using VlcRemote.Core.Protocol;
using VlcRemote.Core.Session;

namespace VlcRemote.Core.Displays;

/// <summary>
/// Monitor selection for the VLC window: <c>listDisplays</c> / <c>moveToDisplay</c> / <c>preferredDisplay</c>
/// (protocol/README.md, "sys"), the remembered preferred monitor and the one-time placement of every new VLC
/// window on it. Win32 specifics live behind <see cref="IMonitorSource"/> and <see cref="IVlcWindowHost"/>.
/// </summary>
public sealed class DisplayController
{
    private readonly IMonitorSource _monitors;
    private readonly IDisplayPreferenceStore _prefs;
    private readonly IVlcWindowHost _vlc;
    private readonly ILogger _log;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly object _gate = new();
    private readonly HashSet<int> _claimedPids = new();
    private readonly HashSet<int> _pendingPids = new();
    private readonly HashSet<long> _handledWindows = new();

    public DisplayController(IMonitorSource monitors, IDisplayPreferenceStore prefs, IVlcWindowHost vlc,
        ILogger? log = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _monitors = monitors;
        _prefs = prefs;
        _vlc = vlc;
        _log = log ?? NullLogger.Instance;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>How long to wait for the main window of a new VLC process.</summary>
    public TimeSpan WindowWaitTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan WindowPollInterval { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>The preferred monitor or the VLC window's monitor changed — the state should be pushed.</summary>
    public event Action? Changed;

    public string? PreferredId => _prefs.PreferredId;

    public static bool Handles(string sysName) => sysName is "listDisplays" or "moveToDisplay" or "preferredDisplay";

    /// <summary>Execute a display <c>sys</c> command; returns the reply (<c>displays</c>) or <c>null</c> → <c>ok</c>.</summary>
    public async Task<Message?> ExecuteAsync(string name, CommandArgs args, CancellationToken ct)
    {
        switch (name)
        {
            case "listDisplays":
                return List();
            case "moveToDisplay":
            {
                var id = args.GetString("displayId");
                int? index = string.IsNullOrEmpty(id) ? args.GetInt("display") : null;
                if (string.IsNullOrEmpty(id) && index is null)
                    throw ProtocolException.BadRequest("moveToDisplay requires 'displayId' (or the legacy 'display' index)");
                await MoveToDisplayAsync(id, index, ct).ConfigureAwait(false);
                return null;
            }
            case "preferredDisplay":
                SetPreferred(args.GetString("displayId"));
                return null;
            default:
                throw ProtocolException.Unsupported($"unknown sys command '{name}'");
        }
    }

    public IReadOnlyList<MonitorInfo> GetMonitors() => _monitors.GetMonitors();

    public DisplaysMessage List()
    {
        var monitors = _monitors.GetMonitors();
        var current = FindVlcMonitor(monitors);
        var preferred = _prefs.PreferredId;
        return new DisplaysMessage
        {
            Items = monitors.Select(m => new DisplayEntry
            {
                Index = m.Index,
                Id = m.Id,
                Name = m.Name,
                NameText = m.NameText,
                Kind = m.KindName,
                Primary = m.Primary,
                Current = current is not null && current.Id == m.Id,
                Preferred = SameId(preferred, m.Id),
                Bounds = new DisplayBounds { X = m.Bounds.X, Y = m.Bounds.Y, Width = m.Bounds.Width, Height = m.Bounds.Height },
            }).ToList(),
            PreferredId = preferred,
        };
    }

    /// <summary>Monitor with the VLC main window; <c>null</c> — VLC is not running or has no window.</summary>
    public MonitorInfo? FindVlcMonitor(IReadOnlyList<MonitorInfo>? monitors = null)
    {
        if (_vlc.ProcessId is not int pid) return null;
        var window = _vlc.FindMainWindow(pid);
        if (window == 0) return null;
        return MonitorTopology.FindByGdi(monitors ?? _monitors.GetMonitors(), _vlc.GetWindowMonitor(window));
    }

    /// <summary>Remember the preferred monitor (<c>null</c>/empty — do not manage the VLC window). Does not move anything.</summary>
    public void SetPreferred(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) id = null;
        var monitor = MonitorTopology.FindById(_monitors.GetMonitors(), id);
        var name = id is null ? null : monitor?.Name ?? (SameId(id, _prefs.PreferredId) ? _prefs.PreferredName : null);
        id = monitor?.Id ?? id;
        if (string.Equals(id, _prefs.PreferredId, StringComparison.Ordinal) && string.Equals(name, _prefs.PreferredName, StringComparison.Ordinal))
            return;
        _prefs.SetPreferred(id, name);
        if (id is null) _log.LogInformation("Preferred display cleared: the VLC window is not managed");
        else _log.LogInformation("Preferred display: {Name} ({Id}){NotConnected}", name ?? "?", id, monitor is null ? " (not connected now)" : "");
        RaiseChanged();
    }

    /// <summary>
    /// Move the VLC window to a monitor (by id, or by the legacy index) and remember it as preferred.
    /// VLC not running — only remember. A not connected id is remembered, but a running VLC cannot be moved there.
    /// </summary>
    public async Task MoveToDisplayAsync(string? id, int? legacyIndex, CancellationToken ct)
    {
        var monitors = _monitors.GetMonitors();
        MonitorInfo target;
        if (!string.IsNullOrEmpty(id))
        {
            var found = MonitorTopology.FindById(monitors, id);
            if (found is null)
            {
                SetPreferred(id);
                if (_vlc.ProcessId is not null) throw ProtocolException.BadRequest(ProtocolText.DisplayNotConnected);
                return;
            }
            target = found;
        }
        else
        {
            target = MonitorTopology.FindByIndex(monitors, legacyIndex)
                ?? throw ProtocolException.BadRequest($"no display with index {legacyIndex}");
        }

        SetPreferred(target.Id);
        if (_vlc.ProcessId is not int pid)
        {
            _log.LogInformation("moveToDisplay: VLC is not running; {Name} will be used for the next VLC window", target.Name);
            return;
        }
        var window = _vlc.FindMainWindow(pid);
        if (window == 0)
        {
            lock (_gate)
            {
                // A new VLC whose window has not appeared yet: the one-time placement will use the new preference.
                if (_pendingPids.Contains(pid) || !_claimedPids.Contains(pid))
                {
                    _log.LogInformation("moveToDisplay: VLC {Pid} has no window yet; it will open on {Name}", pid, target.Name);
                    return;
                }
            }
            throw ProtocolException.VlcError(ProtocolText.VlcWindowNotFound);
        }
        lock (_gate) _handledWindows.Add(window);
        if (!await MoveVlcWindowAsync(window, target, ct).ConfigureAwait(false))
            throw ProtocolException.VlcError(ProtocolText.VlcWindowNotFound);
    }

    /// <summary>
    /// One-time placement of a new VLC process's window (launched by the companion or manually): waits for the main
    /// window and moves it to <paramref name="targetId"/> (or the preferred monitor) if that is connected. Each
    /// process and each window is handled once; later moves by the user are left alone.
    /// </summary>
    public async Task<PlacementDecision> PlaceNewWindowAsync(int pid, bool startedBeforeCompanion, string? targetId, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_claimedPids.Add(pid)) return PlacementDecision.AlreadyHandled;
            _pendingPids.Add(pid);
        }
        try
        {
            var decision = AutoPlacement.Decide(new PlacementInput(targetId ?? _prefs.PreferredId, _monitors.GetMonitors(),
                Window: 0, WindowMonitorGdi: null, HandledWindows: [], startedBeforeCompanion));
            if (decision != PlacementDecision.Move)
            {
                _log.LogInformation("VLC {Pid}: window placement skipped ({Decision})", pid, decision);
                return decision;
            }

            long window = 0;
            var attempts = Math.Max(1, (int)Math.Ceiling(WindowWaitTimeout / WindowPollInterval));
            for (var i = 0; i <= attempts; i++)
            {
                window = _vlc.FindMainWindow(pid);
                if (window != 0 || i == attempts) break;
                await _delay(WindowPollInterval, ct).ConfigureAwait(false);
            }
            if (window == 0)
            {
                _log.LogInformation("VLC {Pid}: no main window within {Seconds} s; not placed", pid, WindowWaitTimeout.TotalSeconds);
                return PlacementDecision.WindowNotFound;
            }

            // The preference may have changed while waiting.
            var preferred = targetId ?? _prefs.PreferredId;
            var monitors = _monitors.GetMonitors();
            long[] handled;
            lock (_gate) handled = [.. _handledWindows];
            var windowMonitor = _vlc.GetWindowMonitor(window);
            decision = AutoPlacement.Decide(new PlacementInput(preferred, monitors, window, windowMonitor, handled, StartedBeforeCompanion: false));
            lock (_gate) _handledWindows.Add(window);
            if (decision == PlacementDecision.Move)
            {
                var target = MonitorTopology.FindById(monitors, preferred)!;
                var ok = await MoveVlcWindowAsync(window, target, ct).ConfigureAwait(false);
                _log.LogInformation("VLC {Pid}: new window {Window:X} moved from {From} to {Name} ({Gdi}): {Ok}",
                    pid, window, windowMonitor ?? "?", target.Name, target.GdiDeviceName, ok);
            }
            else
            {
                _log.LogInformation("VLC {Pid}: new window {Window:X} on {Monitor} not moved ({Decision})", pid, window, windowMonitor ?? "?", decision);
            }
            return decision;
        }
        finally
        {
            lock (_gate) _pendingPids.Remove(pid);
        }
    }

    /// <summary>Unfullscreen (if VLC is controllable and fullscreen) → move → fullscreen again.</summary>
    private async Task<bool> MoveVlcWindowAsync(long window, MonitorInfo target, CancellationToken ct)
    {
        if (string.Equals(_vlc.GetWindowMonitor(window), target.GdiDeviceName, StringComparison.OrdinalIgnoreCase))
            return true;
        bool? fullscreen = null;
        if (_vlc.IsControllable)
        {
            try { fullscreen = await _vlc.GetFullscreenAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogDebug("Reading VLC fullscreen state failed: {Error}", ex.Message);
            }
        }
        if (fullscreen == true)
        {
            await _vlc.SetFullscreenAsync(false, ct).ConfigureAwait(false);
            await _delay(TimeSpan.FromMilliseconds(400), ct).ConfigureAwait(false);
        }
        // Not controllable (or fullscreen state unknown): the host moves a borderless fullscreen window as a whole.
        var ok = _vlc.MoveWindow(window, target);
        if (fullscreen == true)
        {
            await _delay(TimeSpan.FromMilliseconds(300), ct).ConfigureAwait(false);
            await _vlc.SetFullscreenAsync(true, ct).ConfigureAwait(false);
        }
        RaiseChanged();
        return ok;
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { _log.LogWarning(ex, "Display Changed handler failed"); }
    }

    private static bool SameId(string? a, string? b) => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
