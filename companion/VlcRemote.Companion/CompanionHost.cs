using Microsoft.Extensions.Logging;
using VlcRemote.Companion.Net;
using VlcRemote.Companion.Services;
using VlcRemote.Companion.Vlc;
using VlcRemote.Core.Pairing;
using VlcRemote.Core.Protocol;
using VlcRemote.Core.Session;
using Message = VlcRemote.Core.Protocol.Message;

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
        Caps =
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
        _vlc.SystemInfoProvider = BuildSystemInfo;
        _vlc.ArtBaseUrlProvider = () => NetworkUtil.GetPrimaryLanAddress() is { } ip ? $"http://{ip}:{_settings.WsPort}" : null;
        _power.KeepAwakeEnabled = settings.KeepAwake;
    }

    public string MachineName => Environment.MachineName;
    public string InstallationId => _settings.InstallationId;
    public IReadOnlyList<string> Caps { get; }
    public VlcInfo VlcInfo => _vlc.Info;
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
    };

    public BeaconMessage BuildBeacon() => new()
    {
        Id = InstallationId,
        Name = MachineName,
        WsPort = _settings.WsPort,
        NeedsPairing = !Pairing.HasPairedDevices,
    };

    private SystemInfo BuildSystemInfo()
    {
        var vol = _audio.GetSystemVolume();
        int? display = null;
        if (_vlc.ProcessId is int pid && _vlc.RunState == VlcRunState.Running)
        {
            try { display = _display.FindDisplayOfProcess(pid); } catch { }
        }
        return new SystemInfo
        {
            Volume = vol?.Percent,
            Muted = vol?.Muted,
            AudioDevice = _vlc.LastAudioDevice,
            Display = display ?? _settings.PreferredDisplay,
            KeepAwake = _power.KeepAwakeEnabled,
        };
    }

    public Task<StateMessage> GetStateAsync(CancellationToken ct) => _vlc.GetStateAsync(ct);

    public Task ExecuteCommandAsync(string name, CommandArgs args, CancellationToken ct) => _vlc.ExecuteCommandAsync(name, args, ct);

    public async Task<Message?> ExecuteSysAsync(string name, CommandArgs args, CancellationToken ct)
    {
        switch (name)
        {
            case "listDisplays":
                return new DisplaysMessage { Items = _display.ListDisplays() };

            case "moveToDisplay":
            {
                var index = args.RequireInt("display");
                await MoveToDisplayAsync(index, ct).ConfigureAwait(false);
                return null;
            }

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

    /// <summary>Перенести окно VLC на экран: снять полный экран → SetWindowPos → полный экран.</summary>
    public async Task MoveToDisplayAsync(int index, CancellationToken ct)
    {
        if (_display.GetScreen(index) is null) throw ProtocolException.BadRequest($"no display with index {index}");
        _settings.PreferredDisplay = index;
        _settingsStore.Save(_settings);

        if (_vlc.RunState != VlcRunState.Running || _vlc.ProcessId is not int pid)
            throw ProtocolException.VlcNotRunning();

        var state = await _vlc.GetStateAsync(ct).ConfigureAwait(false);
        if (state.Video.Fullscreen)
        {
            await _vlc.ExecuteCommandAsync("fullscreen", new CommandArgs(ParseArgs("""{"on":false}""")), ct).ConfigureAwait(false);
            await Task.Delay(400, ct).ConfigureAwait(false);
        }
        var moved = _display.MoveWindowToDisplay(pid, index);
        if (!moved) throw ProtocolException.VlcError("окно VLC не найдено");
        await Task.Delay(300, ct).ConfigureAwait(false);
        await _vlc.ExecuteCommandAsync("fullscreen", new CommandArgs(ParseArgs("""{"on":true}""")), ct).ConfigureAwait(false);
    }

    private static System.Text.Json.JsonElement ParseArgs(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public Task<IReadOnlyList<BrowseEntry>> BrowseAsync(string uri, CancellationToken ct) => _vlc.BrowseAsync(uri, ct);
    public Task<IReadOnlyList<PlaylistEntry>> GetPlaylistAsync(CancellationToken ct) => _vlc.GetPlaylistAsync(ct);
    public Task<TracksInfo> GetTracksAsync(CancellationToken ct) => _vlc.GetTracksAsync(ct);
    public Task<IReadOnlyList<AudioDeviceEntry>> GetAudioDevicesAsync(CancellationToken ct) => _vlc.GetAudioDevicesAsync(ct);
}
