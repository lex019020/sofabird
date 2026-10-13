using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using VlcRemote.Companion.Interop;
using VlcRemote.Core.History;
using VlcRemote.Core.Lua;
using VlcRemote.Core.Protocol;
using VlcRemote.Core.Session;
using VlcRemote.Core.State;
using VlcRemote.Core.Vlc;
using VlcRemote.Core.Localization;

namespace VlcRemote.Companion.Vlc;

/// <summary>A VLC process seen for the first time (<see cref="VlcManager.ProcessAppeared"/>).</summary>
/// <param name="Pid">Process id.</param>
/// <param name="Owned">Started by the companion (its window is placed by the launch itself).</param>
/// <param name="StartedBeforeCompanion">The process is older than the companion (left where the user put it).</param>
public sealed record VlcProcessAppeared(int Pid, bool Owned, bool StartedBeforeCompanion);

public enum VlcRunState
{
    /// <summary>vlc.exe was not found on disk.</summary>
    NotInstalled,
    /// <summary>There is no vlc.exe process.</summary>
    NotRunning,
    /// <summary>The process was started by the companion, the interfaces are still coming up.</summary>
    Starting,
    /// <summary>vlc.exe is running, but the HTTP interface does not answer with our password.</summary>
    RunningNoApi,
    /// <summary>Full control: HTTP (+RC).</summary>
    Running,
}

