using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Sofabird.Core.History;
using Sofabird.Core.Lua;
using Sofabird.Core.Protocol;
using Sofabird.Core.State;
using Sofabird.Core.Vlc;

namespace Sofabird.Companion.Vlc;

/// <summary>A VLC process seen for the first time (<see cref="VlcManager.ProcessAppeared"/>).</summary>
/// <param name="Pid">Process id.</param>
/// <param name="Owned">Started by the companion (its window is placed by the launch itself).</param>
/// <param name="StartedBeforeCompanion">The process is older than the companion (left where the user put it).</param>
public sealed record VlcProcessAppeared(int Pid, bool Owned, bool StartedBeforeCompanion);

/// <summary>
/// Owns the VLC lifecycle: finds/starts the process, polls HTTP (500 ms during
/// playback / 2 s otherwise), keeps the RC socket for events, <c>adev</c> and the current tracks,
/// builds the <see cref="StateMessage"/> and publishes it on changes.
/// </summary>
public sealed partial class VlcManager : IAsyncDisposable
{
    private readonly Settings _settings;
    private readonly ILogger _log;
    private readonly ILoggerFactory _lf;
    private readonly LuaPagesInstaller? _lua;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    /// <summary>One launch/restart/quit at a time: two phones (or the tray and a phone) must not start two VLCs.</summary>
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

    private VlcHttpClient _http;
    private VlcRcClient _rc;
    private Task? _loop;
    private Process? _owned;
    private DateTimeOffset _ownedStartedAt;
    private VlcStatus? _lastStatus;
    private IReadOnlyList<VlcPlaylistItem>? _playlist;
    private int _playlistPlId = int.MinValue;
    private bool _playlistDirty = true;
    private VlcTracksPage? _tracksPage;
    private bool _tracksPageMissing;
    private RcTrackSelection? _rcTracks;
    private bool _rcTracksDirty = true;
    private string? _lastStateJson;
    private bool _muted;
    private int _savedVolume = 256;
    private bool _luaInstallPending;
    private bool _endReachedHint;
    private int _consecutiveHttpFailures;
    private CancellationTokenSource? _postOpenCts;
    private string? _lastAudioDevice;
    private readonly VlcrcConfigurator _vlcrc;
    private int? _noControlNotifiedPid;
    private int? _announcedPid;
    private volatile bool _launchInProgress;
    private readonly DateTime _companionStartedAt = Process.GetCurrentProcess().StartTime;
    private readonly HistoryTracker? _history;

    public VlcManager(Settings settings, VlcInstallation? installation, LuaPagesInstaller? lua, ILoggerFactory lf, HistoryTracker? history = null)
    {
        _settings = settings;
        _history = history;
        if (history is not null) history.Store.Changed += Poke;
        Installation = installation;
        _lua = lua;
        _lf = lf;
        _log = lf.CreateLogger<VlcManager>();
        _http = new VlcHttpClient("127.0.0.1", settings.VlcHttpPort, settings.VlcHttpPassword ?? "", logger: lf.CreateLogger<VlcHttpClient>());
        _rc = new VlcRcClient("127.0.0.1", settings.VlcRcPort, lf.CreateLogger<VlcRcClient>());
        _rc.StatusChanged += OnRcStatusChanged;
        RunState = installation is null ? VlcRunState.NotInstalled : VlcRunState.NotRunning;
        Info = new VlcInfo { Running = false, Status = ToStatus(RunState), Version = installation?.Version, Level = 2 };
        // Always created: ConfigureVlc can be turned on at run time from the setup window.
        _vlcrc = new VlcrcConfigurator(settings.VlcrcPath ?? VlcrcConfigurator.DefaultPath, lf.CreateLogger<VlcrcConfigurator>());
    }

