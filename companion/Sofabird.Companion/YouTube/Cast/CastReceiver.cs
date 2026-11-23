using Microsoft.Extensions.Logging;
using Sofabird.Core.YouTube.Cast;

namespace Sofabird.Companion.YouTube.Cast;

/// <summary>
/// Casting from the YouTube app to this PC: while it is on (<see cref="Settings.YouTubeCast"/>) and YouTube on the TV
/// is installed, the PC answers the app's DIAL searches (<see cref="SsdpResponder"/>, the HTTP part in
/// <see cref="Net.WebServer"/> under <c>/dial/</c>), stays connected to YouTube's lounge service
/// (<see cref="LoungeConnection"/>) and plays what the phones cast on the TV (<see cref="CastSession"/>). No Google
/// account is involved on the PC; the phones and the PC meet through YouTube's servers.
/// </summary>
public sealed class CastReceiver : IAsyncDisposable
{
    private readonly Settings _settings;
    private readonly SettingsStore _store;
    private readonly YouTubeService _youtube;
    private readonly ICastPlayer _player;
    private readonly string _name;
    private readonly int _httpPort;
    private readonly ILoggerFactory _lf;
    private readonly ILogger _log;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _switch = new(1, 1);
    private LoungeConnection? _lounge;
    private CastSession? _session;
    private SsdpResponder? _ssdp;
    private bool _disposed;

    /// <param name="name">The name phones show in the cast list (the PC's name).</param>
    /// <param name="httpPort">The plain HTTP port the DIAL device description is served on.</param>
    public CastReceiver(Settings settings, SettingsStore store, YouTubeService youtube, ICastPlayer player, string name, int httpPort,
        ILoggerFactory lf)
    {
        _settings = settings;
        _store = store;
        _youtube = youtube;
        _player = player;
        _name = name;
        _httpPort = httpPort;
        _lf = lf;
        _log = lf.CreateLogger<CastReceiver>();
        _http = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        })
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan, // each request has its own; the poll lasts minutes
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Sofabird");
        // Firefox installed from the setup window: casting can start.
        _youtube.Changed += () =>
        {
            if (!_disposed && Wanted != Active) _ = RefreshAsync();
        };
    }

    /// <summary>Casting is switched on (the setup window).</summary>
    public bool Enabled => _settings.YouTubeCast;

    /// <summary>The PC answers DIAL searches and keeps a lounge connection (on, and YouTube on the TV installed).</summary>
    public bool Active => _lounge is not null;

    /// <summary>Connected to YouTube: phones can cast now.</summary>
    public bool Connected => _lounge?.Bound == true;

    /// <summary><see cref="Active"/> or <see cref="Connected"/> changed (the setup window shows it).</summary>
    public event Action? Changed;

    private bool Wanted => _settings.YouTubeCast && _youtube.Available;

    /// <summary>Starts or stops casting as the setting and YouTube on the TV say.</summary>
    public async Task RefreshAsync()
    {
        await _switch.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            if (Wanted && _lounge is null) Start();
            else if (!Wanted && _lounge is not null) await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            _switch.Release();
        }
        RaiseChanged();
    }

    /// <summary>Switches casting on or off (the setup window).</summary>
    public async Task SetEnabledAsync(bool on)
    {
        if (_settings.YouTubeCast != on)
        {
            _store.Update(s => s.YouTubeCast = on);
            _log.LogInformation("YouTube cast: {State}", on ? "on" : "off");
        }
        await RefreshAsync().ConfigureAwait(false);
    }

    private void Start()
    {
        var deviceId = _settings.CastDeviceId ?? Guid.NewGuid().ToString();
        if (_settings.CastDeviceId is null) _store.Update(s => s.CastDeviceId = deviceId);
        CastSession? session = null;
        var lounge = new LoungeConnection(_http, new LoungeScreen(deviceId, _name), () => _settings.CastScreenId,
            id => _store.Update(s => s.CastScreenId = id), m => session!.HandleAsync(m), _lf.CreateLogger<LoungeConnection>());
        session = new CastSession(_player, lounge.Send, log: _lf.CreateLogger<CastSession>());
        lounge.SessionStarted += session.Reset;
        lounge.BoundChanged += RaiseChanged;
        _ssdp = new SsdpResponder(ip => $"http://{ip}:{_httpPort}/dial/dd.xml", _settings.InstallationId, _lf.CreateLogger<SsdpResponder>());
        _ssdp.Start();
        _session = session;
        _lounge = lounge;
        lounge.Start();
        _log.LogInformation("YouTube cast: on; this PC appears in the YouTube app's cast list as {Name}", _name);
    }

    private async Task StopAsync()
    {
        _ssdp?.Dispose();
        _ssdp = null;
        if (_lounge is { } lounge)
        {
            _lounge = null;
            await lounge.DisposeAsync().ConfigureAwait(false);
        }
        _session?.Dispose();
        _session = null;
        _log.LogInformation("YouTube cast: off");
    }

    /// <summary>The DIAL device description; <c>null</c> — casting is off (404).</summary>
    public string? DeviceDescription() => Active ? Dial.DeviceDescription(_name, _settings.InstallationId) : null;

    /// <summary>The status of the YouTube app; <c>null</c> — casting is off (404).</summary>
    public string? AppStatus() => Active ? Dial.AppStatus(Connected) : null;

    /// <summary>
    /// A phone launches the YouTube app with a pairing code, which is registered in the background (the phone waits
    /// for the screen in the lounge). Returns the HTTP status: 201 or 200 (already running), 400, 404 (off).
    /// </summary>
    public int Launch(string? body)
    {
        if (_lounge is not { } lounge) return 404;
        if (Dial.ParseLaunch(body) is not { } launch) return 400;
        var running = lounge.Bound;
        _log.LogInformation("YouTube cast: a phone opens YouTube on this PC ({Mode})", launch.Mode ?? "?");
        _ = Task.Run(async () =>
        {
            try
            {
                await lounge.RegisterPairingCodeAsync(launch.PairingCode, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
            {
                _log.LogWarning("YouTube cast: the phone's pairing code could not be registered: {Error}", ex.Message);
            }
        }, CancellationToken.None);
        return running ? 200 : 201;
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { _log.LogWarning(ex, "YouTube cast: Changed handler failed"); }
    }

    public async ValueTask DisposeAsync()
    {
        await _switch.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            if (_lounge is not null) await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            _switch.Release();
        }
        _http.Dispose();
    }
}
