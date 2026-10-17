using Microsoft.Extensions.Logging;
using Sofabird.Companion.Interop;
using Sofabird.Core.Localization;
using Sofabird.Core.Protocol;
using Sofabird.Core.Session;
using Sofabird.Core.YouTube;

namespace Sofabird.Companion.YouTube;

/// <summary>
/// YouTube on the TV: a link from the phone opens in the TV browser in kiosk mode, the window is moved to the TV and
/// brought to the front, and the player made fullscreen once the video itself plays. With the TV extension connected
/// (<see cref="TvExtensionBridge"/>) the page reports the exact state, an ad included, and carries out the commands;
/// without it Windows' media controls (SMTC) do that, and YouTube's own "f" key makes the player fullscreen.
/// </summary>
public sealed class YouTubeService : IDisposable
{
    private static readonly TimeSpan FrontTimeout = TimeSpan.FromSeconds(30);
    /// <summary>How long to wait for the video itself (ads come first) before giving up on fullscreen.</summary>
    private static readonly TimeSpan PlayingWait = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan LearnEvery = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(15);
    private const string SignInUrl = "https://accounts.google.com/ServiceLogin?service=youtube&continue=https%3A%2F%2Fwww.youtube.com%2F";

    private readonly TvBrowser _browser;
    private readonly YouTubeMediaSession _media;
    private readonly TvExtensionBridge _page;
    private readonly TvAddons _addons;
    private readonly FirefoxInstaller _installer;
    private readonly Settings _settings;
    private readonly SettingsStore _store;
    private readonly string _dataDir;
    private readonly ILogger _log;
    private CancellationTokenSource? _fullscreen;
    private string? _videoId;
    private DateTime _learntAt;

    public YouTubeService(TvBrowser browser, YouTubeMediaSession media, TvExtensionBridge page, TvAddons addons,
        FirefoxInstaller installer, Settings settings, SettingsStore store, string dataDir, ILogger<YouTubeService> log)
    {
        _browser = browser;
        _media = media;
        _page = page;
        _addons = addons;
        _installer = installer;
        _settings = settings;
        _store = store;
        _dataDir = dataDir;
        _log = log;
        _media.Changed += () => Changed?.Invoke();
        _page.Changed += () => Changed?.Invoke();
    }

    /// <summary>Something changed on the TV (phones get the new state).</summary>
    public event Action? Changed;

    /// <summary>Moves a window of the TV browser to the TV (set by the host: the monitor VLC opens on); no-op there.</summary>
    public Action<IntPtr>? PlaceOnTv { get; set; }

    /// <summary>The TV browser's window, or zero when it is not open.</summary>
    public IntPtr Window() => Available ? _browser.FindWindow() : IntPtr.Zero;

    /// <summary>A browser for YouTube is installed (the <c>youtube</c> capability).</summary>
    public bool Available => _browser.ExePath is not null;

    /// <summary>The TV browser (Firefox's path), for the setup window; <c>null</c> — none installed.</summary>
    public string? BrowserPath => _browser.ExePath;

    /// <summary>Ads on YouTube are blocked with uBlock Origin.</summary>
    public bool BlockAds => _settings.BlockAds;

