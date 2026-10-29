using Microsoft.Extensions.Logging;
using Sofabird.Companion.Net;
using Sofabird.Companion.Services;
using Sofabird.Companion.Vlc;
using Sofabird.Companion.YouTube;
using Sofabird.Core.Displays;
using Sofabird.Core.History;
using Sofabird.Core.Library;
using Sofabird.Core.Pairing;
using Sofabird.Core.Protocol;
using Sofabird.Core.Session;
using Sofabird.Core.Streaming;
using Sofabird.Core.YouTube;
using Message = Sofabird.Core.Protocol.Message;
using Sofabird.Core.Localization;

namespace Sofabird.Companion;

/// <summary>Implementation of <see cref="ISessionHost"/>: connects sessions with VLC, displays, power and sound.</summary>
public sealed class CompanionHost : ISessionHost
{
    /// <summary><c>sys sleep</c>: time for the reply to reach the phone before Windows suspends.</summary>
    private static readonly TimeSpan SleepAfterReply = TimeSpan.FromMilliseconds(300);

    private readonly Settings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly VlcManager _vlc;
    private readonly DisplayService _display;
    private readonly PowerService _power;
    private readonly AudioService _audio;
    private readonly BackgroundWork _background;
    private readonly LibraryService _library;
    private readonly MoonlightService _moonlight;
    private readonly YouTubeService _youtube;
    private readonly YouTubeHistory _youtubeHistory;
    private readonly WakeOnLanService _wake;
    private readonly ILogger _log;
    private readonly IReadOnlyList<string> _baseCaps;
    private readonly IReadOnlyList<string> _bluetoothCaps;

    public CompanionHost(Settings settings, SettingsStore settingsStore, VlcManager vlc, PairingService pairing,
        DisplayService display, PowerService power, AudioService audio, AudioDeviceWatcher audioWatcher, BackgroundWork background,
        LibraryService library, MoonlightService moonlight, YouTubeService youtube, YouTubeHistory youtubeHistory,
        WakeOnLanService wake, ILogger<CompanionHost> log)
    {
        _settings = settings;
        _settingsStore = settingsStore;
        _vlc = vlc;
        Pairing = pairing;
        PairingPrompter = new PairingPrompter(pairing);
        _display = display;
        _power = power;
        _audio = audio;
        _background = background;
        _library = library;
        _moonlight = moonlight;
        _youtube = youtube;
        _youtubeHistory = youtubeHistory;
        _wake = wake;
        _log = log;
        _baseCaps =
        [
            Core.Protocol.Caps.CurrentTrack,
            Core.Protocol.Caps.ChapterNames,
            Core.Protocol.Caps.AudioDevices,
            Core.Protocol.Caps.PushEvents,
            Core.Protocol.Caps.LaunchVlc,
            Core.Protocol.Caps.WindowControl,
            Core.Protocol.Caps.Power,
            Core.Protocol.Caps.SystemVolume,
            Core.Protocol.Caps.Metadata,
            Core.Protocol.Caps.Library,
            .. vlc.History is null ? Array.Empty<string>() : [Core.Protocol.Caps.History],
            .. moonlight.ExePath is null ? Array.Empty<string>() : [Core.Protocol.Caps.Moonlight],
        ];
        _bluetoothCaps = [.. _baseCaps, Core.Protocol.Caps.Bluetooth];
        Displays = new DisplayController(display, new SettingsDisplayPreference(settings, settingsStore), new VlcWindowHost(vlc, display), log)
        {
            FullscreenAfterManualPlacement = () => _settings.LaunchFullscreen,
        };
        Displays.Changed += _vlc.Poke;
        Displays.CaptureTopology();
        display.TopologyChanged += _vlc.Poke;
        // Lid closed, TV off/on, HDMI unplugged: VLC back on the TV, or paused while the TV is gone.
        display.TopologyChanged += Displays.OnTopologyChanged;
        audioWatcher.DeviceArrived += OnAudioDeviceArrived;
        _vlc.ProcessAppeared += OnVlcProcessAppeared;
        _vlc.LaunchTargetResolver = ResolveLaunchTarget;
        _vlc.PlaceLaunchedWindowAsync = (pid, target, ct) => Displays.PlaceNewWindowAsync(pid, startedBeforeCompanion: false, target, ct);
        _vlc.SystemInfoProvider = BuildSystemInfo;
        _vlc.LibraryInfoProvider = () => _library.Info;
        _library.Changed += _vlc.Poke;
        _moonlight.Changed += _vlc.Poke;
        _youtube.Changed += _vlc.Poke;
        _vlc.YouTubeInfoProvider = _youtube.Info;
        _vlc.YouTubeHistoryRevision = () => _youtubeHistory.Revision;
        _youtubeHistory.Changed += _vlc.Poke;
        _youtube.PlaceOnTv = window => PlaceOnTv(window, null);
        _moonlight.PlaceOnTv = window => PlaceOnTv(window, null);
        Displays.TvScreenChosen += OnTvScreenChosen;
        _vlc.ArtBaseUrlProvider = () => NetworkUtil.GetPrimaryLanAddress() is { } ip ? $"http://{ip}:{_settings.WsPort}" : null;
        _vlc.StateChanged += s => _power.OnPlaybackState(s.Playback.State == "playing");
        _power.KeepAwakeEnabled = settings.KeepAwake;
    }

