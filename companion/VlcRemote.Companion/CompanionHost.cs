using Microsoft.Extensions.Logging;
using VlcRemote.Companion.Net;
using VlcRemote.Companion.Services;
using VlcRemote.Companion.Vlc;
using VlcRemote.Core.Displays;
using VlcRemote.Core.Pairing;
using VlcRemote.Core.Protocol;
using VlcRemote.Core.Session;
using Message = VlcRemote.Core.Protocol.Message;
using VlcRemote.Core.Localization;

namespace VlcRemote.Companion;

/// <summary>Реализация <see cref="ISessionHost"/>: связывает сессии с VLC, экранами, питанием и звуком.</summary>
public sealed class CompanionHost : ISessionHost
{
    private readonly Settings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly VlcManager _vlc;
    private readonly DisplayService _display;
    private readonly PowerService _power;
    private readonly AudioService _audio;
    private readonly ILogger _log;
    private readonly IReadOnlyList<string> _baseCaps;
    private readonly IReadOnlyList<string> _bluetoothCaps;

    public CompanionHost(Settings settings, SettingsStore settingsStore, VlcManager vlc, PairingService pairing,
        DisplayService display, PowerService power, AudioService audio, ILogger<CompanionHost> log)
    {
        _settings = settings;
        _settingsStore = settingsStore;
        _vlc = vlc;
        Pairing = pairing;
        PairingPrompter = new PairingPrompter(pairing);
        _display = display;
        _power = power;
        _audio = audio;
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
        ];
        _bluetoothCaps = [.. _baseCaps, Core.Protocol.Caps.Bluetooth];
        if (settings.MigratePreferredDisplay(display.GetMonitors()))
        {
            settingsStore.Save(settings);
            _log.LogInformation("Migrated the preferred display index to id {Id}", settings.PreferredDisplayId ?? "(none)");
        }
        Displays = new DisplayController(display, new SettingsDisplayPreference(settings, settingsStore), new VlcWindowHost(vlc, display), log)
        {
            FullscreenAfterManualPlacement = () => _settings.LaunchFullscreen,
        };
        Displays.Changed += _vlc.Poke;
        display.TopologyChanged += _vlc.Poke;
        _vlc.ProcessAppeared += OnVlcProcessAppeared;
        _vlc.LaunchTargetResolver = ResolveLaunchTarget;
        _vlc.PlaceLaunchedWindowAsync = (pid, target, ct) => Displays.PlaceNewWindowAsync(pid, startedBeforeCompanion: false, target, ct);
        _vlc.SystemInfoProvider = BuildSystemInfo;
        _vlc.ArtBaseUrlProvider = () => NetworkUtil.GetPrimaryLanAddress() is { } ip ? $"http://{ip}:{_settings.WsPort}" : null;
        _power.KeepAwakeEnabled = settings.KeepAwake;
    }

    public string MachineName => Environment.MachineName;
    public string InstallationId => _settings.InstallationId;
    /// <summary>Base capabilities plus <c>bluetooth</c> while the RFCOMM service is advertising.</summary>
    public IReadOnlyList<string> Caps => Bluetooth?.IsAdvertising == true ? _bluetoothCaps : _baseCaps;

    /// <summary>Bluetooth RFCOMM server (set by <c>Program</c> after the session manager exists).</summary>
    public BluetoothRfcommServer? Bluetooth { get; set; }

    /// <summary>Adapter address for <c>/info</c>, <c>beacon</c>, <c>welcome</c>: only while the radio is on and Bluetooth is enabled.</summary>
    public string? BluetoothAddress => Bluetooth?.AdapterAddress;
    public VlcInfo VlcInfo => _vlc.Info;

    /// <summary>Monitor selection for the VLC window (sys listDisplays/moveToDisplay/preferredDisplay, auto-placement).</summary>
    public DisplayController Displays { get; }
    public PairingService Pairing { get; }

    /// <summary>Решения об автоматическом окне сопряжения (ограничение частоты, «не создавать второй код»).</summary>
    public PairingPrompter PairingPrompter { get; }

    /// <summary>Нужно показать/поднять окно с кодом. Вызывается из потока сессии — подписчик сам маршалит в UI.</summary>
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
        Vlc = _vlc.Info,
        WsPort = _settings.WsPort,
        NeedsPairing = !Pairing.HasPairedDevices,
        PcId = InstallationId,
        Bt = BluetoothAddress,
    };

    public BeaconMessage BuildBeacon() => new()
    {
        Id = InstallationId,
        Name = MachineName,
        WsPort = _settings.WsPort,
        NeedsPairing = !Pairing.HasPairedDevices,
        Bt = BluetoothAddress,
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
            Display = display?.Index,
            DisplayId = display?.Id,
            PreferredDisplayId = Displays.PreferredId,
            KeepAwake = _power.KeepAwakeEnabled,
        };
    }

    /// <summary>A VLC opened outside the companion (e.g. a double-click on a movie): place its window once.</summary>
    private void OnVlcProcessAppeared(VlcProcessAppeared appeared)
    {
        if (appeared.Owned) return; // the launch places its own window
        _ = Task.Run(async () =>
        {
            try { await Displays.PlaceNewWindowAsync(appeared.Pid, appeared.StartedBeforeCompanion, targetId: null, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { _log.LogWarning(ex, "Placing the window of VLC {Pid} failed", appeared.Pid); }
        });
    }

    /// <summary>
    /// Monitor for a companion-launched VLC window: the one-off legacy <c>display</c> index or the preferred monitor,
    /// if connected; <c>null</c> with a single monitor (nothing to choose) or when the window is not managed.
    /// </summary>
    private string? ResolveLaunchTarget(int? displayIndex)
    {
        var monitors = _display.GetMonitors();
        if (monitors.Count < 2) return null;
        var target = displayIndex is int i ? MonitorTopology.FindByIndex(monitors, i) : MonitorTopology.FindById(monitors, Displays.PreferredId);
        return target?.Id;
    }

    public Task<StateMessage> GetStateAsync(CancellationToken ct) => _vlc.GetStateAsync(ct);

    public Task ExecuteCommandAsync(string name, CommandArgs args, CancellationToken ct) => _vlc.ExecuteCommandAsync(name, args, ct);

    public async Task<Message?> ExecuteSysAsync(string name, CommandArgs args, CancellationToken ct)
    {
        if (DisplayController.Handles(name))
            return await Displays.ExecuteAsync(name, args, ct).ConfigureAwait(false);

        switch (name)
        {
            case "keepAwake":
            {
                var on = args.RequireBool("on");
                _power.KeepAwakeEnabled = on;
                _settings.KeepAwake = on;
                _settingsStore.Save(_settings);
                _power.OnPlaybackState(_vlc.LastState?.Playback.State == "playing");
                _vlc.Poke();
                return null;
            }

            case "displayPower":
                _power.SetDisplayPower(args.RequireBool("on"));
                return null;

            case "sleep":
                _power.Sleep();
                return null;

            case "shutdown":
                _power.Shutdown();
                return null;

            case "audioDevice":
                await _vlc.SetAudioDeviceAsync(args.GetString("id") ?? "", ct).ConfigureAwait(false);
                return null;

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

            default:
                throw ProtocolException.Unsupported($"unknown sys command '{name}'");
        }
    }

    public Task<IReadOnlyList<BrowseEntry>> BrowseAsync(string uri, CancellationToken ct) => _vlc.BrowseAsync(uri, ct);
    public Task<IReadOnlyList<PlaylistEntry>> GetPlaylistAsync(CancellationToken ct) => _vlc.GetPlaylistAsync(ct);
    public Task<TracksInfo> GetTracksAsync(CancellationToken ct) => _vlc.GetTracksAsync(ct);
    public Task<IReadOnlyList<AudioDeviceEntry>> GetAudioDevicesAsync(CancellationToken ct) => _vlc.GetAudioDevicesAsync(ct);
}
