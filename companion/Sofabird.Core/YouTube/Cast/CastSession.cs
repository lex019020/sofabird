using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sofabird.Core.YouTube.Cast;

/// <summary>A player state as the lounge protocol numbers it.</summary>
public enum CastState
{
    Idle = -1,
    Playing = 1,
    Paused = 2,
    /// <summary>Opening a video, or an ad before it.</summary>
    Loading = 3,
    Stopped = 4,
}

/// <summary>What the TV shows, for the phones that cast to it; <see cref="VideoId"/> <c>null</c> — nothing.</summary>
public sealed record CastPlayback(string? VideoId, CastState State, double Time, double Duration, bool CanNext = false, bool CanPrevious = false)
{
    public static readonly CastPlayback Nothing = new(null, CastState.Idle, 0, 0);
}

/// <summary>The PC's volume, 0–100.</summary>
public sealed record CastVolume(int Percent, bool Muted);

/// <summary>The player a cast drives: YouTube in the TV browser (the companion's YouTube on the TV).</summary>
public interface ICastPlayer
{
    CastPlayback Playback { get; }
    CastVolume Volume { get; }
    /// <summary>Anything but the position moving on changed (a seek included).</summary>
    event Action? Changed;
    /// <summary>The video played to its end.</summary>
    event Action? Ended;
    Task OpenAsync(string videoId, double startSeconds);
    Task PlayAsync();
    Task PauseAsync();
    Task SeekAsync(double seconds);
    /// <summary>"Stop casting": the video goes off the TV.</summary>
    Task StopAsync();
    /// <summary>YouTube's own next / previous video (its playlist or autoplay), when the cast's queue has none.</summary>
    Task NextAsync();
    Task PreviousAsync();
    void SetVolume(int percent, bool muted);
}

/// <summary>
/// The screen side of a cast from the YouTube app: carries out what the phones send through the lounge channel
/// (<c>setPlaylist</c>, <c>play</c>, <c>seekTo</c>, <c>setVolume</c>, …) on the <see cref="ICastPlayer"/>, keeps the
/// queue the phones build, and tells them what the TV does (<c>nowPlaying</c>, <c>onStateChange</c>,
/// <c>onVolumeChanged</c>) so that their mini-player, seek bar and volume follow it. Sending goes through the
/// delegate given to the constructor (non-blocking, in order).
/// </summary>
public sealed class CastSession : IDisposable
{
    /// <summary>After opening a video, how long another video reported by the page counts as the one before it.</summary>
    private static readonly TimeSpan PendingFor = TimeSpan.FromSeconds(20);
    /// <summary>A position this far from where it would be by now is a seek the phones should hear about.</summary>
    private const double SeekTolerance = 2.5;
    private const string Unsupported = "UNSUPPORTED";

    private readonly ICastPlayer _player;
    private readonly Action<IReadOnlyList<LoungeCommand>> _send;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private readonly HashSet<string> _remotes = new(StringComparer.Ordinal);
    private List<string> _queue = [];
    private int _index = -1;
    private string? _listId;
    private CastPlayback? _sent;
    private DateTimeOffset _sentAt;
    private string _cpn;
    private (string VideoId, double Start, DateTimeOffset Until)? _pending;

    public CastSession(ICastPlayer player, Action<IReadOnlyList<LoungeCommand>> send, TimeProvider? time = null, ILogger? log = null)
    {
        _player = player;
        _send = send;
        _time = time ?? TimeProvider.System;
        _log = log ?? NullLogger.Instance;
        _cpn = NewCpn();
        _player.Changed += Report;
        _player.Ended += OnEnded;
    }

    public void Dispose()
    {
        _player.Changed -= Report;
        _player.Ended -= OnEnded;
    }

    private void OnEnded() => _ = AdvanceAsync();

    /// <summary>The phones connected to this screen now.</summary>
    public int Remotes
    {
        get { lock (_gate) return _remotes.Count; }
    }