    /// <summary>"Keep the PC awake during a movie": from the phone, the tray menu and the setup window.</summary>
    public void SetKeepAwake(bool on)
    {
        _settingsStore.Update(s => s.KeepAwake = on);
        _power.KeepAwakeEnabled = on;
        _vlc.Poke(); // phones see the new value in the state
    }

    public string MachineName => Environment.MachineName;
    public string InstallationId => _settings.InstallationId;
    /// <summary>Base capabilities plus <c>bluetooth</c> while the RFCOMM service is advertising.</summary>
    public IReadOnlyList<string> Caps
    {
        get
        {
            var caps = Bluetooth?.IsAdvertising == true ? _bluetoothCaps : _baseCaps;
            // Firefox may be installed from the setup window while the companion runs.
            return _youtube.Available ? [.. caps, Core.Protocol.Caps.YouTube] : caps;
        }
    }

    /// <summary>Bluetooth RFCOMM server (set by <c>Program</c> after the session manager exists).</summary>
    public BluetoothRfcommServer? Bluetooth { get; set; }

    /// <summary>Adapter address for <c>/info</c>, <c>beacon</c>, <c>welcome</c>: only while the radio is on and Bluetooth is enabled.</summary>
    public string? BluetoothAddress => Bluetooth?.AdapterAddress;

    public IReadOnlyList<string>? WakeMacs => _wake.Macs();
    /// <summary>Set once the encrypted listener runs (<see cref="Net.WebServer"/>).</summary>
    public TlsInfo? Tls { get; set; }
    public VlcInfo VlcInfo => _vlc.Info.Clone();

    /// <summary>Monitor selection for the VLC window (sys listDisplays/moveToDisplay/preferredDisplay, auto-placement).</summary>
    public DisplayController Displays { get; }
    public PairingService Pairing { get; }

    /// <summary>Decisions about the automatic pairing window (rate limiting, "do not create a second code").</summary>
    public PairingPrompter PairingPrompter { get; }

    /// <summary>The code window must be shown/raised. Called from the session thread — the subscriber marshals to the UI itself.</summary>
    public event Action<PairingPrompt>? PairingPromptRequested;

    public void RequestPairing(DeviceInfo? device)
    {
        var prompt = PairingPrompter.Request(device?.Name, device?.Id);
        _log.LogInformation("Pairing requested by {Device} ({Id}): {Action}", device?.Name ?? "?", device?.Id ?? "?", prompt.Action);
        if (prompt.Action == PairingPromptAction.Throttled) return;
        try { PairingPromptRequested?.Invoke(prompt); }
        catch (Exception ex) { _log.LogWarning(ex, "PairingPromptRequested handler failed"); }
    }

    public InfoResponse BuildInfo() => new()
    {
        Name = MachineName,
        Version = AppVersion.Informational,
        Vlc = _vlc.Info.Clone(),
        WsPort = _settings.WsPort,
        NeedsPairing = !Pairing.HasPairedDevices,
        PcId = InstallationId,
        Bt = BluetoothAddress,
        Macs = WakeMacs,
        Tls = Tls,
    };