    /// <summary><see cref="VlcRunState"/> → the protocol's <c>vlc.status</c>.</summary>
    public static string ToStatus(VlcRunState state) => state switch
    {
        VlcRunState.NotInstalled => VlcStatusNames.NotInstalled,
        VlcRunState.NotRunning => VlcStatusNames.NotRunning,
        VlcRunState.Starting => VlcStatusNames.Starting,
        VlcRunState.RunningNoApi => VlcStatusNames.NoControl,
        VlcRunState.Running => VlcStatusNames.Running,
        _ => VlcStatusNames.NotRunning,
    };

    public VlcInstallation? Installation { get; private set; }
    public VlcRunState RunState { get; private set; }
    public VlcInfo Info { get; }
    public StateMessage? LastState { get; private set; }
    public int? ProcessId { get; private set; }
    public bool LuaInstallPending => _luaInstallPending;
    /// <summary>The Lua pages answer (level 1 inside VLC).</summary>
    public bool TracksPageAvailable => !_tracksPageMissing && _tracksPage is not null;
    /// <summary>Watch history (resume prompt, next episode, track choices); <c>null</c> — not recorded.</summary>
    public HistoryTracker? History => _history;
    /// <summary>VLC is kept paused until a phone answers the resume question.</summary>
    public bool PlaybackHeld => _history?.IsHolding == true;

    /// <summary>Provider of the <c>system</c> block of the state (Windows volume, display, keepAwake).</summary>
    public Func<SystemInfo?>? SystemInfoProvider { get; set; }
    /// <summary>Base URL of the companion for <c>artUrl</c>.</summary>
    public Func<string?>? ArtBaseUrlProvider { get; set; }
    /// <summary>Provider of the <c>library</c> block of the state (revision, scanning).</summary>
    public Func<LibraryInfo?>? LibraryInfoProvider { get; set; }

    /// <summary><c>state.youtube</c> (YouTube in the TV browser); <c>null</c> — not reported.</summary>
    public Func<YouTubeInfo?>? YouTubeInfoProvider { get; set; }

    /// <summary>The revision of the YouTube history, added to <c>state.history.revision</c> (one number for both).</summary>
    public Func<long>? YouTubeHistoryRevision { get; set; }

    private long HistoryRevision => (_history?.Store.Revision ?? 0) + (YouTubeHistoryRevision?.Invoke() ?? 0);

    public event Action<StateMessage>? StateChanged;
    public event Action<EventMessage>? Event;

    /// <summary>
    /// What played reached its end: VLC stopped there, or went on to the next item of the playlist (an episode of a
    /// series) — the end of what was on the TV, for the sleep timer's "end of this".
    /// </summary>
    public event Action? ItemEnded;
    public event Action? RunStateChanged;
    /// <summary>VLC is open without control (does not answer HTTP with our password); once per process, the argument is the PID.</summary>
    public event Action<int>? NoControlDetected;
    /// <summary>A VLC process is seen for the first time (launched by the companion or manually). Raised on the poll thread.</summary>
    public event Action<VlcProcessAppeared>? ProcessAppeared;

    /// <summary>
    /// Monitor id a companion-launched VLC window will be moved to (the preferred monitor); <c>null</c> — the window is
    /// not managed.
    /// </summary>
    public Func<string?>? LaunchTargetResolver { get; set; }
    /// <summary>Places the window of a companion-launched process (pid, target monitor id); completes when it is done.</summary>
    public Func<int, string, CancellationToken, Task>? PlaceLaunchedWindowAsync { get; set; }

    public void Start()
    {
        if (_loop is not null) return;
        if (_settings.AutoInstallLuaPages) TryInstallLuaPages();
        TryConfigureVlcrc();
        _loop = Task.Run(() => PollLoopAsync(_cts.Token));
    }

