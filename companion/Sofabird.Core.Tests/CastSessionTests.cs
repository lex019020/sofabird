using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Sofabird.Core.YouTube.Cast;
using Xunit;

namespace Sofabird.Core.Tests;

public sealed class CastSessionTests : IDisposable
{
    private const string Bunny = "aqz-KE-bpKQ";
    private const string Zoo = "jNQXAC9IVRw";

    private readonly FakePlayer _player = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 23, 20, 0, 0, TimeSpan.Zero));
    private readonly List<LoungeCommand> _sent = new();
    private readonly CastSession _session;

    public CastSessionTests() => _session = new CastSession(_player, c => _sent.AddRange(c), _time);

    public void Dispose() => _session.Dispose();

    private static LoungeMessage Msg(string name, object? fields = null)
    {
        var args = fields is null ? new List<JsonElement>() : [JsonSerializer.SerializeToElement(fields)];
        return new LoungeMessage(1, name, args);
    }

    private Task Connect() => _session.HandleAsync(Msg("remoteConnected", new { id = "phone-1", name = "Pixel", type = "REMOTE_CONTROL" }));

    private LoungeCommand Last(string name) => _sent.Last(c => c.Name == name);

    private static string? Field(LoungeCommand c, string key) => c.Fields.FirstOrDefault(f => f.Key == key).Value;

    [Fact]
    public async Task A_phone_that_connects_hears_the_whole_state()
    {
        _player.Playback = new CastPlayback(Bunny, CastState.Playing, 30, 600);
        await Connect();
        Assert.Equal(1, _session.Remotes);
        Assert.Equal(["onAutoplayModeChanged", "onHasPreviousNextChanged", "nowPlaying", "onVolumeChanged", "onStateChange"], _sent.Select(c => c.Name));
        Assert.Equal(Bunny, Field(Last("nowPlaying"), "videoId"));
        Assert.Equal("1", Field(Last("nowPlaying"), "state"));
        Assert.Equal("30", Field(Last("onStateChange"), "currentTime"));
        Assert.Equal("UNSUPPORTED", Field(Last("onAutoplayModeChanged"), "autoplayMode"));
    }

    [Fact]
    public async Task Nothing_on_the_tv_is_an_empty_now_playing()
    {
        await Connect();
        Assert.Empty(Last("nowPlaying").Fields);
        Assert.DoesNotContain(_sent, c => c.Name == "onStateChange");
    }

    [Fact]
    public async Task A_cast_opens_the_video_where_the_phone_was_and_shows_it_loading()
    {
        await Connect();
        _sent.Clear();
        await _session.HandleAsync(Msg("setPlaylist", new
        {
            videoId = Bunny, currentTime = "12", currentIndex = "0", listId = "RQabc", videoIds = $"{Bunny},{Zoo}",
        }));
        Assert.Equal([$"open {Bunny} 12"], _player.Calls);
        var now = Last("nowPlaying");
        Assert.Equal(Bunny, Field(now, "videoId"));
        Assert.Equal("3", Field(now, "state"));
        Assert.Equal("RQabc", Field(now, "listId"));
        Assert.Equal("0", Field(now, "currentIndex"));
        Assert.Equal("true", Field(Last("onHasPreviousNextChanged"), "hasNext"));
        Assert.Equal("false", Field(Last("onHasPreviousNextChanged"), "hasPrevious"));
    }

    [Fact]
    public async Task The_video_the_page_still_shows_does_not_replace_the_one_being_opened()
    {
        _player.Playback = new CastPlayback(Zoo, CastState.Playing, 5, 19);
        await Connect();
        await _session.HandleAsync(Msg("setPlaylist", new { videoId = Bunny, currentTime = "0", videoIds = Bunny }));
        _sent.Clear();
        _player.Raise(); // the old video still reports for a moment
        Assert.Empty(_sent);
        _player.Playback = new CastPlayback(Bunny, CastState.Playing, 0.5, 600);
        _player.Raise();
        Assert.Equal("1", Field(Last("onStateChange"), "state"));
        Assert.Equal(Bunny, Field(Last("nowPlaying"), "videoId")); // loading → playing: the phones hear it again
    }

    [Fact]
    public async Task The_same_video_cast_again_is_not_reopened()
    {
        _player.Playback = new CastPlayback(Bunny, CastState.Playing, 100, 600);
        await Connect();
        await _session.HandleAsync(Msg("setPlaylist", new { videoId = Bunny, currentTime = "101", videoIds = Bunny }));
        Assert.Empty(_player.Calls);
        await _session.HandleAsync(Msg("setPlaylist", new { videoId = Bunny, currentTime = "300", videoIds = Bunny }));
        Assert.Equal(["seek 300"], _player.Calls);
    }

    [Fact]
    public async Task Refuses_a_cast_without_a_valid_video()
    {
        await Connect();
        await _session.HandleAsync(Msg("setPlaylist", new { videoId = "javascript:x", currentTime = "0" }));
        Assert.Empty(_player.Calls);
    }

    [Fact]
    public async Task Controls_reach_the_player()
    {
        await Connect();
        await _session.HandleAsync(Msg("pause"));
        await _session.HandleAsync(Msg("play"));
        await _session.HandleAsync(Msg("seekTo", new { newTime = "61.5" }));
        await _session.HandleAsync(Msg("stopVideo"));
        Assert.Equal(["pause", "play", "seek 61.5", "stop"], _player.Calls);
    }

    [Fact]
    public async Task The_volume_is_the_pcs()
    {
        await Connect();
        _sent.Clear();
        await _session.HandleAsync(Msg("setVolume", new { volume = "40", muted = "false" }));
        Assert.Equal(new CastVolume(40, false), _player.Volume);
        Assert.Equal("40", Field(Last("onVolumeChanged"), "volume"));
        await _session.HandleAsync(Msg("setVolume", new { muted = "true" }));
        Assert.Equal(new CastVolume(40, true), _player.Volume);
        await _session.HandleAsync(Msg("getVolume"));
        Assert.Equal("true", Field(Last("onVolumeChanged"), "muted"));
    }

    [Fact]
    public async Task Tells_the_phones_about_a_pause_and_a_seek_but_not_about_the_clock()
    {
        _player.Playback = new CastPlayback(Bunny, CastState.Playing, 10, 600);
        await Connect();
        _sent.Clear();
        _player.Playback = new CastPlayback(Bunny, CastState.Playing, 15, 600); // 5 s on, as a real player would be
        _time.Advance(TimeSpan.FromSeconds(5)); // the periodic check too
        _player.Raise();
        Assert.Empty(_sent); // where the phones expect it

        _player.Playback = new CastPlayback(Bunny, CastState.Playing, 120, 600);
        _player.Raise();
        Assert.Equal(["onStateChange"], _sent.Select(c => c.Name)); // a seek
        _sent.Clear();

        _player.Playback = new CastPlayback(Bunny, CastState.Paused, 120, 600);
        _player.Raise();
        Assert.Equal(["onStateChange"], _sent.Select(c => c.Name));
        Assert.Equal("2", Field(Last("onStateChange"), "state"));
        _sent.Clear();

        _player.Playback = new CastPlayback(Bunny, CastState.Playing, 120, 600);
        _player.Raise();
        Assert.Equal(["nowPlaying", "onHasPreviousNextChanged", "onStateChange"], _sent.Select(c => c.Name));
    }

    [Fact]
    public async Task Says_when_the_video_leaves_the_tv()
    {
        _player.Playback = new CastPlayback(Bunny, CastState.Playing, 10, 600);
        await Connect();
        _sent.Clear();
        _player.Playback = CastPlayback.Nothing; // VLC took the TV
        _player.Raise();
        var now = Assert.Single(_sent);
        Assert.Equal("nowPlaying", now.Name);
        Assert.Empty(now.Fields);
    }

    [Fact]
    public async Task A_browser_gone_without_an_event_is_noticed()
    {
        _player.Playback = new CastPlayback(Bunny, CastState.Loading, 0, 0);
        await Connect();
        _sent.Clear();
        _player.Playback = CastPlayback.Nothing; // the browser's process ended: nobody raises Changed
        _time.Advance(TimeSpan.FromSeconds(5));
        var now = Assert.Single(_sent);
        Assert.Equal("nowPlaying", now.Name);
        Assert.Empty(now.Fields);
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Single(_sent); // only changes
    }

    [Fact]
    public async Task Without_phones_nothing_is_sent()
    {
        _player.Playback = new CastPlayback(Bunny, CastState.Playing, 10, 600);
        _player.Raise();
        await _session.HandleAsync(Msg("loungeStatus", new { devices = "[]" }));
        _player.Playback = new CastPlayback(Bunny, CastState.Paused, 10, 600);
        _player.Raise();
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task The_lounge_says_who_is_connected()
    {
        await _session.HandleAsync(Msg("loungeStatus", new
        {
            devices = """[{"type":"LOUNGE_SCREEN","id":"me"},{"type":"REMOTE_CONTROL","id":"a"},{"type":"REMOTE_CONTROL","id":"b"}]""",
        }));
        Assert.Equal(2, _session.Remotes);
        await _session.HandleAsync(Msg("remoteDisconnected", new { id = "a" }));
        Assert.Equal(1, _session.Remotes);
        _session.Reset();
        Assert.Equal(0, _session.Remotes);
    }

    [Fact]
    public async Task Next_and_previous_walk_the_queue_then_fall_back_to_youtubes_own()
    {
        await Connect();
        await _session.HandleAsync(Msg("setPlaylist", new { videoId = Bunny, currentTime = "0", currentIndex = "0", videoIds = $"{Bunny},{Zoo}" }));
        _player.Playback = new CastPlayback(Bunny, CastState.Playing, 1, 600);
        _player.Calls.Clear();
        await _session.HandleAsync(Msg("next"));
        Assert.Equal([$"open {Zoo} 0"], _player.Calls);
        _player.Playback = new CastPlayback(Zoo, CastState.Playing, 1, 19);
        await _session.HandleAsync(Msg("next")); // the end of the queue
        await _session.HandleAsync(Msg("previous"));
        Assert.Equal([$"open {Zoo} 0", "next", $"open {Bunny} 0"], _player.Calls);
    }

    [Fact]
    public async Task A_queued_video_that_ends_is_followed_by_the_next()
    {
        await Connect();
        await _session.HandleAsync(Msg("setPlaylist", new { videoId = Bunny, currentTime = "0", currentIndex = "0", videoIds = $"{Bunny},{Zoo}" }));
        _player.Playback = new CastPlayback(Bunny, CastState.Paused, 600, 600);
        _player.Calls.Clear();
        _player.End();
        await Task.Delay(50);
        Assert.Equal([$"open {Zoo} 0"], _player.Calls);
    }

    [Fact]
    public async Task A_phone_adding_to_the_queue_updates_next()
    {
        _player.Playback = new CastPlayback(Bunny, CastState.Playing, 1, 600);
        await Connect();
        await _session.HandleAsync(Msg("setPlaylist", new { videoId = Bunny, currentTime = "1", videoIds = Bunny }));
        Assert.Equal("false", Field(Last("onHasPreviousNextChanged"), "hasNext"));
        await _session.HandleAsync(Msg("updatePlaylist", new { listId = "RQabc", videoIds = $"{Bunny},{Zoo}" }));
        Assert.Equal("true", Field(Last("onHasPreviousNextChanged"), "hasNext"));
        Assert.Equal("0", Field(Last("nowPlaying"), "currentIndex"));
    }

    [Fact]
    public async Task Disposing_stops_listening_to_the_player()
    {
        await Connect();
        _session.Dispose();
        _sent.Clear();
        _player.Playback = new CastPlayback(Bunny, CastState.Playing, 1, 600);
        _player.Raise();
        Assert.Empty(_sent);
    }

    private sealed class FakePlayer : ICastPlayer
    {
        public CastPlayback Playback { get; set; } = CastPlayback.Nothing;
        public CastVolume Volume { get; private set; } = new(70, false);
        public List<string> Calls { get; } = new();
        public event Action? Changed;
        public event Action? Ended;

        public void Raise() => Changed?.Invoke();
        public void End() => Ended?.Invoke();

        public Task OpenAsync(string videoId, double startSeconds) => Call($"open {videoId} {startSeconds}");
        public Task PlayAsync() => Call("play");
        public Task PauseAsync() => Call("pause");
        public Task SeekAsync(double seconds) => Call($"seek {seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        public Task StopAsync() => Call("stop");
        public Task NextAsync() => Call("next");
        public Task PreviousAsync() => Call("previous");
        public void SetVolume(int percent, bool muted) => Volume = new CastVolume(percent, muted);

        private Task Call(string what)
        {
            lock (Calls) Calls.Add(what);
            return Task.CompletedTask;
        }
    }
}