    /// <summary>A new lounge session starts: who is connected comes again with its <c>loungeStatus</c>.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _remotes.Clear();
            _sent = null;
        }
    }

    public async Task HandleAsync(LoungeMessage m)
    {
        switch (m.Name)
        {
            case "loungeStatus":
                SetRemotes(RemoteIds(m.Get("devices")));
                break;
            case "remoteConnected":
                Connected(m);
                break;
            case "remoteDisconnected":
                if (m.Get("id") is { } gone)
                {
                    lock (_gate) _remotes.Remove(gone);
                    _log.LogInformation("YouTube cast: {Name} disconnected", m.Get("name") ?? "a phone");
                }
                break;
            case "getNowPlaying":
                _send([NowPlaying(Current())]);
                break;
            case "getVolume":
                _send([VolumeChanged()]);
                break;
            case "setPlaylist":
                await SetPlaylistAsync(m).ConfigureAwait(false);
                break;
            case "updatePlaylist":
                UpdatePlaylist(m);
                break;
            case "play":
                await _player.PlayAsync().ConfigureAwait(false);
                break;
            case "pause":
                await _player.PauseAsync().ConfigureAwait(false);
                break;
            case "seekTo":
                if (m.GetNumber("newTime") is double at) await _player.SeekAsync(Math.Max(0, at)).ConfigureAwait(false);
                break;
            case "stopVideo":
                lock (_gate)
                {
                    _queue = [];
                    _index = -1;
                    _pending = null;
                }
                await _player.StopAsync().ConfigureAwait(false);
                break;
            case "next":
                await StepAsync(+1).ConfigureAwait(false);
                break;
            case "previous":
                await StepAsync(-1).ConfigureAwait(false);
                break;
            case "setVolume":
                SetVolume(m);
                break;
            case "setAutoplayMode":
                _send([Autoplay()]);
                break;
            default:
                _log.LogDebug("YouTube cast: {Name} not handled", m.Name);
                break;
        }
    }

    private void Connected(LoungeMessage m)
    {
        if (m.Get("id") is not { } id) return;
        lock (_gate)
        {
            _remotes.Add(id);
            _sent = null; // the phone gets everything
        }
        _log.LogInformation("YouTube cast: {Name} connected", m.Get("name") ?? "a phone");
        var now = Current();
        var commands = new List<LoungeCommand> { Autoplay(), HasPreviousNext(now), NowPlaying(now), VolumeChanged() };
        if (now.VideoId is not null) commands.Add(StateChange(now));
        lock (_gate)
        {
            _sent = now;
            _sentAt = _time.GetUtcNow();
        }
        _send(commands);
    }

    private void SetRemotes(IReadOnlyList<string> ids)
    {
        lock (_gate)
        {
            _remotes.Clear();
            _remotes.UnionWith(ids);
        }
    }

    private async Task SetPlaylistAsync(LoungeMessage m)
    {
        var videoId = m.Get("videoId");
        if (!YouTubeLink.IsVideoId(videoId))
        {
            _log.LogWarning("YouTube cast: setPlaylist without a valid video id");
            return;
        }
        var ids = (m.Get("videoIds") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Where(YouTubeLink.IsVideoId).ToList();
        var index = int.TryParse(m.Get("currentIndex"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : -1;
        if (index < 0 || index >= ids.Count || ids[index] != videoId) index = ids.IndexOf(videoId!);
        if (index < 0)
        {
            ids = [videoId!];
            index = 0;
        }
        var start = Math.Max(0, m.GetNumber("currentTime") ?? 0);
        var playing = _player.Playback;
        var same = playing.VideoId == videoId && playing.State is CastState.Playing or CastState.Paused or CastState.Loading;
        lock (_gate)
        {
            _queue = ids;
            _index = index;
            _listId = m.Get("listId");
            if (!same) _pending = (videoId!, start, _time.GetUtcNow() + PendingFor);
            _sent = null;
        }
        _log.LogInformation("YouTube cast: playing {VideoId} from {Start:0}s ({Count} in the queue)", videoId, start, ids.Count);
        Report();
        if (!same) await _player.OpenAsync(videoId!, start).ConfigureAwait(false);
        else if (Math.Abs(playing.Time - start) > 5) await _player.SeekAsync(start).ConfigureAwait(false);
    }

    private void UpdatePlaylist(LoungeMessage m)
    {
        var ids = (m.Get("videoIds") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Where(YouTubeLink.IsVideoId).ToList();
        var current = Current().VideoId;
        lock (_gate)
        {
            _queue = ids;
            _listId = m.Get("listId");
            _index = current is null ? -1 : ids.IndexOf(current);
        }
        var now = Current();
        _send([NowPlaying(now), HasPreviousNext(now)]);
    }

    /// <summary>The next or previous video of the queue; YouTube's own when the queue has none.</summary>
    private async Task StepAsync(int delta)
    {
        string? target = null;
        var current = _player.Playback.VideoId;
        lock (_gate)
        {
            if (_index >= 0 && _index < _queue.Count && _queue[_index] == current && _index + delta >= 0 && _index + delta < _queue.Count)
            {
                _index += delta;
                target = _queue[_index];
                _pending = (target, 0, _time.GetUtcNow() + PendingFor);
            }
        }
        if (target is null)
        {
            await (delta > 0 ? _player.NextAsync() : _player.PreviousAsync()).ConfigureAwait(false);
            return;
        }
        Report();
        await _player.OpenAsync(target, 0).ConfigureAwait(false);
    }

    /// <summary>A queued video ended: the next one of the queue (otherwise YouTube goes on by itself).</summary>
    private async Task AdvanceAsync()
    {
        try
        {
            bool more;
            lock (_gate) more = _remotes.Count > 0 && _index >= 0 && _index + 1 < _queue.Count;
            if (more) await StepAsync(+1).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "YouTube cast: the next video of the queue did not open");
        }
    }

    private void SetVolume(LoungeMessage m)
    {
        var current = _player.Volume;
        var percent = m.GetNumber("volume") is double v ? (int)Math.Clamp(Math.Round(v), 0, 100) : current.Percent;
        var muted = m.Get("muted") is { } s ? s.Equals("true", StringComparison.OrdinalIgnoreCase) : current.Muted;
        _player.SetVolume(percent, muted);
        _send([VolumeChanged()]);
    }

    /// <summary>Tells the phones what changed on the TV (called by the player; also after a command).</summary>
    public void Report()
    {
        List<LoungeCommand> commands;
        lock (_gate)
        {
            var now = CurrentLocked();
            var previous = _sent;
            var sentAt = _sentAt;
            _sent = now;
            _sentAt = _time.GetUtcNow();
            if (_remotes.Count == 0) return;
            commands = Diff(previous, sentAt, now);
        }
        if (commands.Count > 0) _send(commands);
    }

    private List<LoungeCommand> Diff(CastPlayback? previous, DateTimeOffset sentAt, CastPlayback now)
    {
        var commands = new List<LoungeCommand>();
        var videoChanged = previous?.VideoId != now.VideoId;
        if (videoChanged) _cpn = NewCpn();
        if (now.VideoId is null)
        {
            if (videoChanged) commands.Add(NowPlaying(now));
            return commands;
        }
        var stateChanged = previous?.State != now.State;
        if (videoChanged || (stateChanged && previous!.State != CastState.Playing))
            commands.AddRange([NowPlaying(now), HasPreviousNext(now)]);
        else if (previous!.CanNext != now.CanNext || previous.CanPrevious != now.CanPrevious)
            commands.Add(HasPreviousNext(now));
        var expected = previous is null ? now.Time
            : previous.State == CastState.Playing ? previous.Time + (_time.GetUtcNow() - sentAt).TotalSeconds : previous.Time;
        var moved = Math.Abs(now.Time - expected) > SeekTolerance;
        var lengthChanged = previous is null || Math.Abs(previous.Duration - now.Duration) > 0.5;
        if (videoChanged || stateChanged || moved || lengthChanged) commands.Add(StateChange(now));
        return commands;
    }

    private CastPlayback Current()
    {
        lock (_gate) return CurrentLocked();
    }

    /// <summary>The player's state, with a video just opened standing in for the one the page still shows.</summary>
    private CastPlayback CurrentLocked()
    {
        var now = _player.Playback;
        if (_pending is not { } pending) return now;
        if (now.VideoId == pending.VideoId || _time.GetUtcNow() > pending.Until)
        {
            _pending = null;
            return now;
        }
        return new CastPlayback(pending.VideoId, CastState.Loading, pending.Start, 0);
    }

    private (string? ListId, int Index) QueuePosition(string? videoId)
    {
        lock (_gate)
        {
            if (videoId is null || _queue.Count == 0) return (null, -1);
            if (_index < 0 || _index >= _queue.Count || _queue[_index] != videoId) _index = _queue.IndexOf(videoId);
            return _index < 0 ? (null, -1) : (_listId, _index);
        }
    }

    private LoungeCommand NowPlaying(CastPlayback p)
    {
        if (p.VideoId is null) return new LoungeCommand("nowPlaying");
        var fields = PlaybackFields(p);
        fields.Insert(0, new("videoId", p.VideoId));
        var (listId, index) = QueuePosition(p.VideoId);
        if (index >= 0)
        {
            if (!string.IsNullOrEmpty(listId)) fields.Add(new("listId", listId));
            fields.Add(new("currentIndex", Num(index)));
        }
        return new LoungeCommand("nowPlaying", fields);
    }

    private LoungeCommand StateChange(CastPlayback p) => new("onStateChange", PlaybackFields(p));

    private List<KeyValuePair<string, string>> PlaybackFields(CastPlayback p)
    {
        var loaded = p.State is CastState.Playing or CastState.Paused or CastState.Loading ? p.Duration : 0;
        return
        [
            new("state", Num((int)p.State)),
            new("currentTime", Num(p.Time)),
            new("duration", Num(p.Duration)),
            new("cpn", _cpn),
            new("loadedTime", Num(loaded)),
            new("seekableStartTime", "0"),
            new("seekableEndTime", Num(p.Duration)),
        ];
    }

    private LoungeCommand HasPreviousNext(CastPlayback p)
    {
        var (_, index) = QueuePosition(p.VideoId);
        int count;
        lock (_gate) count = _queue.Count;
        var previous = index > 0 || p.CanPrevious;
        var next = (index >= 0 && index + 1 < count) || p.CanNext;
        return new LoungeCommand("onHasPreviousNextChanged", [new("hasPrevious", Bool(previous)), new("hasNext", Bool(next))]);
    }

    private LoungeCommand VolumeChanged()
    {
        var v = _player.Volume;
        return new LoungeCommand("onVolumeChanged", [new("volume", Num(v.Percent)), new("muted", Bool(v.Muted))]);
    }

    private static LoungeCommand Autoplay() => new("onAutoplayModeChanged", [new("autoplayMode", Unsupported)]);

    /// <summary>The ids of the phones in a <c>loungeStatus</c>'s device list (a JSON string).</summary>
    internal static IReadOnlyList<string> RemoteIds(string? devices)
    {
        if (string.IsNullOrEmpty(devices)) return [];
        try
        {
            using var doc = JsonDocument.Parse(devices);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            return doc.RootElement.EnumerateArray()
                .Where(d => d.ValueKind == JsonValueKind.Object && d.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                    && t.GetString() == "REMOTE_CONTROL" && d.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                .Select(d => d.GetProperty("id").GetString()!)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string Num(double value) => Math.Round(value, 3).ToString(CultureInfo.InvariantCulture);

    private static string Bool(bool value) => value ? "true" : "false";

    /// <summary>A "client playback nonce": 16 characters, a new one for every video.</summary>
    private static string NewCpn()
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        return string.Create(16, 0, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = chars[Random.Shared.Next(chars.Length)];
        });
    }
}