    // ------------------------------------------------------------------ polling

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Poll failed");
            }

            var delay = VlcRunStatePolicy.PollDelay(RunState, _lastStatus?.IsPlaying == true);
            try
            {
                await _wake.WaitAsync(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Wake the polling loop (after a command or an RC event).</summary>
    public void Poke()
    {
        if (_wake.CurrentCount == 0)
        {
            try { _wake.Release(); } catch (SemaphoreFullException) { }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        await _pollGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var process = FindVlcProcess();
            if (process is null)
            {
                await OnNoProcessAsync(ct).ConfigureAwait(false);
                return;
            }

            ProcessId = process.Id;
            AnnounceProcess(process);
            VlcStatus status;
            try
            {
                status = await _http.GetStatusAsync(ct).ConfigureAwait(false);
                _consecutiveHttpFailures = 0;
            }
            catch (VlcException ex)
            {
                _consecutiveHttpFailures++;
                var startedRecently = (_owned is not null && !_owned.HasExited && DateTimeOffset.UtcNow - _ownedStartedAt < VlcRunStatePolicy.StartupGrace)
                    || StartedRecently(process, VlcRunStatePolicy.StartupGrace); // opened by hand, HTTP is still coming up
                if (VlcRunStatePolicy.AfterHttpFailure(RunState, _consecutiveHttpFailures, startedRecently) is not { } newState)
                {
                    _log.LogDebug("VLC HTTP request failed ({Count} in a row): {Error}", _consecutiveHttpFailures, ex.Message);
                    return;
                }
                if (RunState != newState)
                {
                    _log.LogInformation("VLC process {Pid} is running but HTTP is not available: {Error}", process.Id, ex.Message);
                    if (RunState == VlcRunState.Running)
                    {
                        _lastStatus = null;
                        await _rc.DisconnectAsync().ConfigureAwait(false);
                    }
                    SetRunState(newState);
                }
                if (newState == VlcRunState.RunningNoApi && _noControlNotifiedPid != process.Id)
                {
                    _noControlNotifiedPid = process.Id;
                    _log.LogWarning("VLC {Pid} was opened without phone control (HTTP interface with the companion password is not available)", process.Id);
                    try { NoControlDetected?.Invoke(process.Id); }
                    catch (Exception hex) { _log.LogWarning(hex, "NoControlDetected handler failed"); }
                }
                return;
            }

            if (RunState != VlcRunState.Running)
            {
                _log.LogInformation("VLC HTTP interface is up (version {Version}, api {Api})", status.Version, status.ApiVersion);
                Info.Version = NormalizeVersion(status.Version) ?? Info.Version;
                Info.ApiVersion = status.ApiVersion;
                SetRunState(VlcRunState.Running);
                _playlistDirty = true;
                _rcTracksDirty = true;
                _tracksPageMissing = false;
                RaiseEvent(new EventMessage { Name = EventNames.VlcStarted });
                _ = ConnectRcAsync(ct);
            }

            var prev = _lastStatus;
            _lastStatus = status;
            if (status.Fullscreen) LastFullscreenAt = DateTimeOffset.UtcNow;

            if (prev is not null && prev.CurrentPlId != status.CurrentPlId)
            {
                if (prev.Length > 0 && prev.Time >= prev.Length - 3) ItemEnded?.Invoke();
                _playlistDirty = true;
                _rcTracksDirty = true;
                _muted = false;
                if (status.CurrentPlId >= 0)
                    RaiseEvent(new EventMessage { Name = EventNames.InputChanged, Uri = _playlist?.FirstOrDefault(p => p.PlId == status.CurrentPlId)?.Uri });
            }

            var endReached = false;
            if (prev is not null && !prev.IsStopped && status.IsStopped)
            {
                var nearEnd = prev.Length > 0 && prev.Time >= prev.Length - 3;
                if (nearEnd || _endReachedHint)
                {
                    endReached = true;
                    RaiseEvent(new EventMessage { Name = EventNames.EndReached });
                    ItemEnded?.Invoke();
                }
            }
            _endReachedHint = false;

            if (_playlistDirty || _playlistPlId != status.CurrentPlId)
            {
                try
                {
                    _playlist = await _http.GetPlaylistAsync(ct).ConfigureAwait(false);
                    _playlistPlId = status.CurrentPlId;
                    _playlistDirty = false;
                }
                catch (VlcException ex)
                {
                    _log.LogDebug("playlist.json failed: {Error}", ex.Message);
                }
            }

            if (status.Information is not null && !_tracksPageMissing)
            {
                try
                {
                    _tracksPage = await _http.GetTracksAsync(ct).ConfigureAwait(false);
                    if (_tracksPage is null)
                    {
                        _tracksPageMissing = true;
                        _log.LogInformation("tracks.json is not installed in VLC (level 0 inside VLC); falling back to RC for current tracks");
                    }
                }
                catch (VlcException ex)
                {
                    _log.LogDebug("tracks.json failed: {Error}", ex.Message);
                }
            }
            else if (status.Information is null)
            {
                _tracksPage = null;
            }

            if (_tracksPageMissing && status.Information is not null && _rcTracksDirty && status.IsPlaying)
            {
                await RefreshRcTracksAsync(ct).ConfigureAwait(false);
            }
            else if (status.Information is null)
            {
                _rcTracks = null;
            }

            var state = BuildState(status);
            if (_history is not null)
            {
                var actions = _history.Observe(new PlaybackObservation(state.Item?.Uri, state.Item?.Title, state.Playback.State,
                    state.Playback.Time, state.Playback.Length, state.Tracks, endReached));
                if (actions.Count > 0) await RunHistoryActionsAsync(actions, ct).ConfigureAwait(false);
                state.Resume = _history.Resume;
                state.Next = _history.Next;
                state.History = new HistoryInfo { Revision = HistoryRevision };
            }
            PublishState(state);
        }
        finally
        {
            _pollGate.Release();
        }
    }

    /// <summary>Performs what the history asks for (pause for the resume question, track choices, events).</summary>
    private async Task RunHistoryActionsAsync(IReadOnlyList<HistoryAction> actions, CancellationToken ct)
    {
        foreach (var a in actions)
        {
            try
            {
                switch (a.Kind)
                {
                    case HistoryActionKind.Event when a.Event is not null:
                        RaiseEvent(a.Event);
                        break;
                    case HistoryActionKind.Pause:
                        _lastStatus = await Cmd("pl_forcepause", ct).ConfigureAwait(false);
                        break;
                    case HistoryActionKind.Play:
                        _lastStatus = await Cmd("pl_forceresume", ct).ConfigureAwait(false);
                        break;
                    case HistoryActionKind.Seek:
                        _lastStatus = await Cmd("seek", ct, ("val", Str((int)Math.Clamp(a.Seconds, 0, int.MaxValue)))).ConfigureAwait(false);
                        break;
                    case HistoryActionKind.SelectTrack:
                        await Cmd(a.TrackKind == "audio" ? "audio_track" : "subtitle_track", ct, ("val", Str(a.TrackId))).ConfigureAwait(false);
                        _rcTracksDirty = true;
                        break;
                }
            }
            catch (VlcException ex)
            {
                _log.LogWarning("History action {Kind} failed: {Error}", a.Kind, ex.Message);
            }
        }
        Poke();
    }

    /// <summary>Raise <see cref="ProcessAppeared"/> once per new VLC process (not while the companion is launching one).</summary>
    private void AnnounceProcess(Process process)
    {
        if (_announcedPid == process.Id || _launchInProgress) return;
        _announcedPid = process.Id;
        var owned = _owned is not null && _owned.Id == process.Id;
        bool startedBefore;
        try { startedBefore = process.StartTime < _companionStartedAt; }
        catch { startedBefore = false; }
        try { ProcessAppeared?.Invoke(new VlcProcessAppeared(process.Id, owned, startedBefore)); }
        catch (Exception ex) { _log.LogWarning(ex, "ProcessAppeared handler failed"); }
    }

    private async Task OnNoProcessAsync(CancellationToken ct)
    {
        ProcessId = null;
        _announcedPid = null;
        if (_owned is not null)
        {
            try { _owned.Dispose(); } catch { }
            _owned = null;
        }
        var wasRunning = RunState is VlcRunState.Running or VlcRunState.RunningNoApi or VlcRunState.Starting;
        if (wasRunning)
        {
            _log.LogInformation("VLC exited");
            _lastStatus = null;
            _playlist = null;
            _tracksPage = null;
            _rcTracks = null;
            _muted = false;
            SetRunState(Installation is null ? VlcRunState.NotInstalled : VlcRunState.NotRunning);
            await _rc.DisconnectAsync().ConfigureAwait(false);
            RaiseEvent(new EventMessage { Name = EventNames.VlcExited });
            if (_history is not null)
                foreach (var a in _history.OnVlcExited())
                    if (a.Event is not null) RaiseEvent(a.Event);
            // VLC is closed and will no longer overwrite vlcrc — write the interfaces for the next start.
            TryConfigureVlcrc();
        }
        if (_luaInstallPending) TryInstallLuaPages();
        // Always rebuild: the system block (preferred display, volume) may change while VLC is closed; duplicates are dropped.
        PublishState(BuildState(null));
    }

    private Process? FindVlcProcess()
    {
        if (_owned is not null)
        {
            try
            {
                if (!_owned.HasExited) return _owned;
            }
            catch { }
        }
        var all = VlcLocator.RunningProcesses();
        Process? result = null;
        foreach (var p in all)
        {
            if (result is null) result = p;
            else p.Dispose();
        }
        return result;
    }

    private StateMessage BuildState(VlcStatus? status)
    {
        return StateBuilder.Build(new StateInputs
        {
            Vlc = Info.Clone(),
            Status = status,
            TracksPage = _tracksPage,
            Playlist = _playlist,
            RcTracks = _rcTracks,
            System = SystemInfoProvider?.Invoke(),
            ArtBaseUrl = ArtBaseUrlProvider?.Invoke(),
            Muted = _muted,
            Resume = _history?.Resume,
            Next = _history?.Next,
            HistoryRevision = _history is null ? null : HistoryRevision,
            Library = LibraryInfoProvider?.Invoke(),
            YouTube = YouTubeInfoProvider?.Invoke(),
        });
    }

    private void PublishState(StateMessage state)
    {
        LastState = state;
        var json = ProtocolJson.Serialize(state.WithoutTimestamp());
        if (json == _lastStateJson) return;
        _lastStateJson = json;
        try { StateChanged?.Invoke(state); }
        catch (Exception ex) { _log.LogWarning(ex, "StateChanged handler failed"); }
    }

    private void RaiseEvent(EventMessage ev)
    {
        _log.LogInformation("Event {Name}", ev.Name);
        try { Event?.Invoke(ev); }
        catch (Exception ex) { _log.LogWarning(ex, "Event handler failed"); }
    }

    private static bool StartedRecently(Process process, TimeSpan window)
    {
        try { return DateTime.Now - process.StartTime < window; }
        catch { return false; }
    }

    private void SetRunState(VlcRunState s)
    {
        if (RunState == s) return;
        RunState = s;
        Info.Status = ToStatus(s);
        Info.Running = s == VlcRunState.Running;
        // For Running the state is published by the poll (with the status.json data).
        if (s != VlcRunState.Running) PublishState(BuildState(null));
        try { RunStateChanged?.Invoke(); }
        catch (Exception ex) { _log.LogWarning(ex, "RunStateChanged handler failed"); }
    }

    private static string? NormalizeVersion(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        var idx = v.IndexOf(' ');
        return idx > 0 ? v[..idx] : v; // "3.0.23 Vetinari" → "3.0.23"
    }

    // ------------------------------------------------------------------ dispose

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _postOpenCts?.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { }
        }
        _rc.StatusChanged -= OnRcStatusChanged;
        if (_history is not null)
        {
            _history.Store.Changed -= Poke;
            _history.OnVlcExited(); // save the position of what is playing now
        }
        await _rc.DisposeAsync().ConfigureAwait(false);
        _http.Dispose();
        _owned?.Dispose();
        _cts.Dispose();
        _pollGate.Dispose();
        _lifecycleGate.Dispose();
        _wake.Dispose();
    }
}
