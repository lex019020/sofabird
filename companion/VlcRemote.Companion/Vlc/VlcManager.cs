using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using VlcRemote.Core.Lua;
using VlcRemote.Core.Protocol;
using VlcRemote.Core.Session;
using VlcRemote.Core.State;
using VlcRemote.Core.Vlc;

namespace VlcRemote.Companion.Vlc;

public enum VlcRunState
{
    /// <summary>vlc.exe не найден на диске.</summary>
    NotInstalled,
    /// <summary>Процесса vlc.exe нет.</summary>
    NotRunning,
    /// <summary>Процесс запущен компаньоном, интерфейсы ещё поднимаются.</summary>
    Starting,
    /// <summary>vlc.exe есть, но HTTP-интерфейс не отвечает с нашим паролем.</summary>
    RunningNoApi,
    /// <summary>Полный контроль: HTTP (+RC).</summary>
    Running,
}

/// <summary>
/// Владеет жизненным циклом VLC: находит/запускает процесс, опрашивает HTTP (500 мс при
/// воспроизведении / 2 с иначе), держит RC-сокет для событий, <c>adev</c> и текущих дорожек,
/// собирает <see cref="StateMessage"/> и публикует его при изменениях.
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

    public VlcManager(Settings settings, VlcInstallation? installation, LuaPagesInstaller? lua, ILoggerFactory lf)
    {
        _settings = settings;
        Installation = installation;
        _lua = lua;
        _lf = lf;
        _log = lf.CreateLogger<VlcManager>();
        _http = new VlcHttpClient("127.0.0.1", settings.VlcHttpPort, settings.VlcHttpPassword ?? "", logger: lf.CreateLogger<VlcHttpClient>());
        _rc = new VlcRcClient("127.0.0.1", settings.VlcRcPort, lf.CreateLogger<VlcRcClient>());
        _rc.StatusChanged += OnRcStatusChanged;
        Info = new VlcInfo { Running = false, Version = installation?.Version, Level = 2 };
        RunState = installation is null ? VlcRunState.NotInstalled : VlcRunState.NotRunning;
    }

    public VlcInstallation? Installation { get; private set; }
    public VlcRunState RunState { get; private set; }
    public VlcInfo Info { get; }
    public StateMessage? LastState { get; private set; }
    public int? ProcessId { get; private set; }
    public bool Muted => _muted;
    public bool LuaInstallPending => _luaInstallPending;
    /// <summary>Lua-страницы отвечают (уровень 1 внутри VLC).</summary>
    public bool TracksPageAvailable => !_tracksPageMissing && _tracksPage is not null;

    /// <summary>Поставщик блока <c>system</c> для состояния (громкость Windows, экран, keepAwake).</summary>
    public Func<SystemInfo?>? SystemInfoProvider { get; set; }
    /// <summary>Базовый URL компаньона для <c>artUrl</c>.</summary>
    public Func<string?>? ArtBaseUrlProvider { get; set; }

    public event Action<StateMessage>? StateChanged;
    public event Action<EventMessage>? Event;
    public event Action? RunStateChanged;

    public void Start()
    {
        if (_loop is not null) return;
        if (_settings.AutoInstallLuaPages) TryInstallLuaPages(initial: true);
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

    /// <summary>Разбудить цикл опроса (после команды или события RC).</summary>
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
            VlcStatus status;
            try
            {
                status = await _http.GetStatusAsync(ct).ConfigureAwait(false);
                _consecutiveHttpFailures = 0;
            }
            catch (VlcException ex)
            {
                _consecutiveHttpFailures++;
                var starting = _owned is not null && !_owned.HasExited && DateTimeOffset.UtcNow - _ownedStartedAt < TimeSpan.FromSeconds(20);
                var newState = starting ? VlcRunState.Starting : VlcRunState.RunningNoApi;
                if (RunState != newState)
                {
                    _log.LogInformation("VLC process {Pid} is running but HTTP is not available: {Error}", process.Id, ex.Message);
                    SetRunState(newState);
                }
                if (RunState == VlcRunState.RunningNoApi && Info.Running)
                {
                    Info.Running = false;
                    PublishState(BuildState(null));
                }
                return;
            }

            if (RunState != VlcRunState.Running)
            {
                _log.LogInformation("VLC HTTP interface is up (version {Version}, api {Api})", status.Version, status.ApiVersion);
                Info.Running = true;
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

            if (prev is not null && prev.CurrentPlId != status.CurrentPlId)
            {
                _playlistDirty = true;
                _rcTracksDirty = true;
                _muted = false;
                if (status.CurrentPlId >= 0)
                    RaiseEvent(new EventMessage { Name = EventNames.InputChanged, Uri = _playlist?.FirstOrDefault(p => p.PlId == status.CurrentPlId)?.Uri });
            }

            if (prev is not null && !prev.IsStopped && status.IsStopped)
            {
                var nearEnd = prev.Length > 0 && prev.Time >= prev.Length - 3;
                if (nearEnd || _endReachedHint)
                    RaiseEvent(new EventMessage { Name = EventNames.EndReached });
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

            PublishState(BuildState(status));
        }
        finally
        {
            _pollGate.Release();
        }
    }

    private async Task OnNoProcessAsync(CancellationToken ct)
    {
        ProcessId = null;
        if (_owned is not null)
        {
            try { _owned.Dispose(); } catch { }
            _owned = null;
        }
        var wasRunning = RunState is VlcRunState.Running or VlcRunState.RunningNoApi or VlcRunState.Starting;
        if (wasRunning)
        {
            _log.LogInformation("VLC exited");
            Info.Running = false;
            _lastStatus = null;
            _playlist = null;
            _tracksPage = null;
            _rcTracks = null;
            _muted = false;
            SetRunState(Installation is null ? VlcRunState.NotInstalled : VlcRunState.NotRunning);
            await _rc.DisconnectAsync().ConfigureAwait(false);
            RaiseEvent(new EventMessage { Name = EventNames.VlcExited });
        }
        if (_luaInstallPending) TryInstallLuaPages(initial: false);
        if (_lastStateJson is null || wasRunning)
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

    private void SetRunState(VlcRunState s)
    {
        if (RunState == s) return;
        RunState = s;
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
            if (audio is null) return; // пауза — RC отказал, попробуем позже
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
            // RC не умеет «устройство по умолчанию» пустым id; пробуем через нашу Lua-страницу.
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

    // ------------------------------------------------------------------ process control

    public bool IsProcessRunning => RunState is VlcRunState.Running or VlcRunState.RunningNoApi or VlcRunState.Starting;

    /// <summary>
    /// Запустить <c>vlc.exe</c> с HTTP+RC интерфейсами. Если VLC уже под контролем — открыть
    /// <paramref name="uri"/> через HTTP. Если запущен без API — исключение (нужен перезапуск).
    /// </summary>
    public async Task LaunchAsync(string? uri, int? displayIndex, CancellationToken ct)
    {
        Installation ??= VlcLocator.Find(_settings.VlcPath);
        if (Installation is null)
            throw ProtocolException.VlcNotRunning("VLC не найден. Установите VLC 3.x или укажите путь в настройках.");

        if (RunState == VlcRunState.Running)
        {
            if (!string.IsNullOrEmpty(uri))
                await OpenAsync(uri, enqueue: false, subtitle: null, audioTrack: null, subtitleTrack: null, ct).ConfigureAwait(false);
            return;
        }
        if (RunState is VlcRunState.RunningNoApi or VlcRunState.Starting)
            throw ProtocolException.VlcError("VLC запущен без веб-интерфейса — перезапустите его из меню компаньона");

        if (_settings.AutoInstallLuaPages) TryInstallLuaPages(initial: false);

        var psi = new ProcessStartInfo
        {
            FileName = Installation.ExePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Installation.InstallDir,
        };
        foreach (var a in BuildLaunchArgs(uri, displayIndex ?? _settings.PreferredDisplay))
            psi.ArgumentList.Add(a);

        _log.LogInformation("Launching VLC: {Exe} {Args}", psi.FileName, RedactArgs(psi.ArgumentList));
        var process = Process.Start(psi) ?? throw ProtocolException.VlcError("Не удалось запустить vlc.exe");
        _owned = process;
        _ownedStartedAt = DateTimeOffset.UtcNow;
        ProcessId = process.Id;
        SetRunState(VlcRunState.Starting);
        Poke();

        // Ждём HTTP до 20 с.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (process.HasExited)
                throw ProtocolException.VlcError($"vlc.exe завершился сразу после запуска (код {process.ExitCode}). Возможно, включён режим одного экземпляра.");
            if (RunState == VlcRunState.Running) return;
            await Task.Delay(300, ct).ConfigureAwait(false);
            Poke();
        }
        if (RunState != VlcRunState.Running)
            _log.LogWarning("VLC started but HTTP interface did not answer within 20 s");
    }

    internal IEnumerable<string> BuildLaunchArgs(string? uri, int? displayIndex)
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
        if (displayIndex is int d && d >= 0)
            yield return $"--qt-fullscreen-screennumber={d}";
        if (_settings.LaunchFullscreen)
            yield return "--fullscreen";
        if (!string.IsNullOrWhiteSpace(_settings.VlcExtraArgs))
            foreach (var extra in SplitArgs(_settings.VlcExtraArgs))
                yield return extra;
        if (!string.IsNullOrEmpty(uri))
            yield return uri;
    }

    /// <summary>Командная строка для журнала: значение <c>--http-password</c> скрыто.</summary>
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

    /// <summary>Закрыть все экземпляры VLC (включая запущенные без API) и запустить заново.</summary>
    public async Task RestartAsync(string? uri, CancellationToken ct)
    {
        await QuitAsync(ct).ConfigureAwait(false);
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var procs = VlcLocator.RunningProcesses();
            if (procs.Length == 0) break;
            foreach (var p in procs) p.Dispose();
            await Task.Delay(300, ct).ConfigureAwait(false);
        }
        foreach (var p in VlcLocator.RunningProcesses())
        {
            _log.LogWarning("VLC {Pid} did not exit gracefully; killing", p.Id);
            try { p.Kill(); } catch { }
            p.Dispose();
        }
        await Task.Delay(500, ct).ConfigureAwait(false);
        Poke();
        await Task.Delay(200, ct).ConfigureAwait(false);
        await LaunchAsync(uri, null, ct).ConfigureAwait(false);
    }

    /// <summary>Закрыть VLC: RC <c>quit</c>, затем <c>vlc://quit</c> по HTTP, затем закрытие окна.</summary>
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

    // ------------------------------------------------------------------ Lua pages

    /// <summary>Установить Lua-страницы, если VLC не запущен; иначе отложить до его закрытия.</summary>
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
                VlcRunState.NotInstalled => "VLC не установлен",
                VlcRunState.RunningNoApi => "VLC запущен без веб-интерфейса",
                VlcRunState.Starting => "VLC запускается",
                _ => "VLC не запущен",
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

    public async Task<IReadOnlyList<BrowseEntry>> BrowseAsync(string uri, CancellationToken ct)
    {
        EnsureRunning();
        var entries = await _http.BrowseAsync(uri, ct).ConfigureAwait(false);
        return entries.Select(e => new BrowseEntry
        {
            Name = e.Name,
            Uri = e.Uri,
            Type = e.Type == "dir" ? "dir" : "file",
            Size = e.Type == "dir" ? null : e.Size,
            Modified = e.ModificationTime,
        }).ToList();
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
            case "launch":
                await LaunchAsync(args.GetString("uri"), args.GetInt("display"), ct).ConfigureAwait(false);
                return;
            case "quit":
                if (!IsProcessRunning) return;
                await QuitAsync(ct).ConfigureAwait(false);
                return;
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

    private async Task OpenAsync(string uri, bool enqueue, string? subtitle, int? audioTrack, int? subtitleTrack, CancellationToken ct)
    {
        var before = _lastStatus?.CurrentPlId ?? -1;
        var argList = new List<(string, string)> { ("input", uri) };
        await Cmd(enqueue ? "in_enqueue" : "in_play", ct, argList.ToArray()).ConfigureAwait(false);
        _playlistDirty = true;
        if (enqueue) return;

        if (subtitle is null && audioTrack is null && subtitleTrack is null) return;

        // Дорожки выбираем после старта в фоне: ждём, пока появится новый вход и information.
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
        await _rc.DisposeAsync().ConfigureAwait(false);
        _http.Dispose();
        _owned?.Dispose();
        _cts.Dispose();
        _pollGate.Dispose();
        _wake.Dispose();
    }
}
