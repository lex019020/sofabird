using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VlcRemote.Core.Localization;
using VlcRemote.Core.Protocol;
using VlcRemote.Core.Session;

namespace VlcRemote.Core.Displays;

/// <summary>
/// Monitor selection for the VLC window: <c>listDisplays</c> / <c>moveToDisplay</c> / <c>preferredDisplay</c>
/// (protocol/README.md, "sys"), the remembered preferred monitor, the one-time placement of every new VLC
/// window on it and the recovery after a monitor change (<see cref="OnTopologyChanged"/>).
/// Win32 specifics live behind <see cref="IMonitorSource"/> and <see cref="IVlcWindowHost"/>.
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
    // Monitor change recovery: the topology and the VLC window's monitor as of the last handled change.
    private IReadOnlyList<MonitorInfo>? _lastTopology;
    private string? _lastVlcMonitorId;
    private bool _awaitingPreferred;
    private bool _fullscreenWhenLost;
    private DateTimeOffset? _topologyChangedAt;
    private CancellationTokenSource? _topologyCts;

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

    /// <summary>
    /// After placing a manually opened VLC window (e.g. a double-clicked movie) on the preferred monitor, switch the
    /// video to fullscreen there (setting <c>launchFullscreen</c>). Null means never.
    /// </summary>
    public Func<bool>? FullscreenAfterManualPlacement { get; init; }
    /// <summary>How long to wait for VLC to become controllable and show video before giving up on fullscreen.</summary>
    public TimeSpan FullscreenWaitTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan FullscreenPollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Windows rebuilds the desktop in several steps (a monitor goes, the primary one moves): the reaction waits until
    /// the changes stop for this long.
    /// </summary>
    public TimeSpan TopologySettleDelay { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>See <see cref="TopologyRecovery.WasFullscreen"/>; covers the 2 s status polling while paused.</summary>
    public TimeSpan FullscreenMemory { get; init; } = TimeSpan.FromSeconds(5);
    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;

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
        var monitor = MonitorTopology.FindByGdi(monitors ?? _monitors.GetMonitors(), _vlc.GetWindowMonitor(window));
        // Remembered for the next monitor change; not while one is being handled (Windows has already moved the window).
        lock (_gate)
        {
            if (monitor is not null && _topologyChangedAt is null) _lastVlcMonitorId = monitor.Id;
        }
        return monitor;
    }

    /// <summary>
    /// The set of monitors changed (called by the monitor source, on any thread). After <see cref="TopologySettleDelay"/>
    /// without further changes the VLC window is brought back to the preferred monitor — restored and fullscreen again
    /// if the change knocked it out of fullscreen — or playback is paused when that monitor went away.
    /// </summary>
    public void OnTopologyChanged()
    {
        var cts = MarkTopologyChanged();
        _ = Task.Run(async () =>
        {
            try { await HandleTopologyChangeAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log.LogWarning(ex, "Handling the monitor change failed"); }
        });
    }

    /// <summary>Notes the time of the first change of a burst and cancels the pending reaction (the burst goes on).</summary>
    internal CancellationTokenSource MarkTopologyChanged()
    {
        lock (_gate)
        {
            _topologyChangedAt ??= Clock();
            _topologyCts?.Cancel();
            return _topologyCts = new CancellationTokenSource();
        }
    }

    /// <summary>Remembers the current monitors as the baseline for the next change (at startup).</summary>
    public void CaptureTopology()
    {
        var monitors = _monitors.GetMonitors();
        lock (_gate) _lastTopology ??= monitors;
    }

    internal async Task<TopologyReaction> HandleTopologyChangeAsync(CancellationToken ct)
    {
        await _delay(TopologySettleDelay, ct).ConfigureAwait(false);
        var after = _monitors.GetMonitors();
        DateTimeOffset changedAt;
        TopologyInput input;
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            changedAt = _topologyChangedAt ?? Clock();
            _topologyChangedAt = null;
            input = new TopologyInput(_prefs.PreferredId, _lastVlcMonitorId, _lastTopology ?? after, after, _awaitingPreferred);
            _lastTopology = after;
        }
        var reaction = TopologyRecovery.Decide(input);
        _log.LogInformation("Monitors changed ({Before} → {After}): {Reaction}", input.Before.Count, after.Count, reaction);
        switch (reaction)
        {
            case TopologyReaction.Restore:
            {
                // Back after it was lost: fullscreen as it was then (minutes may have passed).
                var wasFullscreen = input.AwaitingPreferred ? _fullscreenWhenLost : TopologyRecovery.WasFullscreen(_vlc.LastFullscreenAt, changedAt, FullscreenMemory);
                lock (_gate) _awaitingPreferred = false;
                await RestoreVlcWindowAsync(MonitorTopology.FindById(after, input.PreferredId)!, wasFullscreen, ct).ConfigureAwait(false);
                break;
            }
            case TopologyReaction.PreferredLost:
                lock (_gate)
                {
                    // A second loss in a row (another monitor went too) keeps what was known at the first one.
                    if (!_awaitingPreferred) _fullscreenWhenLost = TopologyRecovery.WasFullscreen(_vlc.LastFullscreenAt, changedAt, FullscreenMemory);
                    _awaitingPreferred = true;
                }
                if (_vlc.ProcessId is not null && _vlc.IsControllable && _vlc.IsPlaying && !_vlc.PlaybackHeld)
                {
                    await _vlc.PauseAsync(ct).ConfigureAwait(false);
                    _log.LogInformation("The display with VLC is gone: playback paused until it is back");
                }
                break;
        }
        RaiseChanged();
        return reaction;
    }

    private async Task RestoreVlcWindowAsync(MonitorInfo target, bool wasFullscreen, CancellationToken ct)
    {
        if (_vlc.ProcessId is not int pid) return;
        var window = _vlc.FindMainWindow(pid);
        if (window == 0) return;
        // A minimized window is restored only if the change knocked it out of fullscreen (or it has to move anyway):
        // one the user minimized on purpose stays minimized.
        var onTarget = string.Equals(_vlc.GetWindowMonitor(window), target.GdiDeviceName, StringComparison.OrdinalIgnoreCase);
        var restored = (wasFullscreen || !onTarget) && _vlc.RestoreWindow(window);
        var moved = !onTarget && await MoveVlcWindowAsync(window, target, ct).ConfigureAwait(false);
        var fullscreenAgain = false;
        if (wasFullscreen && _vlc.IsControllable && await _vlc.GetFullscreenAsync(ct).ConfigureAwait(false) == false)
        {
            await _vlc.SetFullscreenAsync(true, ct).ConfigureAwait(false);
            fullscreenAgain = true;
        }
        lock (_gate) _lastVlcMonitorId = target.Id;
        _log.LogInformation("VLC window after the monitor change: on {Name}{Restored}{Moved}{Fullscreen}", target.Name,
            restored ? ", restored" : "", moved ? ", moved" : "", fullscreenAgain ? ", fullscreen again" : "");
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
        // Choosing a display from the phone also brings a minimized VLC back into view there.
        if (_vlc.RestoreWindow(window)) _log.LogInformation("moveToDisplay: VLC window restored");
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
            // A manually opened movie on the chosen monitor should play fullscreen, like a launch from the phone.
            if (targetId is null && decision is PlacementDecision.Move or PlacementDecision.AlreadyThere
                && FullscreenAfterManualPlacement?.Invoke() == true)
            {
                await EnsureFullscreenAsync(pid, ct).ConfigureAwait(false);
            }
            return decision;
        }
        finally
        {
            lock (_gate) _pendingPids.Remove(pid);
        }
    }

    /// <summary>
    /// Waits until VLC <paramref name="pid"/> is controllable and has a video output, then makes it fullscreen once.
    /// Returns whether fullscreen was reached; gives up after <see cref="FullscreenWaitTimeout"/> or if VLC exits.
    /// </summary>
    internal async Task<bool> EnsureFullscreenAsync(int pid, CancellationToken ct)
    {
        var attempts = Math.Max(1, (int)Math.Ceiling(FullscreenWaitTimeout / FullscreenPollInterval));
        var requested = false;
        for (var i = 0; i < attempts; i++)
        {
            // While the resume question holds VLC paused, keep waiting (the video output may appear only once playback
            // goes on); the full timeout starts over after the answer.
            if (_vlc.PlaybackHeld && _vlc.IsControllable) i = 0;
            // Right after a cold start the manager may not have adopted this process yet (ProcessId is null),
            // so only stop when it follows a different VLC.
            if (_vlc.ProcessId is int followed && followed != pid)
            {
                _log.LogInformation("VLC {Pid}: another VLC ({Other}) is followed now; fullscreen skipped", pid, followed);
                return false;
            }
            if (_vlc.IsControllable)
            {
                bool? fs = null;
                try { fs = await _vlc.GetFullscreenAsync(ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogDebug("Fullscreen state: {Error}", ex.Message); }
                if (fs == true)
                {
                    if (requested) _log.LogInformation("VLC {Pid}: switched to fullscreen on the chosen monitor", pid);
                    return true;
                }
                if (fs == false)
                {
                    // Without a video output yet (the file is still opening) VLC ignores the request, so retry.
                    try { await _vlc.SetFullscreenAsync(true, ct).ConfigureAwait(false); requested = true; }
                    catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogDebug("Fullscreen request: {Error}", ex.Message); }
                }
            }
            await _delay(FullscreenPollInterval, ct).ConfigureAwait(false);
        }
        _log.LogInformation("VLC {Pid}: did not become controllable/fullscreen within {Seconds} s", pid, FullscreenWaitTimeout.TotalSeconds);
        return false;
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