    /// <summary>
    /// Turns uBlock Origin on or off (the setup window, the phone). Turning it on downloads it first; an open TV browser
    /// is restarted so that it applies now, and continues the video where it was.
    /// </summary>
    public async Task SetBlockAdsAsync(bool on, CancellationToken ct)
    {
        if (on && !await _addons.EnsureUBlockAsync(ct).ConfigureAwait(false))
            throw ProtocolException.BadRequest(ProtocolText.YouTubeAdBlockFailed);
        if (_settings.BlockAds == on) return;
        _store.Update(s => s.BlockAds = on);
        _log.LogInformation("YouTube on the TV: ads {Blocked}", on ? "blocked (uBlock Origin)" : "not blocked");
        Changed?.Invoke();
        if (Available && _browser.IsRunning()) await RestartAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Closes the TV browser and opens the video it showed at the same position (add-ons change at a start).</summary>
    private async Task RestartAsync(CancellationToken ct)
    {
        var page = _page.State;
        var videoId = page?.VideoId ?? _videoId;
        var at = page is { Watching: true, Ad: false } ? (int)page.TimeAt(DateTime.UtcNow) : 0;
        Close();
        var deadline = DateTime.UtcNow + CloseWait;
        while (_browser.IsRunning())
        {
            if (DateTime.UtcNow > deadline)
            {
                _log.LogWarning("YouTube on the TV: the browser did not close; the change applies at its next start");
                return;
            }
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        if (videoId is not null) Open($"https://www.youtube.com/watch?v={videoId}&t={at}s");
    }

    /// <summary>Installs Firefox for the TV (the setup window); <c>null</c> — done, otherwise what went wrong.</summary>
    public async Task<string?> InstallBrowserAsync(IProgress<FirefoxInstallStep>? progress, CancellationToken ct)
    {
        var error = await _installer.InstallAsync(progress, ct).ConfigureAwait(false);
        _browser.Refresh();
        if (error is null && Environment.ProcessPath is { } exe) TvExtension.Register(exe, _dataDir, _log);
        Changed?.Invoke();
        return error;
    }

    /// <summary>
    /// Opens the TV browser's profile in a normal window on the PC's screen at Google's sign-in for YouTube (keyboard and
    /// mouse at the PC). The sign-in stays in the profile.
    /// </summary>
    public void SignIn()
    {
        if (!Available) return;
        _browser.OpenForSignIn(SignInUrl);
    }

    /// <summary>Opens the video found in <paramref name="text"/> (a link or the text the YouTube app shares) on the TV.</summary>
    public void Open(string text)
    {
        if (!Available) throw ProtocolException.Unsupported(ProtocolText.YouTubeNoBrowser);
        var video = YouTubeLink.Parse(text) ?? throw ProtocolException.BadRequest(ProtocolText.YouTubeBadLink);
        _videoId = video.VideoId;
        // The extension loads it into the TV tab; otherwise (or when the browser is closed) the browser is started.
        if (!(_browser.IsRunning() && _page.Send("open", url: video.WatchUrl))) _browser.Open(video.WatchUrl);
        else _log.LogInformation("TV browser: loading {Url} through the extension", video.WatchUrl);
        _fullscreen?.Cancel();
        _fullscreen?.Dispose();
        _fullscreen = new CancellationTokenSource(FrontTimeout + PlayingWait);
        var ct = _fullscreen.Token;
        _ = Task.Run(() => FrontAndFullscreenAsync(video.VideoId, ct), CancellationToken.None); // outlives the request
        Changed?.Invoke();
    }

    /// <summary>
    /// The browser starts behind VLC (the companion runs in the background) and on the primary monitor: waits for its
    /// window, moves it to the TV, brings it to the front, waits until the video itself plays and makes the player
    /// fullscreen. Ads come first: the extension tells them apart; through SMTC an ad reports its own title, so there
    /// "the video plays" means the media session's title starts the window's title (which is the page's).
    /// </summary>
    private async Task FrontAndFullscreenAsync(string videoId, CancellationToken ct)
    {
        try
        {
            var window = IntPtr.Zero;
            while (window == IntPtr.Zero)
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
                window = _browser.FindWindow();
            }
            PlaceOnTv?.Invoke(window);
            ForegroundWindow.Activate(window);
            var deadline = DateTime.UtcNow + PlayingWait;
            var video = false;
            while (!video && DateTime.UtcNow < deadline)
            {
                await Task.Delay(700, ct).ConfigureAwait(false);
                if (_page.State is { } page)
                {
                    video = page is { Watching: true, Ad: false, Playing: true } && page.VideoId == videoId;
                    continue;
                }
                var title = _browser.WindowTitle();
                await _media.LearnAsync(title).ConfigureAwait(false);
                video = _media.Read() is { Playing: true, Title: { } playing } && title?.StartsWith(playing, StringComparison.Ordinal) == true;
            }
            if (!video)
            {
                _log.LogWarning("YouTube on the TV: the video did not start in {Wait}; no fullscreen", PlayingWait);
                return;
            }
            await Task.Delay(800, ct).ConfigureAwait(false); // the player settles after it starts
            MakeFullscreen();
            Changed?.Invoke();
        }
        catch (OperationCanceledException)
        {
            // a newer link took over, or the browser never showed up
        }
    }

    public async Task ControlAsync(string action, double? time)
    {
        if (!_browser.IsRunning()) throw ProtocolException.BadRequest(ProtocolText.YouTubeNotOpen);
        if (action == "seek" && time is null) throw ProtocolException.BadRequest("seek requires 'time'");
        var ok = action switch
        {
            "fullscreen" => MakeFullscreen(),
            "close" => Close(),
            "play" or "pause" or "toggle" or "seek" or "next" or "previous" or "skipAd" when _page.Connected => _page.Send(action, time),
            "skipAd" => false, // only the extension sees the ad's button
            "play" => await _media.PlayAsync().ConfigureAwait(false),
            "pause" => await _media.PauseAsync().ConfigureAwait(false),
            "toggle" => await _media.TogglePlayPauseAsync().ConfigureAwait(false),
            "next" => await _media.NextAsync().ConfigureAwait(false),
            "previous" => await _media.PreviousAsync().ConfigureAwait(false),
            "seek" => await _media.SeekAsync(time!.Value).ConfigureAwait(false),
            _ => throw ProtocolException.BadRequest($"unknown youtube action '{action}'"),
        };
        if (!ok) _log.LogDebug("YouTube {Action} was not accepted", action);
        Changed?.Invoke();
    }

    /// <summary>
    /// The player fullscreen on the TV (also the phone's "Fullscreen" button): the browser moved to the TV and brought to
    /// the front, then the extension's fullscreen, or YouTube's "f" key without it.
    /// </summary>
    private bool MakeFullscreen()
    {
        var window = _browser.FindWindow();
        if (window != IntPtr.Zero) PlaceOnTv?.Invoke(window); // kiosk mode may have gone fullscreen where it opened
        if (window == IntPtr.Zero || !ForegroundWindow.Activate(window))
        {
            _log.LogWarning("YouTube on the TV: the browser could not be brought to the front");
            if (window == IntPtr.Zero || !_page.Connected) return false;
        }
        if (_page.Send("fullscreen"))
        {
            _log.LogInformation("YouTube on the TV: fullscreen through the extension");
            _ = Task.Run(() => CheckFullscreenAsync(window), CancellationToken.None);
            return true;
        }
        ForegroundWindow.PressKey(NativeMethods.VK_F);
        _log.LogInformation("YouTube on the TV: fullscreen key");
        return true;
    }

    /// <summary>The page did not go fullscreen (the browser refused it): YouTube's "f" key, a real key press, instead.</summary>
    private async Task CheckFullscreenAsync(IntPtr window)
    {
        await Task.Delay(2000).ConfigureAwait(false);
        if (_page.State is not { Watching: true, Fullscreen: false }) return;
        if (window == IntPtr.Zero || !ForegroundWindow.Activate(window)) return;
        ForegroundWindow.PressKey(NativeMethods.VK_F);
        _log.LogInformation("YouTube on the TV: the page is not fullscreen, fullscreen key");
    }

    /// <summary>VLC starts something from the phone: the TV browser goes away so that VLC is what the TV shows.</summary>
    public void CloseForVlc()
    {
        if (!Available || !_browser.IsRunning()) return;
        _log.LogInformation("YouTube on the TV: closed, VLC takes the TV");
        Close();
        Changed?.Invoke();
    }

    private bool Close()
    {
        _fullscreen?.Cancel();
        _browser.Close();
        return true;
    }

    /// <summary><c>state.youtube</c>; <c>null</c> without a browser.</summary>
    public YouTubeInfo? Info()
    {
        if (!Available) return null;
        var info = Read();
        info.AdBlock = _settings.BlockAds;
        return info;
    }

    private YouTubeInfo Read()
    {
        var open = _browser.IsRunning();
        if (!open) return new YouTubeInfo { Open = false, VideoId = _videoId };
        if (_page.State is { } page) return FromPage(page);
        var playback = _media.Read();
        if (playback is null && DateTime.UtcNow - _learntAt > LearnEvery)
        {
            // The session is learnt from the window title, which matches only once the video (not an ad) plays.
            _learntAt = DateTime.UtcNow;
            _ = _media.LearnAsync(_browser.WindowTitle());
        }
        return new YouTubeInfo
        {
            Open = true,
            Playing = playback?.Playing == true,
            VideoId = _videoId,
            Title = playback?.Title,
            Channel = playback?.Channel,
            Time = playback?.Time is double t ? Math.Round(t, 1) : null,
            Length = playback?.Length is double l ? Math.Round(l, 1) : null,
            CanNext = playback?.CanNext == true,
            CanPrevious = playback?.CanPrevious == true,
        };
    }

    /// <summary>The page's own report: the video it shows (also the next one YouTube played by itself), an ad.</summary>
    private YouTubeInfo FromPage(TvPageState page)
    {
        if (!page.Watching) return new YouTubeInfo { Open = true, VideoId = _videoId };
        return new YouTubeInfo
        {
            Open = true,
            Playing = page.Playing,
            VideoId = page.VideoId ?? _videoId,
            Title = page.Title,
            Channel = page.Channel,
            Time = Math.Round(page.TimeAt(DateTime.UtcNow), 1),
            Length = page.Duration > 0 ? Math.Round(page.Duration, 1) : null,
            CanNext = page.CanNext,
            CanPrevious = page.CanPrevious,
            Ad = page.Ad,
            AdSkippable = page.AdSkippable,
        };
    }

    public void Dispose()
    {
        _fullscreen?.Cancel();
        _fullscreen?.Dispose();
    }
}
