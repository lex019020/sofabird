using Microsoft.Extensions.Logging;
using Windows.Media.Control;

namespace Sofabird.Companion.YouTube;

/// <summary>What the TV browser's media session reports.</summary>
public sealed record YouTubePlayback(string? Title, string? Channel, bool Playing, double? Time, double? Length, bool CanNext, bool CanPrevious);

/// <summary>
/// The TV browser's YouTube as Windows' media controls (SMTC) see it: Firefox publishes the page's media session with the
/// title, channel, position and play/pause/seek/next. The session of the TV profile is told apart from other players by
/// its id, learnt once by matching its title with the TV browser's window title and kept in the settings.
/// </summary>
public sealed class YouTubeMediaSession : IDisposable
{
    private readonly ILogger _log;
    private readonly Action<string> _rememberSessionId;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private readonly List<GlobalSystemMediaTransportControlsSession> _hooked = [];
    private readonly object _gate = new();
    private string? _sessionId;
    private (string? Title, string? Channel) _media;

    public YouTubeMediaSession(string? sessionId, Action<string> rememberSessionId, ILogger<YouTubeMediaSession> log)
    {
        _sessionId = sessionId;
        _rememberSessionId = rememberSessionId;
        _log = log;
    }

    /// <summary>Something changed in the TV browser's media session.</summary>
    public event Action? Changed;

    public async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.SessionsChanged += (_, _) => Rehook();
            Rehook();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Windows media controls are not available");
        }
    }

    /// <summary>
    /// Learns the TV profile's session from the browser's window title ("&lt;video&gt; - YouTube — Mozilla Firefox"):
    /// the session whose title starts it. Called while the TV browser shows a video.
    /// </summary>
    public async Task LearnAsync(string? windowTitle)
    {
        if (_manager is null || string.IsNullOrEmpty(windowTitle)) return;
        foreach (var s in _manager.GetSessions())
        {
            try
            {
                var props = await s.TryGetMediaPropertiesAsync();
                if (string.IsNullOrEmpty(props.Title) || !windowTitle.StartsWith(props.Title, StringComparison.Ordinal)) continue;
                if (s.SourceAppUserModelId == _sessionId) return;
                _sessionId = s.SourceAppUserModelId;
                _log.LogInformation("TV browser media session: {Id}", _sessionId);
                _rememberSessionId(_sessionId);
                await RefreshMediaAsync().ConfigureAwait(false);
                Changed?.Invoke();
                return;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                _log.LogDebug(ex, "Reading a media session failed");
            }
        }
    }

    private GlobalSystemMediaTransportControlsSession? Ours()
    {
        if (_manager is null || _sessionId is null) return null;
        try { return _manager.GetSessions().FirstOrDefault(s => s.SourceAppUserModelId == _sessionId); }
        catch (System.Runtime.InteropServices.COMException) { return null; }
    }

    /// <summary>The current playback, the position moved on to now while playing; <c>null</c> — no session.</summary>
    public YouTubePlayback? Read()
    {
        var s = Ours();
        if (s is null) return null;
        try
        {
            var info = s.GetPlaybackInfo();
            var timeline = s.GetTimelineProperties();
            var playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            double? length = timeline.EndTime > TimeSpan.Zero ? timeline.EndTime.TotalSeconds : null;
            double? time = null;
            if (length is not null)
            {
                var t = timeline.Position.TotalSeconds;
                if (playing && timeline.LastUpdatedTime > DateTimeOffset.MinValue)
                    t += Math.Max(0, (DateTimeOffset.Now - timeline.LastUpdatedTime).TotalSeconds) * (info.PlaybackRate ?? 1);
                time = Math.Min(t, length.Value);
            }
            (string? title, string? channel) media;
            lock (_gate) media = _media;
            return new YouTubePlayback(media.title, media.channel, playing, time, length,
                info.Controls.IsNextEnabled, info.Controls.IsPreviousEnabled);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            _log.LogDebug(ex, "Reading the media session failed");
            return null;
        }
    }

    public Task<bool> PlayAsync() => Run(s => s.TryPlayAsync().AsTask());
    public Task<bool> PauseAsync() => Run(s => s.TryPauseAsync().AsTask());
    public Task<bool> TogglePlayPauseAsync() => Run(s => s.TryTogglePlayPauseAsync().AsTask());
    public Task<bool> NextAsync() => Run(s => s.TrySkipNextAsync().AsTask());
    public Task<bool> PreviousAsync() => Run(s => s.TrySkipPreviousAsync().AsTask());
    public Task<bool> SeekAsync(double seconds) =>
        Run(s => s.TryChangePlaybackPositionAsync(TimeSpan.FromSeconds(Math.Max(0, seconds)).Ticks).AsTask());

    private async Task<bool> Run(Func<GlobalSystemMediaTransportControlsSession, Task<bool>> action)
    {
        var s = Ours();
        if (s is null) return false;
        try { return await action(s).ConfigureAwait(false); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            _log.LogDebug(ex, "A media session command failed");
            return false;
        }
    }

    /// <summary>Follows every session's changes (the TV profile's one is picked when reading).</summary>
    private void Rehook()
    {
        if (_manager is null) return;
        lock (_gate)
        {
            foreach (var s in _hooked)
            {
                s.MediaPropertiesChanged -= OnMediaChanged;
                s.PlaybackInfoChanged -= OnPlaybackChanged;
                s.TimelinePropertiesChanged -= OnTimelineChanged;
            }
            _hooked.Clear();
            foreach (var s in _manager.GetSessions())
            {
                s.MediaPropertiesChanged += OnMediaChanged;
                s.PlaybackInfoChanged += OnPlaybackChanged;
                s.TimelinePropertiesChanged += OnTimelineChanged;
                _hooked.Add(s);
            }
        }
        _ = RefreshMediaAsync();
        Changed?.Invoke();
    }

    private void OnMediaChanged(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e)
    {
        if (s.SourceAppUserModelId == _sessionId) _ = RefreshMediaAsync();
    }

    private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e)
    {
        if (s.SourceAppUserModelId == _sessionId) Changed?.Invoke();
    }

    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e)
    {
        if (s.SourceAppUserModelId == _sessionId) Changed?.Invoke();
    }

    /// <summary>Title and channel are read asynchronously, so they are cached for the state.</summary>
    private async Task RefreshMediaAsync()
    {
        var s = Ours();
        if (s is null) return;
        try
        {
            var props = await s.TryGetMediaPropertiesAsync();
            lock (_gate) _media = (NullIfEmpty(props.Title), NullIfEmpty(props.Artist));
            Changed?.Invoke();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            _log.LogDebug(ex, "Reading the media properties failed");
        }
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var s in _hooked)
            {
                s.MediaPropertiesChanged -= OnMediaChanged;
                s.PlaybackInfoChanged -= OnPlaybackChanged;
                s.TimelinePropertiesChanged -= OnTimelineChanged;
            }
            _hooked.Clear();
        }
    }
}