    public BeaconMessage BuildBeacon() => new()
    {
        Id = InstallationId,
        Name = MachineName,
        WsPort = _settings.WsPort,
        NeedsPairing = !Pairing.HasPairedDevices,
        Bt = BluetoothAddress,
        Macs = WakeMacs,
        Tls = Tls,
    };

    private SystemInfo BuildSystemInfo()
    {
        var vol = _audio.GetSystemVolume();
        MonitorInfo? display = null;
        try { display = Displays.FindVlcMonitor(); }
        catch (Exception ex) { _log.LogDebug(ex, "Monitor of the VLC window not found"); }
        return new SystemInfo
        {
            Volume = vol?.Percent,
            Muted = vol?.Muted,
            AudioDevice = _vlc.LastAudioDevice,
            DisplayId = display?.Id,
            PreferredDisplayId = Displays.PreferredId,
            KeepAwake = _power.KeepAwakeEnabled,
            MoonlightHost = _moonlight.ExePath is null ? null : MoonlightCommandLine.ChooseHost(_moonlight.Hosts(), null)?.Name,
            MoonlightStreaming = _moonlight.ExePath is null ? null : MoonlightService.StreamingHost() is not null,
        };
    }

    /// <summary>
    /// An output device became active. If it is the one chosen from the phone (the TV's HDMI audio after the TV was off),
    /// VLC is switched back to it: VLC moved to another device when it disappeared and does not return by itself.
    /// </summary>
    private void OnAudioDeviceArrived(string id)
    {
        var wanted = _settings.VlcAudioDevice;
        if (string.IsNullOrEmpty(wanted) || !string.Equals(wanted, id, StringComparison.OrdinalIgnoreCase)) return;
        _background.Run("Switching VLC back to the returned audio device", async ct =>
        {
            // Windows reports the endpoint before audio can be opened on it.
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            if (await _vlc.RestoreAudioDeviceAsync(id, ct).ConfigureAwait(false))
                _log.LogInformation("Audio device {Id} is back: VLC switched back to it", id);
        });
    }

    /// <summary>A VLC opened outside the companion (e.g. a double-click on a movie): place its window once.</summary>
    private void OnVlcProcessAppeared(VlcProcessAppeared appeared)
    {
        if (appeared.Owned) return; // the launch places its own window
        _background.Run($"Placing the window of VLC {appeared.Pid}",
            ct => Displays.PlaceNewWindowAsync(appeared.Pid, appeared.StartedBeforeCompanion, targetId: null, ct));
    }

    /// <summary>
    /// Monitor for a companion-launched VLC window: the preferred monitor, if connected; <c>null</c> with a single
    /// monitor (nothing to choose) or when the window is not managed.
    /// </summary>
    private string? ResolveLaunchTarget()
    {
        var monitors = _display.GetMonitors();
        return monitors.Count < 2 ? null : MonitorTopology.FindById(monitors, Displays.PreferredId)?.Id;
    }

    /// <summary>One TV screen: YouTube and the Moonlight stream follow the monitor just chosen (VLC moves by itself).</summary>
    private void OnTvScreenChosen(MonitorInfo monitor)
    {
        foreach (var window in new[] { _youtube.Window(), MoonlightService.StreamWindow() })
            if (window != IntPtr.Zero) PlaceOnTv(window, monitor);
    }

    /// <summary>
    /// A window for the TV (the TV browser, Moonlight's stream) goes to <paramref name="target"/>, by default where VLC
    /// plays: the monitor VLC opens on, or the one with its window. Nothing happens with one monitor or no choice.
    /// </summary>
    private void PlaceOnTv(IntPtr window, MonitorInfo? target)
    {
        var monitors = _display.GetMonitors();
        if (monitors.Count < 2) return;
        target ??= MonitorTopology.FindById(monitors, Displays.PreferredId) ?? Displays.FindVlcMonitor(monitors);
        if (target is null) return;
        if (string.Equals(DisplayService.GetWindowMonitorDevice(window), target.GdiDeviceName, StringComparison.OrdinalIgnoreCase)) return;
        _display.MoveWindowToMonitor(window, target);
    }