/// <summary>
/// Owns the VLC lifecycle: finds/starts the process, polls HTTP (500 ms during
/// playback / 2 s otherwise), keeps the RC socket for events, <c>adev</c> and the current tracks,
/// builds the <see cref="StateMessage"/> and publishes it on changes.
/// </summary>
public sealed class VlcManager : IAsyncDisposable
{
    private readonly Settings _settings;
    private readonly ILogger _log;
    private readonly ILoggerFactory _lf;
    private readonly LuaPagesInstaller? _lua;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);

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
    public bool Muted => _muted;
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

    public event Action<StateMessage>? StateChanged;
    public event Action<EventMessage>? Event;
    public event Action? RunStateChanged;
    /// <summary>VLC is open without control (does not answer HTTP with our password); once per process, the argument is the PID.</summary>
    public event Action<int>? NoControlDetected;
    /// <summary>A VLC process is seen for the first time (launched by the companion or manually). Raised on the poll thread.</summary>
    public event Action<VlcProcessAppeared>? ProcessAppeared;

    /// <summary>
    /// Monitor id a companion-launched VLC window will be moved to (argument: the legacy one-off <c>display</c> index
    /// of <c>launch</c>/<c>open</c>, or <c>null</c> for the preferred monitor); <c>null</c> — the window is not managed.
    /// </summary>
    public Func<int?, string?>? LaunchTargetResolver { get; set; }
    /// <summary>Places the window of a companion-launched process (pid, target monitor id); completes when it is done.</summary>
    public Func<int, string, CancellationToken, Task>? PlaceLaunchedWindowAsync { get; set; }

    public void Start()
    {
        if (_loop is not null) return;
        if (_settings.AutoInstallLuaPages) TryInstallLuaPages(initial: true);
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

            var delay = RunState == VlcRunState.Running && _lastStatus?.IsPlaying == true
                ? TimeSpan.FromMilliseconds(500)
                : RunState == VlcRunState.Starting ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromSeconds(2);
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
                // A one-off failure (VLC busy opening a file) does not count as a loss of control.
                if (RunState == VlcRunState.Running && _consecutiveHttpFailures < 3)
                {
                    _log.LogDebug("VLC HTTP request failed ({Count} in a row): {Error}", _consecutiveHttpFailures, ex.Message);
                    return;
                }
                var starting = (_owned is not null && !_owned.HasExited && DateTimeOffset.UtcNow - _ownedStartedAt < TimeSpan.FromSeconds(20))
                    || StartedRecently(process, TimeSpan.FromSeconds(20)); // opened by hand, HTTP is still coming up (the first start with a "stale plugins cache" takes ~12 s)
                var newState = starting ? VlcRunState.Starting : VlcRunState.RunningNoApi;
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
                state.History = new HistoryInfo { Revision = _history.Store.Revision };
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
        if (_luaInstallPending) TryInstallLuaPages(initial: false);
        // Always rebuild: the system block (preferred display, volume) may change while VLC is closed; duplicates are dropped.
        PublishState(BuildState(null));
        await Task.CompletedTask;
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
            Vlc = Info,
            Status = status,
            TracksPage = _tracksPage,
            Playlist = _playlist,
            RcTracks = _rcTracks,
            System = SystemInfoProvider?.Invoke(),
            ArtBaseUrl = ArtBaseUrlProvider?.Invoke(),
            Muted = _muted,
            Resume = _history?.Resume,
            Next = _history?.Next,
            HistoryRevision = _history?.Store.Revision,
        });
    }

    private void PublishState(StateMessage state)
    {
        LastState = state;
        var updatedAt = state.Playback.UpdatedAt;
        state.Playback.UpdatedAt = 0;
        var json = ProtocolJson.Serialize(state);
        state.Playback.UpdatedAt = updatedAt;
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

    // ------------------------------------------------------------------ RC

    private async Task ConnectRcAsync(CancellationToken ct)
    {
        try
        {
            await _rc.ConnectAsync(ct).ConfigureAwait(false);
        }
        catch (VlcException ex)
        {
            _log.LogInformation("RC not available: {Error}", ex.Message);
        }
    }

    private void OnRcStatusChanged(RcStatusChange change)
    {
        _log.LogDebug("RC: {Raw}", change.Raw);
        switch (change.Kind)
        {
            case RcStatusKind.StopState when change.Number == 0:
                _endReachedHint = true;
                break;
            case RcStatusKind.NewInput:
                _playlistDirty = true;
                _rcTracksDirty = true;
                break;
        }
        Poke();
    }

    private async Task RefreshRcTracksAsync(CancellationToken ct)
    {
        if (!_rc.IsConnected)
        {
            await ConnectRcAsync(ct).ConfigureAwait(false);
            if (!_rc.IsConnected) return;
        }
        try
        {
            var audio = await _rc.ListTracksAsync(VlcTrackKind.Audio, ct).ConfigureAwait(false);
            if (audio is null) return; // paused — RC refused, try later
            var spu = await _rc.ListTracksAsync(VlcTrackKind.Subtitle, ct).ConfigureAwait(false);
            var video = await _rc.ListTracksAsync(VlcTrackKind.Video, ct).ConfigureAwait(false);
            _rcTracks = new RcTrackSelection(
                audio.FirstOrDefault(t => t.Current)?.Id,
                spu?.FirstOrDefault(t => t.Current)?.Id,
                video?.FirstOrDefault(t => t.Current)?.Id);
            _rcTracksDirty = false;
        }
        catch (VlcException ex)
        {
            _log.LogDebug("RC track listing failed: {Error}", ex.Message);
        }
    }

    public async Task<IReadOnlyList<AudioDeviceEntry>> GetAudioDevicesAsync(CancellationToken ct)
    {
        EnsureRunning();
        var list = await _rc.ListAudioDevicesAsync(ct).ConfigureAwait(false);
        var result = list.Select(d => new AudioDeviceEntry { Id = d.Id, Name = d.Text, Current = d.Current }).ToList();
        _lastAudioDevice = result.FirstOrDefault(d => d.Current)?.Id;
        return result;
    }

    public async Task SetAudioDeviceAsync(string id, CancellationToken ct)
    {
        EnsureRunning();
        if (string.IsNullOrEmpty(id))
        {
            // RC cannot select "the default device" with an empty id; try through our Lua page.
            if (TracksPageAvailable)
            {
                var r = await _http.SetVarAsync("aout", "device", "", ct).ConfigureAwait(false);
                if (!r.Ok) throw ProtocolException.VlcError(r.Error ?? "setvar failed");
            }
            else
            {
                throw ProtocolException.Unsupported("selecting the default audio device requires Lua pages (tracks.json) installed in VLC");
            }
        }
        else
        {
            await _rc.SetAudioDeviceAsync(id, ct).ConfigureAwait(false);
        }
        _lastAudioDevice = id;
        Poke();
    }

    public string? LastAudioDevice => _lastAudioDevice;

    /// <summary>
    /// The output device chosen from the phone came back (e.g. the TV's HDMI audio after the TV was off): VLC fell back
    /// to another device meanwhile, so switch it back. Returns <c>true</c> if VLC was switched.
    /// </summary>
    public async Task<bool> RestoreAudioDeviceAsync(string id, CancellationToken ct)
    {
        if (RunState != VlcRunState.Running || string.IsNullOrEmpty(id)) return false;
        var devices = await GetAudioDevicesAsync(ct).ConfigureAwait(false);
        if (!devices.Any(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase))) return false;
        if (devices.Any(d => d.Current && string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase))) return false;
        await _rc.SetAudioDeviceAsync(id, ct).ConfigureAwait(false);
        _lastAudioDevice = id;
        Poke();
        return true;
    }

    /// <summary>When the status polling last saw VLC fullscreen.</summary>
    public DateTimeOffset? LastFullscreenAt { get; private set; }

    public bool IsPlaying => RunState == VlcRunState.Running && _lastStatus?.IsPlaying == true;

    public async Task PauseAsync(CancellationToken ct)
    {
        EnsureRunning();
        _lastStatus = await Cmd("pl_forcepause", ct).ConfigureAwait(false);
        Poke();
    }

    // ------------------------------------------------------------------ process control

    public bool IsProcessRunning => RunState is VlcRunState.Running or VlcRunState.RunningNoApi or VlcRunState.Starting;

    /// <summary>
    /// Start <c>vlc.exe</c> with the HTTP+RC interfaces. If VLC is already under control, open
    /// <paramref name="uri"/> over HTTP. If VLC is open without control (<see cref="VlcRunState.RunningNoApi"/>),
    /// restart it with the same file (the position is lost). If it is starting, wait for it.
    /// </summary>
    public async Task LaunchAsync(string? uri, int? displayIndex, CancellationToken ct)
    {
        Installation ??= VlcLocator.Find(_settings.VlcPath);
        if (Installation is null)
            throw ProtocolException.VlcNotRunning(ProtocolText.VlcNotFoundInstall);

        if (RunState == VlcRunState.Starting)
        {
            var wait = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
            while (RunState == VlcRunState.Starting && DateTimeOffset.UtcNow < wait)
            {
                Poke();
                await Task.Delay(300, ct).ConfigureAwait(false);
            }
        }

        switch (RunState)
        {
            case VlcRunState.Running:
                if (!string.IsNullOrEmpty(uri))
                    await OpenAsync(uri, enqueue: false, subtitle: null, audioTrack: null, subtitleTrack: null, ct).ConfigureAwait(false);
                return;
            case VlcRunState.RunningNoApi:
                await RestartAsync(uri, displayIndex, ct).ConfigureAwait(false);
                return;
            case VlcRunState.Starting:
                _log.LogWarning("launch: VLC is still starting after 20 s");
                return;
        }

        await StartProcessAsync(uri, displayIndex, ct).ConfigureAwait(false);
    }

    private async Task StartProcessAsync(string? uri, int? displayIndex, CancellationToken ct)
    {
        var installation = Installation ?? throw ProtocolException.VlcNotRunning(ProtocolText.VlcNotFoundInstall);
        if (_settings.AutoInstallLuaPages) TryInstallLuaPages(initial: false);
        TryConfigureVlcrc();

        // Window placement: the media is opened only after the window is on the target monitor, so the video
        // (fullscreen with LaunchFullscreen) starts there instead of flashing on another screen first.
        string? target = null;
        try { target = LaunchTargetResolver?.Invoke(displayIndex); }
        catch (Exception ex) { _log.LogWarning(ex, "Resolving the launch display failed"); }
        var deferredUri = target is not null && PlaceLaunchedWindowAsync is not null && !string.IsNullOrEmpty(uri) ? uri : null;

        var psi = new ProcessStartInfo
        {
            FileName = installation.ExePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = installation.InstallDir,
        };
        foreach (var a in BuildLaunchArgs(deferredUri is null ? uri : null, fullscreenFollowsWindow: target is not null))
            psi.ArgumentList.Add(a);

        _log.LogInformation("Launching VLC: {Exe} {Args}{Target}", psi.FileName, RedactArgs(psi.ArgumentList),
            target is null ? "" : $" (window -> {target}{(deferredUri is null ? "" : ", media after placement")})");
        Process process;
        Task? placement = null;
        _launchInProgress = true;
        try
        {
            process = Process.Start(psi) ?? throw ProtocolException.VlcError(ProtocolText.VlcStartFailed);
            _owned = process;
            _ownedStartedAt = DateTimeOffset.UtcNow;
            ProcessId = process.Id;
            if (target is not null && PlaceLaunchedWindowAsync is { } place)
                placement = place(process.Id, target, ct);
        }
        finally
        {
            _launchInProgress = false;
        }
        SetRunState(VlcRunState.Starting);
        Poke();

        // Wait for HTTP for up to 20 s.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (process.HasExited)
                throw ProtocolException.VlcError(ProtocolText.VlcExitedImmediately(process.ExitCode));
            if (RunState == VlcRunState.Running) break;
            await Task.Delay(300, ct).ConfigureAwait(false);
            Poke();
        }
        if (RunState != VlcRunState.Running)
            _log.LogWarning("VLC started but HTTP interface did not answer within 20 s");

        if (placement is not null)
        {
            try { await placement.ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "Placing the VLC window failed"); }
        }
        if (deferredUri is not null)
        {
            if (RunState == VlcRunState.Running)
                await OpenAsync(deferredUri, enqueue: false, subtitle: null, audioTrack: null, subtitleTrack: null, ct).ConfigureAwait(false);
            else
                _log.LogWarning("Cannot open {Uri}: VLC HTTP interface is not available", deferredUri);
        }
    }

    /// <param name="uri">Media to open (or <c>null</c>).</param>
    /// <param name="fullscreenFollowsWindow">
    /// The companion places the window itself: <c>--qt-fullscreen-screennumber=-1</c> overrides a screen number from
    /// <c>vlcrc</c>, so fullscreen video opens on the monitor with the main window (Qt's screen order differs from ours).
    /// </param>
    internal IEnumerable<string> BuildLaunchArgs(string? uri, bool fullscreenFollowsWindow)
    {
        yield return "--extraintf";
        yield return "http:rc";
        yield return "--http-host";
        yield return _settings.VlcHttpHost;
        yield return "--http-port";
        yield return _settings.VlcHttpPort.ToString(CultureInfo.InvariantCulture);
        yield return "--http-password";
        yield return _settings.VlcHttpPassword ?? "";
        yield return "--rc-host";
        yield return $"127.0.0.1:{_settings.VlcRcPort}";
        yield return "--rc-quiet";
        if (fullscreenFollowsWindow)
            yield return "--qt-fullscreen-screennumber=-1";
        if (_settings.LaunchFullscreen)
            yield return "--fullscreen";
        if (!string.IsNullOrWhiteSpace(_settings.VlcExtraArgs))
            foreach (var extra in SplitArgs(_settings.VlcExtraArgs))
                yield return extra;
        if (!string.IsNullOrEmpty(uri))
            yield return uri;
    }

    /// <summary>Command line for the log: the <c>--http-password</c> value is hidden.</summary>
    internal static string RedactArgs(IEnumerable<string> args)
    {
        var sb = new StringBuilder();
        var redactNext = false;
        foreach (var a in args)
        {
            if (sb.Length > 0) sb.Append(' ');
            if (redactNext) { sb.Append("***"); redactNext = false; continue; }
            if (a == "--http-password") redactNext = true;
            sb.Append(a.Contains(' ') ? $"\"{a}\"" : a);
        }
        return sb.ToString();
    }

    private static IEnumerable<string> SplitArgs(string s)
    {
        var sb = new StringBuilder();
        var quoted = false;
        foreach (var ch in s)
        {
            if (ch == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(ch) && !quoted)
            {
                if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
                continue;
            }
            sb.Append(ch);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    /// <summary>
    /// Close all VLC instances (including those open without control) and start again with control.
    /// Without <paramref name="uri"/> VLC opens with the same file: the current item (if VLC is under control)
    /// or the file from the process command line. The playback position is lost.
    /// </summary>
    public async Task RestartAsync(string? uri, int? displayIndex, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(uri))
        {
            uri = RunState == VlcRunState.Running ? LastState?.Item?.Uri : null;
            uri ??= ReadMediaFromRunningProcess();
        }
        _log.LogInformation("Restarting VLC with phone control; media: {Media}", string.IsNullOrEmpty(uri) ? "(none)" : uri);

        if (RunState == VlcRunState.Running)
        {
            try
            {
                await _rc.QuitAsync(ct).ConfigureAwait(false);
                await WaitForExitAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            }
            catch (VlcException ex)
            {
                _log.LogDebug("RC quit failed: {Error}", ex.Message);
            }
        }
        foreach (var p in VlcLocator.RunningProcesses())
        {
            try { p.CloseMainWindow(); } catch { }
            p.Dispose();
        }
        if (!await WaitForExitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false))
        {
            foreach (var p in VlcLocator.RunningProcesses())
            {
                _log.LogWarning("VLC {Pid} did not exit gracefully; killing", p.Id);
                try { p.Kill(); } catch { }
                p.Dispose();
            }
            if (!await WaitForExitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false))
                throw ProtocolException.VlcError(ProtocolText.VlcCloseFailed);
        }

        // The poll will see that there is no process (NotRunning) and write vlcrc; then a normal start.
        await PollOnceAsync(ct).ConfigureAwait(false);
        TryConfigureVlcrc();
        Installation ??= VlcLocator.Find(_settings.VlcPath);
        await StartProcessAsync(uri, displayIndex, ct).ConfigureAwait(false);
    }

    /// <summary>The file/URI from the command line of the running vlc.exe (for example, opened by double-clicking a file).</summary>
    private string? ReadMediaFromRunningProcess()
    {
        var procs = VlcLocator.RunningProcesses();
        try
        {
            foreach (var p in procs)
            {
                var cmd = ProcessCommandLine.TryGet(p.Id);
                if (cmd is null) continue;
                var media = VlcCommandLine.ExtractMedia(ProcessCommandLine.Split(cmd));
                _log.LogDebug("VLC {Pid} command line media: {Media}", p.Id, media ?? "(none)");
                if (media is not null) return media;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Reading VLC command line failed");
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
        return null;
    }

    /// <summary>Close VLC: RC <c>quit</c>, then <c>vlc://quit</c> over HTTP, then closing the window.</summary>
    public async Task QuitAsync(CancellationToken ct)
    {
        if (RunState == VlcRunState.Running)
        {
            try
            {
                await _rc.QuitAsync(ct).ConfigureAwait(false);
                if (await WaitForExitAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false)) return;
            }
            catch (VlcException ex)
            {
                _log.LogDebug("RC quit failed: {Error}", ex.Message);
            }
            try
            {
                await _http.CommandAsync("in_play", ct, ("input", "vlc://quit")).ConfigureAwait(false);
                if (await WaitForExitAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false)) return;
            }
            catch (VlcException ex)
            {
                _log.LogDebug("vlc://quit failed: {Error}", ex.Message);
            }
        }
        foreach (var p in VlcLocator.RunningProcesses())
        {
            try { p.CloseMainWindow(); } catch { }
            p.Dispose();
        }
        await WaitForExitAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        Poke();
    }

    private static async Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var procs = VlcLocator.RunningProcesses();
            var any = procs.Length > 0;
            foreach (var p in procs) p.Dispose();
            if (!any) return true;
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        return false;
    }

    // ------------------------------------------------------------------ vlcrc

    /// <summary>
    /// Write the web interface and RC with the companion's password into vlcrc (if <c>ConfigureVlc</c> is on).
    /// Only when no vlc.exe is running — otherwise VLC overwrites the file from memory.
    /// </summary>
    public VlcrcResult? TryConfigureVlcrc()
    {
        if (!_settings.ConfigureVlc || Installation is null) return null;
        try
        {
            var procs = VlcLocator.RunningProcesses();
            var running = procs.Length > 0;
            foreach (var p in procs) p.Dispose();
            if (running) return null;
            return _vlcrc.Ensure(new VlcrcSettings(_settings.VlcHttpPort, _settings.VlcHttpPassword ?? "", _settings.VlcRcPort));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Configuring {Path} failed", _vlcrc.Path);
            return null;
        }
    }

    /// <summary>
    /// Read-only: VLC is set up for phone control (vlcrc has our interfaces and password, Lua pages are current).
    /// <c>null</c> when VLC is not installed.
    /// </summary>
    public bool? IsConfiguredForPhone()
    {
        if (Installation is null) return null;
        var rc = _vlcrc.IsConfigured(new VlcrcSettings(_settings.VlcHttpPort, _settings.VlcHttpPassword ?? "", _settings.VlcRcPort));
        bool lua;
        try { lua = _lua is null || !_lua.NeedsInstall(Installation.Version); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { lua = false; }
        return rc && lua;
    }

    // ------------------------------------------------------------------ Lua pages

    /// <summary>Install the Lua pages if VLC is not running; otherwise postpone until it closes.</summary>
    public bool TryInstallLuaPages(bool initial, bool force = false)
    {
        if (_lua is null || Installation is null) return false;
        try
        {
            if (!force && !_lua.NeedsInstall(Installation.Version))
            {
                _luaInstallPending = false;
                return false;
            }
            var procs = VlcLocator.RunningProcesses();
            var running = procs.Length > 0;
            foreach (var p in procs) p.Dispose();
            if (running)
            {
                if (!_luaInstallPending)
                    _log.LogInformation("Lua pages need (re)install but VLC is running; deferring until it exits");
                _luaInstallPending = true;
                return false;
            }
            _lua.Install(Installation.Version);
            _luaInstallPending = false;
            _tracksPageMissing = false;
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Lua pages install failed");
            _luaInstallPending = false;
            return false;
        }
    }

    // ------------------------------------------------------------------ data requests

    private void EnsureRunning()
    {
        if (RunState != VlcRunState.Running)
            throw ProtocolException.VlcNotRunning(RunState switch
            {
                VlcRunState.NotInstalled => ProtocolText.VlcNotInstalled,
                VlcRunState.RunningNoApi => ProtocolText.VlcNoControl,
                VlcRunState.Starting => ProtocolText.VlcStarting,
                _ => ProtocolText.VlcNotRunning,
            });
    }

    public async Task<StateMessage> GetStateAsync(CancellationToken ct)
    {
        await PollOnceAsync(ct).ConfigureAwait(false);
        return LastState ?? BuildState(null);
    }

    public async Task<IReadOnlyList<PlaylistEntry>> GetPlaylistAsync(CancellationToken ct)
    {
        EnsureRunning();
        var items = await _http.GetPlaylistAsync(ct).ConfigureAwait(false);
        _playlist = items;
        _playlistDirty = false;
        return items.Select(i => new PlaylistEntry
        {
            PlId = i.PlId, Name = i.Name, Uri = i.Uri, DurationSec = i.Duration, Current = i.Current,
        }).ToList();
    }

    /// <summary>
    /// File browsing is done by the companion itself (without VLC): the library is available even when VLC is closed or open
    /// without control — the movie is chosen on the phone, and <c>open</c> starts VLC.
    /// </summary>
    public async Task<IReadOnlyList<BrowseEntry>> BrowseAsync(string uri, CancellationToken ct)
    {
        var browser = new LocalFileBrowser(() => _settings.MediaRoots);
        try
        {
            return await Task.Run(() =>
            {
                var items = browser.Browse(uri);
                _history?.Store.Annotate(items);
                return items;
            }, ct).ConfigureAwait(false);
        }
        catch (DirectoryNotFoundException)
        {
            throw ProtocolException.BadRequest(ProtocolText.FolderNotFound(LocalFileBrowser.ToLocalPath(uri)));
        }
        catch (IOException ex)
        {
            throw ProtocolException.BadRequest(ProtocolText.FolderUnavailable(ex.Message));
        }
    }

    public async Task<TracksInfo> GetTracksAsync(CancellationToken ct)
    {
        EnsureRunning();
        _rcTracksDirty = true;
        var state = await GetStateAsync(ct).ConfigureAwait(false);
        return state.Tracks ?? new TracksInfo();
    }

    public Task<VlcArt?> GetArtAsync(int? plId, CancellationToken ct)
    {
        EnsureRunning();
        return _http.GetArtAsync(plId, ct);
    }

    // ------------------------------------------------------------------ commands

    public async Task ExecuteCommandAsync(string name, CommandArgs args, CancellationToken ct)
    {
        switch (name)
        {
            case "open" when RunState != VlcRunState.Running && !(args.GetBool("enqueue") ?? false):
                // VLC is closed / starting / without control: start (or restart) it right away with this file.
                await LaunchAsync(args.RequireString("uri"), args.GetInt("display"), ct).ConfigureAwait(false);
                return;
            case "launch":
                await LaunchAsync(args.GetString("uri"), args.GetInt("display"), ct).ConfigureAwait(false);
                return;
            case "quit":
                if (!IsProcessRunning) return;
                await QuitAsync(ct).ConfigureAwait(false);
                return;
            case "dismissNext":
                _history?.DismissNext();
                Poke();
                return;
            case "resume":
            {
                var choice = args.RequireString("choice");
                if (_history is null) throw ProtocolException.Unsupported("watch history is off");
                var actions = _history.Answer(choice);
                if (actions.Count > 0)
                {
                    EnsureRunning();
                    await RunHistoryActionsAsync(actions, ct).ConfigureAwait(false);
                }
                Poke();
                return;
            }
        }

        EnsureRunning();
        VlcStatus? result = null;
        switch (name)
        {
            case "play":
            {
                var plId = args.GetInt("plId");
                if (plId is int id) result = await Cmd("pl_play", ct, ("id", Str(id))).ConfigureAwait(false);
                else if (_lastStatus?.State == "paused") result = await Cmd("pl_forceresume", ct).ConfigureAwait(false);
                else if (_lastStatus?.IsStopped != false) result = await Cmd("pl_play", ct).ConfigureAwait(false);
                break;
            }
            case "pause": result = await Cmd("pl_forcepause", ct).ConfigureAwait(false); break;
            case "togglePause": result = await Cmd("pl_pause", ct).ConfigureAwait(false); break;
            case "stop": result = await Cmd("pl_stop", ct).ConfigureAwait(false); break;
            case "next": result = await Cmd("pl_next", ct).ConfigureAwait(false); break;
            case "prev": result = await Cmd("pl_previous", ct).ConfigureAwait(false); break;
            case "seek":
            {
                if (args.GetInt("seconds") is int abs) result = await Cmd("seek", ct, ("val", Str(Math.Max(0, abs)))).ConfigureAwait(false);
                else if (args.GetInt("delta") is int delta) result = await Cmd("seek", ct, ("val", Signed(delta))).ConfigureAwait(false);
                else throw ProtocolException.BadRequest("seek requires 'seconds' or 'delta'");
                break;
            }
            case "volume":
            {
                if (args.GetInt("value") is int v)
                {
                    v = Math.Clamp(v, 0, 512);
                    result = await Cmd("volume", ct, ("val", Str(v))).ConfigureAwait(false);
                    if (v > 0) _muted = false;
                }
                else if (args.GetInt("delta") is int d)
                {
                    result = await Cmd("volume", ct, ("val", Signed(d))).ConfigureAwait(false);
                    if (result.Volume > 0) _muted = false;
                }
                else throw ProtocolException.BadRequest("volume requires 'value' or 'delta'");
                break;
            }
            case "mute":
            {
                var on = args.RequireBool("on");
                if (on && !_muted)
                {
                    var cur = _lastStatus?.Volume ?? 256;
                    if (cur > 0) _savedVolume = cur;
                    result = await Cmd("volume", ct, ("val", "0")).ConfigureAwait(false);
                    _muted = true;
                }
                else if (!on && _muted)
                {
                    result = await Cmd("volume", ct, ("val", Str(_savedVolume <= 0 ? 256 : _savedVolume))).ConfigureAwait(false);
                    _muted = false;
                }
                break;
            }
            case "rate":
                result = await Cmd("rate", ct, ("val", Dbl(args.RequireDouble("value")))).ConfigureAwait(false);
                break;
            case "track":
            {
                var kind = args.RequireString("kind");
                var id = args.RequireInt("id");
                var cmd = kind switch
                {
                    "audio" => "audio_track",
                    "subtitle" => "subtitle_track",
                    "video" => "video_track",
                    _ => throw ProtocolException.BadRequest("track.kind must be audio|subtitle|video"),
                };
                result = await Cmd(cmd, ct, ("val", Str(id))).ConfigureAwait(false);
                _rcTracksDirty = true;
                if (kind is "audio" or "subtitle") _history?.OnTrackChosen(kind, id, LastState?.Tracks);
                break;
            }
            case "chapter":
            {
                if (args.GetInt("index") is int idx) result = await Cmd("chapter", ct, ("val", Str(idx))).ConfigureAwait(false);
                else if (args.GetInt("delta") is int delta)
                {
                    var fresh = await _http.GetStatusAsync(ct).ConfigureAwait(false);
                    var info = fresh.Information ?? throw ProtocolException.VlcError("nothing is playing");
                    var target = Math.Clamp(info.Chapter + delta, 0, Math.Max(0, info.ChapterCount - 1));
                    result = await Cmd("chapter", ct, ("val", Str(target))).ConfigureAwait(false);
                }
                else throw ProtocolException.BadRequest("chapter requires 'index' or 'delta'");
                break;
            }
            case "title":
                result = await Cmd("title", ct, ("val", Str(args.RequireInt("index")))).ConfigureAwait(false);
                break;
            case "delay":
            {
                var kind = args.RequireString("kind");
                var seconds = args.RequireDouble("seconds");
                var cmd = kind switch
                {
                    "audio" => "audiodelay",
                    "subtitle" => "subdelay",
                    _ => throw ProtocolException.BadRequest("delay.kind must be audio|subtitle"),
                };
                result = await Cmd(cmd, ct, ("val", Dbl(seconds))).ConfigureAwait(false);
                break;
            }
            case "fullscreen":
            {
                var on = args.GetBool("on");
                if (on is null) result = await Cmd("fullscreen", ct).ConfigureAwait(false);
                else
                {
                    var fresh = await _http.GetStatusAsync(ct).ConfigureAwait(false);
                    if (fresh.Fullscreen != on.Value) result = await Cmd("fullscreen", ct).ConfigureAwait(false);
                }
                break;
            }
            case "aspect":
                result = await Cmd("aspectratio", ct, ("val", args.RequireString("value"))).ConfigureAwait(false);
                break;
            case "open":
                await OpenAsync(args.RequireString("uri"), args.GetBool("enqueue") ?? false, args.GetString("subtitle"),
                    args.GetInt("audioTrack"), args.GetInt("subtitleTrack"), ct).ConfigureAwait(false);
                break;
            case "playlistRemove":
            {
                var plId = args.RequireInt("plId");
                await Cmd("pl_delete", ct, ("id", Str(plId))).ConfigureAwait(false);
                var list = await _http.GetPlaylistAsync(ct).ConfigureAwait(false);
                if (list.Any(i => i.PlId == plId))
                {
                    await Task.Delay(200, ct).ConfigureAwait(false);
                    await Cmd("pl_delete", ct, ("id", Str(plId))).ConfigureAwait(false);
                    list = await _http.GetPlaylistAsync(ct).ConfigureAwait(false);
                    if (list.Any(i => i.PlId == plId))
                        throw ProtocolException.VlcError($"VLC did not remove playlist item {plId}");
                }
                _playlist = list;
                break;
            }
            case "playlistClear":
                result = await Cmd("pl_empty", ct).ConfigureAwait(false);
                _playlistDirty = true;
                break;
            case "random":
            case "loop":
            case "repeat":
            {
                var on = args.RequireBool("on");
                var fresh = await _http.GetStatusAsync(ct).ConfigureAwait(false);
                var current = name switch { "random" => fresh.Random, "loop" => fresh.Loop, _ => fresh.Repeat };
                if (current != on) result = await Cmd("pl_" + name, ct).ConfigureAwait(false);
                break;
            }
            default:
                throw ProtocolException.Unsupported($"unknown command '{name}'");
        }

        if (result is not null) _lastStatus = result;
        Poke();
    }

    /// <summary>Fullscreen state of the video output; <c>null</c> — VLC is not under control.</summary>
    public async Task<bool?> GetFullscreenAsync(CancellationToken ct)
    {
        if (RunState != VlcRunState.Running) return null;
        var status = await _http.GetStatusAsync(ct).ConfigureAwait(false);
        return status.Fullscreen;
    }

    public async Task SetFullscreenAsync(bool on, CancellationToken ct)
    {
        if (RunState != VlcRunState.Running) return;
        var fresh = await _http.GetStatusAsync(ct).ConfigureAwait(false);
        if (fresh.Fullscreen != on) _lastStatus = await Cmd("fullscreen", ct).ConfigureAwait(false);
        Poke();
    }

    private async Task OpenAsync(string uri, bool enqueue, string? subtitle, int? audioTrack, int? subtitleTrack, CancellationToken ct)
    {
        var before = _lastStatus?.CurrentPlId ?? -1;
        // The phone picked tracks for this file: they win over the folder's remembered choices.
        if (!enqueue && (subtitle is not null || audioTrack is not null || subtitleTrack is not null))
            _history?.ExpectExplicitTracks(uri);
        var argList = new List<(string, string)> { ("input", uri) };
        await Cmd(enqueue ? "in_enqueue" : "in_play", ct, argList.ToArray()).ConfigureAwait(false);
        _playlistDirty = true;
        if (enqueue) return;

        if (subtitle is null && audioTrack is null && subtitleTrack is null) return;

        // Tracks are selected in the background after the start: wait until the new input and information appear.
        _postOpenCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _postOpenCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
                VlcStatus? st = null;
                while (DateTimeOffset.UtcNow < deadline && !cts.IsCancellationRequested)
                {
                    await Task.Delay(400, cts.Token).ConfigureAwait(false);
                    st = await _http.GetStatusAsync(cts.Token).ConfigureAwait(false);
                    if (st.Information is not null && st.Information.Streams.Count > 0 && (st.CurrentPlId != before || before < 0) && !st.IsStopped)
                        break;
                    st = null;
                }
                if (st is null)
                {
                    _log.LogWarning("open: input did not start within 15 s; skipping track selection");
                    return;
                }
                if (!string.IsNullOrEmpty(subtitle))
                {
                    await Cmd("addsubtitle", cts.Token, ("val", subtitle)).ConfigureAwait(false);
                    await Task.Delay(500, cts.Token).ConfigureAwait(false);
                }
                if (audioTrack is int a)
                    await Cmd("audio_track", cts.Token, ("val", Str(a))).ConfigureAwait(false);
                if (subtitleTrack is int s)
                    await Cmd("subtitle_track", cts.Token, ("val", Str(s))).ConfigureAwait(false);
                _rcTracksDirty = true;
                Poke();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "open: post-start track selection failed");
            }
        }, CancellationToken.None);
    }

    private async Task<VlcStatus> Cmd(string command, CancellationToken ct, params (string Name, string Value)[] args)
    {
        try
        {
            return await _http.CommandAsync(command, ct, args).ConfigureAwait(false);
        }
        catch (VlcUnavailableException)
        {
            Poke();
            throw;
        }
    }

    private static string Str(int v) => v.ToString(CultureInfo.InvariantCulture);
    private static string Signed(int v) => v >= 0 ? "+" + Str(v) : Str(v);
    private static string Dbl(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

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
        _wake.Dispose();
    }
}
