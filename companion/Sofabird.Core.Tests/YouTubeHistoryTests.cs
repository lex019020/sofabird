using Microsoft.Extensions.Time.Testing;
using Sofabird.Core.Protocol;
using Sofabird.Core.YouTube;
using Xunit;

namespace Sofabird.Core.Tests;

public sealed class YouTubeHistoryTests
{
    private const string FilePath = @"C:\data\youtube-history.json";
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 29, 20, 0, 0, TimeSpan.Zero));
    private readonly FakeHistoryFileSystem _fs = new();

    private YouTubeHistory Create() => new(FilePath, _clock, _fs);

    private TvPageState Page(string id, double time, double duration = 600, bool playing = true, bool ad = false,
        bool ended = false, string? title = "Big Buck Bunny") => new()
    {
        Watching = true,
        VideoId = id,
        Title = title,
        Channel = "Blender",
        Time = time,
        Duration = duration,
        Playing = playing,
        Ad = ad,
        Ended = ended,
        ReceivedAt = _clock.GetUtcNow().UtcDateTime,
    };

    [Fact]
    public void A_video_is_recorded_with_where_it_stopped_and_offered_to_continue()
    {
        using var history = Create();
        history.Record(Page("aqz-KE-bpKQ", 5));
        _clock.Advance(TimeSpan.FromSeconds(120));
        history.Record(Page("aqz-KE-bpKQ", 125, playing: false));

        var item = Assert.Single(history.Items("continue"));
        Assert.Equal("https://www.youtube.com/watch?v=aqz-KE-bpKQ", item.Uri);
        Assert.Equal(("Big Buck Bunny", "Blender", YouTubeHistory.Kind), (item.Title, item.Channel, item.Kind));
        Assert.Equal(125, item.Position);
        Assert.Equal(600, item.Length);
        Assert.Equal("https://i.ytimg.com/vi/aqz-KE-bpKQ/hqdefault.jpg", item.ArtUrl);
        Assert.Equal(125, history.ResumeAt("aqz-KE-bpKQ"));
    }

    [Fact]
    public void Ads_short_watches_and_live_streams_are_not_offered_to_continue()
    {
        using var history = Create();
        history.Record(Page("adVideo0001", 30, ad: true)); // an ad: not recorded at all
        Assert.Empty(history.Items("all"));

        history.Record(Page("shortWatch1", 10));
        history.Record(Page("liveStream1", 900, duration: 0)); // no length: nothing to continue
        Assert.Single(history.Items("all"));
        Assert.Empty(history.Items("continue"));
        Assert.Null(history.ResumeAt("shortWatch1"));

        // Closed after 43 s of an 8-minute video (seen on the TV): worth continuing.
        history.Record(Page("closedEarly", 43));
        Assert.Equal("https://www.youtube.com/watch?v=closedEarly", Assert.Single(history.Items("continue")).Uri);
        Assert.Equal(43, history.ResumeAt("closedEarly"));
    }

    [Fact]
    public void The_end_marks_it_finished_and_watching_again_unfinishes_it()
    {
        using var history = Create();
        history.Record(Page("aqz-KE-bpKQ", 590)); // less than 5% left
        var done = Assert.Single(history.Items("all"));
        Assert.True(done.Finished);
        Assert.Equal(0, done.Position);
        Assert.Null(history.ResumeAt("aqz-KE-bpKQ"));

        history.Record(null); // left the video
        _clock.Advance(TimeSpan.FromHours(1));
        history.Record(Page("aqz-KE-bpKQ", 90));
        Assert.False(Assert.Single(history.Items("continue")).Finished);
        Assert.Equal(2, history.Snapshot()[0].PlayCount);
    }

    [Fact]
    public void Positions_while_playing_are_kept_without_a_new_revision()
    {
        using var history = Create();
        history.Record(Page("aqz-KE-bpKQ", 60));
        var revision = history.Revision;
        _clock.Advance(TimeSpan.FromSeconds(11));
        history.Record(Page("aqz-KE-bpKQ", 71));
        Assert.Equal(revision, history.Revision); // lists need no refresh for that
        Assert.Equal(71, history.Snapshot()[0].Position);

        history.Record(Page("aqz-KE-bpKQ", 71, playing: false)); // paused: lists show where
        Assert.True(history.Revision > revision);
    }

    [Fact]
    public void Survives_a_restart_and_drops_old_entries()
    {
        using (var history = Create())
        {
            history.Record(Page("aqz-KE-bpKQ", 300));
            history.Record(Page("anotherVid1", 100, title: "Тестовое видео"));
        }
        Assert.Contains("Тестовое видео", _fs.Files[FilePath], StringComparison.Ordinal); // readable Cyrillic

        using (var history = Create())
        {
            Assert.Equal(2, history.Items("all").Count);
            Assert.Equal(300, history.ResumeAt("aqz-KE-bpKQ"));
        }

        _clock.Advance(TimeSpan.FromDays(200));
        using (var history = Create()) Assert.Empty(history.Items("all"));
    }

    [Fact]
    public void Mark_watched_remove_and_clear()
    {
        using var history = Create();
        history.Record(Page("aqz-KE-bpKQ", 300));
        Assert.True(history.SetFinished("aqz-KE-bpKQ", true));
        Assert.Empty(history.Items("continue"));
        Assert.False(history.SetFinished("aqz-KE-bpKQ", true));
        Assert.True(history.Remove("aqz-KE-bpKQ"));
        Assert.False(history.Remove("aqz-KE-bpKQ"));
        history.Record(Page("anotherVid1", 100));
        history.Clear();
        Assert.Empty(history.Items("all"));
    }

    [Fact]
    public void History_uris_and_merged_lists()
    {
        Assert.Equal("aqz-KE-bpKQ", YouTubeHistory.VideoIdOf("https://www.youtube.com/watch?v=aqz-KE-bpKQ"));
        Assert.Null(YouTubeHistory.VideoIdOf("file:///d:/Movies/x.mkv"));

        var merged = YouTubeHistory.Merge(
            [new HistoryItem { Uri = "file:///a", LastWatched = 3 }, new HistoryItem { Uri = "file:///b", LastWatched = 1 }],
            [new HistoryItem { Uri = "https://www.youtube.com/watch?v=x", LastWatched = 2 }]);
        Assert.Equal(["file:///a", "https://www.youtube.com/watch?v=x", "file:///b"], merged.Select(i => i.Uri));
    }
}