    public Task<StateMessage> GetStateAsync(CancellationToken ct) => _vlc.GetStateAsync(ct);

    public Task ExecuteCommandAsync(string name, CommandArgs args, CancellationToken ct)
    {
        // A YouTube video of the history ("Continue watching"): to the TV browser, where it stopped.
        if (name == "open" && YouTubeHistory.VideoIdOf(args.GetString("uri")) is not null && _youtube.Available)
        {
            _youtube.Open(args.GetString("uri")!);
            return Task.CompletedTask;
        }
        // One thing on the TV at a time: the TV browser would cover what VLC starts, and the phones would stay in
        // their YouTube mode.
        if (StartsVlcItem(name, args)) _youtube.CloseForVlc();
        return _vlc.ExecuteCommandAsync(name, args, ct);
    }

    /// <summary>A file opened from the phone, VLC started, or another playlist item chosen.</summary>
    private static bool StartsVlcItem(string name, CommandArgs args) => name switch
    {
        "open" => !(args.GetBool("enqueue") ?? false),
        "launch" => true,
        "play" => args.GetInt("plId") is not null,
        _ => false,
    };

    public async Task<Message?> ExecuteSysAsync(string name, CommandArgs args, CancellationToken ct)
    {
        if (DisplayController.Handles(name))
            return await Displays.ExecuteAsync(name, args, ct).ConfigureAwait(false);

        switch (name)
        {
            case "keepAwake":
                SetKeepAwake(args.RequireBool("on"));
                return null;

            case "displayPower":
                _power.SetDisplayPower(args.RequireBool("on"));
                return null;

            case "sleep":
                // After the reply: the phone hears "done", then "sleeping", then Windows suspends.
                _ = Task.Run(async () =>
                {
                    await Task.Delay(SleepAfterReply, CancellationToken.None).ConfigureAwait(false);
                    _power.Sleep();
                }, CancellationToken.None); // not the request's token: the session ends with the sleep
                return null;

            case "shutdown":
                _power.Shutdown();
                return null;

            case "audioDevice":
            {
                var id = args.GetString("id") ?? "";
                await _vlc.SetAudioDeviceAsync(id, ct).ConfigureAwait(false);
                _settingsStore.Update(s => s.VlcAudioDevice = id.Length == 0 ? null : id);
                return null;
            }

            case "systemVolume":
            {
                if (args.GetBool("mute") is bool mute) _audio.SetSystemMute(mute);
                if (args.GetInt("value") is int value) _audio.SetSystemVolume(value);
                else if (args.GetInt("delta") is int delta)
                {
                    var cur = _audio.GetSystemVolume()?.Percent ?? 50;
                    _audio.SetSystemVolume(cur + delta);
                }
                else if (!args.Has("mute")) throw ProtocolException.BadRequest("systemVolume requires 'value', 'delta' or 'mute'");
                _vlc.Poke();
                return null;
            }

            case "moonlightStart":
                // The stream covers the TV: a movie playing behind it would go on unseen.
                if (_vlc.IsPlaying) await _vlc.PauseAsync(ct).ConfigureAwait(false);
                await _moonlight.StartAsync(args.GetString("host"), args.GetString("app"), ct).ConfigureAwait(false);
                _vlc.Poke();
                return null;

            case "youtubeOpen":
                // The TV browser covers VLC: a movie playing behind it would go on unseen.
                if (_vlc.IsPlaying) await _vlc.PauseAsync(ct).ConfigureAwait(false);
                _youtube.Open(args.RequireString("url"));
                _vlc.Poke();
                return null;

            case "youtubeAdBlock":
                await _youtube.SetAddonAsync(YouTube.TvAddon.UBlock, args.RequireBool("on"), ct).ConfigureAwait(false);
                _vlc.Poke();
                return null;

            case "youtubeSponsorBlock":
                await _youtube.SetAddonAsync(YouTube.TvAddon.SponsorBlock, args.RequireBool("on"), ct).ConfigureAwait(false);
                _vlc.Poke();
                return null;

            case "youtube":
                await _youtube.ControlAsync(args.RequireString("action"), args.GetDouble("time")).ConfigureAwait(false);
                return null;

            case "moonlightStop":
                await _moonlight.StopAsync(ct).ConfigureAwait(false);
                _vlc.Poke();
                return null;

            default:
                throw ProtocolException.Unsupported($"unknown sys command '{name}'");
        }
    }

