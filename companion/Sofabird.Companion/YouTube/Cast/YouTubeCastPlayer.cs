using System.Globalization;
using Microsoft.Extensions.Logging;
using Sofabird.Companion.Services;
using Sofabird.Core.Protocol;
using Sofabird.Core.Session;
using Sofabird.Core.YouTube.Cast;

namespace Sofabird.Companion.YouTube.Cast;

/// <summary>
/// A cast plays on the companion's YouTube on the TV (<see cref="YouTubeService"/>: the TV browser with its add-ons,
/// history and the phone remote); its volume is the PC's.
/// </summary>
public sealed class YouTubeCastPlayer : ICastPlayer
{
    private readonly YouTubeService _youtube;
    private readonly AudioService _audio;
    private readonly Func<Task> _beforeOpen;
    private readonly Action _volumeChanged;
    private readonly ILogger _log;

    /// <param name="beforeOpen">Before a video opens (VLC pauses: the browser covers it).</param>
    /// <param name="volumeChanged">After the volume changed (the phone remote shows it).</param>
    public YouTubeCastPlayer(YouTubeService youtube, AudioService audio, Func<Task> beforeOpen, Action volumeChanged, ILogger log)
    {
        _youtube = youtube;
        _audio = audio;
        _beforeOpen = beforeOpen;
        _volumeChanged = volumeChanged;
        _log = log;
    }

    public event Action? Changed
    {
        add => _youtube.Changed += value;
        remove => _youtube.Changed -= value;
    }

    public event Action? Ended
    {
        add => _youtube.VideoEnded += value;
        remove => _youtube.VideoEnded -= value;
    }

    public CastPlayback Playback => From(_youtube.Info());

    /// <summary>The TV's state as a cast sees it: an ad, or a video not playing yet, is "loading".</summary>
    internal static CastPlayback From(YouTubeInfo? info)
    {
        if (info is not { Open: true, VideoId: { } id }) return CastPlayback.Nothing;
        if (info.Ad || info.Time is null) return new CastPlayback(id, CastState.Loading, 0, 0);
        return new CastPlayback(id, info.Playing ? CastState.Playing : CastState.Paused, info.Time.Value, info.Length ?? 0,
            info.CanNext, info.CanPrevious);
    }

    public CastVolume Volume => _audio.GetSystemVolume() is { } v ? new CastVolume(v.Percent, v.Muted) : new CastVolume(100, false);

    public async Task OpenAsync(string videoId, double startSeconds)
    {
        await _beforeOpen().ConfigureAwait(false);
        var at = ((int)Math.Max(0, startSeconds)).ToString(CultureInfo.InvariantCulture);
        _youtube.Open($"https://www.youtube.com/watch?v={videoId}&t={at}s");
    }

    public Task PlayAsync() => ControlAsync("play");

    public Task PauseAsync() => ControlAsync("pause");

    public Task SeekAsync(double seconds) => ControlAsync("seek", seconds);

    public Task StopAsync() => ControlAsync("close");

    public Task NextAsync() => ControlAsync("next");

    public Task PreviousAsync() => ControlAsync("previous");

    public void SetVolume(int percent, bool muted)
    {
        var current = _audio.GetSystemVolume();
        if (current?.Muted != muted) _audio.SetSystemMute(muted);
        if (current?.Percent != percent) _audio.SetSystemVolume(percent);
        _volumeChanged();
    }

    private async Task ControlAsync(string action, double? time = null)
    {
        try
        {
            await _youtube.ControlAsync(action, time).ConfigureAwait(false);
        }
        catch (ProtocolException ex)
        {
            // The TV browser was closed meanwhile: nothing to control.
            _log.LogDebug("YouTube cast: {Action} not done: {Error}", action, ex.Message);
        }
    }
}
