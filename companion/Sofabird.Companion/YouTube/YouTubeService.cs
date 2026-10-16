using Microsoft.Extensions.Logging;
using Sofabird.Companion.Interop;
using Sofabird.Core.Localization;
using Sofabird.Core.Protocol;
using Sofabird.Core.Session;
using Sofabird.Core.YouTube;

namespace Sofabird.Companion.YouTube;

/// <summary>
/// YouTube on the TV: a link from the phone opens in the TV browser in kiosk mode, the window is brought to the front
/// and the player made fullscreen (YouTube's own "f" key); play/pause/seek/next go through Windows' media controls,
/// which also report what plays for <c>state.youtube</c>.
/// </summary>
public sealed class YouTubeService : IDisposable
{
    private static readonly TimeSpan FrontTimeout = TimeSpan.FromSeconds(30);
    /// <summary>How long to wait for the video itself (ads come first) before giving up on fullscreen.</summary>
    private static readonly TimeSpan PlayingWait = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan LearnEvery = TimeSpan.FromSeconds(3);

    private readonly TvBrowser _browser;
    private readonly YouTubeMediaSession _media;
    private readonly ILogger _log;
    private CancellationTokenSource? _fullscreen;
    private string? _videoId;
    private DateTime _learntAt;

    public YouTubeService(TvBrowser browser, YouTubeMediaSession media, ILogger<YouTubeService> log)
    {
        _browser = browser;
        _media = media;
        _log = log;
        _media.Changed += () => Changed?.Invoke();
    }

    /// <summary>Something changed on the TV (phones get the new state).</summary>
    public event Action? Changed;

    /// <summary>Moves a window of the TV browser to the TV (set by the host: the monitor VLC opens on); no-op there.</summary>
    public Action<IntPtr>? PlaceOnTv { get; set; }

    /// <summary>The TV browser's window, or zero when it is not open.</summary>
    public IntPtr Window() => Available ? _browser.FindWindow() : IntPtr.Zero;

    /// <summary>A browser for YouTube is installed (the <c>youtube</c> capability).</summary>
    public bool Available => _browser.ExePath is not null;

    /// <summary>Opens the video found in <paramref name="text"/> (a link or the text the YouTube app shares) on the TV.</summary>
    public void Open(string text)
    {
        if (!Available) throw ProtocolException.Unsupported(ProtocolText.YouTubeNoBrowser);
        var video = YouTubeLink.Parse(text) ?? throw ProtocolException.BadRequest(ProtocolText.YouTubeBadLink);
        _videoId = video.VideoId;
        _browser.Open(video.WatchUrl);
        _fullscreen?.Cancel();
        _fullscreen?.Dispose();
        _fullscreen = new CancellationTokenSource(FrontTimeout + PlayingWait);
        var ct = _fullscreen.Token;
        _ = Task.Run(() => FrontAndFullscreenAsync(ct), CancellationToken.None); // outlives the request
        Changed?.Invoke();
    }

    /// <summary>
    /// The browser starts behind VLC (the companion runs in the background): waits for its window, brings it to the
    /// front, waits until the video itself plays and presses "f" for YouTube's fullscreen player. Ads come first and
    /// report their own title, so "the video plays" means the media session's title starts the window's title (which
    /// is the page's). A link loaded into the open browser reloads the page, so this runs for every link.
    /// </summary>
    private async Task FrontAndFullscreenAsync(CancellationToken ct)
    {
        try
        {
            var window = IntPtr.Zero;
            while (window == IntPtr.Zero)
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
                window = _browser.FindWindow();
            }
            // Firefox opens on the primary monitor (the laptop's own screen), not where VLC plays.
            PlaceOnTv?.Invoke(window);
            ForegroundWindow.Activate(window);
            var deadline = DateTime.UtcNow + PlayingWait;
            var video = false;
            while (!video && DateTime.UtcNow < deadline)
            {
                await Task.Delay(700, ct).ConfigureAwait(false);
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
            PressFullscreen();
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
        var ok = action switch
        {
            "play" => await _media.PlayAsync().ConfigureAwait(false),
            "pause" => await _media.PauseAsync().ConfigureAwait(false),
            "toggle" => await _media.TogglePlayPauseAsync().ConfigureAwait(false),
            "next" => await _media.NextAsync().ConfigureAwait(false),
            "previous" => await _media.PreviousAsync().ConfigureAwait(false),
            "seek" => await _media.SeekAsync(time ?? throw ProtocolException.BadRequest("seek requires 'time'")).ConfigureAwait(false),
            "fullscreen" => PressFullscreen(),
            "close" => Close(),
            _ => throw ProtocolException.BadRequest($"unknown youtube action '{action}'"),
        };
        if (!ok) _log.LogDebug("YouTube {Action} was not accepted", action);
        Changed?.Invoke();
    }

    /// <summary>YouTube's "f" in the TV browser, brought to the front first (also the phone's "Fullscreen" button).</summary>
    private bool PressFullscreen()
    {
        var window = _browser.FindWindow();
        if (window != IntPtr.Zero) PlaceOnTv?.Invoke(window); // kiosk mode may have gone fullscreen where it opened
        if (window == IntPtr.Zero || !ForegroundWindow.Activate(window))
        {
            _log.LogWarning("YouTube on the TV: the browser could not be brought to the front");
            return false;
        }
        ForegroundWindow.PressKey(NativeMethods.VK_F);
        _log.LogInformation("YouTube on the TV: fullscreen key");
        return true;
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
        var open = _browser.IsRunning();
        if (!open) return new YouTubeInfo { Open = false, VideoId = _videoId };
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

    public void Dispose()
    {
        _fullscreen?.Cancel();
        _fullscreen?.Dispose();
    }
}