    public Task<IReadOnlyList<BrowseEntry>> BrowseAsync(string uri, CancellationToken ct) => _vlc.BrowseAsync(uri, ct);
    public Task<IReadOnlyList<PlaylistEntry>> GetPlaylistAsync(CancellationToken ct) => _vlc.GetPlaylistAsync(ct);
    public Task<TracksInfo> GetTracksAsync(CancellationToken ct) => _vlc.GetTracksAsync(ct);
    public Task<IReadOnlyList<AudioDeviceEntry>> GetAudioDevicesAsync(CancellationToken ct) => _vlc.GetAudioDevicesAsync(ct);

    public Task<LibraryMessage> GetLibraryAsync(CancellationToken ct) => Task.FromResult(_library.BuildLibrary());

    public Task<ShowMessage?> GetShowAsync(string key, CancellationToken ct) => Task.FromResult(_library.BuildShow(key));

    private HistoryStore HistoryStore => _vlc.History?.Store ?? throw ProtocolException.Unsupported("watch history is off");

    public async Task<HistoryMessage> GetHistoryAsync(string filter, int limit, int offset, CancellationToken ct)
    {
        var store = HistoryStore;
        var youtube = _youtubeHistory.Items(filter);
        if (filter == "youtube") return Page(youtube, limit, offset);
        // "continue" lists series folders (possibly on a network share): off the session thread.
        var (_, files) = await Task.Run(() => store.Query(filter, int.MaxValue, 0), ct).ConfigureAwait(false);
        var page = Page(YouTubeHistory.Merge(files, youtube), limit, offset);
        // Files of the library get its TMDB picture (a YouTube video has its thumbnail already).
        var art = _library.Catalog.ArtOf(page.Items.Where(i => i.ArtUrl is null && i.Kind is null).Select(i => i.Uri));
        foreach (var item in page.Items)
        {
            if (item.ArtUrl is null && art.TryGetValue(LibraryCatalog.UriKey(item.Uri), out var url)) item.ArtUrl = url;
        }
        return page;
    }

    private static HistoryMessage Page(List<HistoryItem> items, int limit, int offset) =>
        new() { Total = items.Count, Items = items.Skip(offset).Take(limit).ToList() };

    public Task UpdateHistoryAsync(string uri, bool? finished, bool remove, CancellationToken ct)
    {
        var store = HistoryStore;
        if (YouTubeHistory.VideoIdOf(uri) is { } videoId)
        {
            var changed = remove ? _youtubeHistory.Remove(videoId) : finished is bool done && _youtubeHistory.SetFinished(videoId, done);
            _log.LogInformation("YouTube history: {Video} {Action} by a phone ({Result})", videoId,
                remove ? "removed" : finished == true ? "marked watched" : "marked unwatched", changed ? "done" : "nothing to change");
            return Task.CompletedTask;
        }
        if (MediaUri.Normalize(uri) is null) throw ProtocolException.BadRequest(ProtocolText.HistoryNotAFile(uri));
        if (remove)
        {
            var removed = store.Remove(uri);
            _log.LogInformation("History: {Uri} {Result} by a phone", uri, removed ? "removed" : "not found, nothing removed");
        }
        else if (finished is bool f)
        {
            store.SetFinished(uri, f);
            _log.LogInformation("History: {Uri} marked {State} by a phone", uri, f ? "watched" : "unwatched");
        }
        _vlc.Poke();
        return Task.CompletedTask;
    }

    public Task ClearHistoryAsync(CancellationToken ct)
    {
        HistoryStore.Clear();
        _youtubeHistory.Clear();
        _log.LogInformation("History cleared by a phone");
        _vlc.Poke();
        return Task.CompletedTask;
    }
}
