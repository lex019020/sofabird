using Sofabird.Core.YouTube;
using Xunit;

namespace Sofabird.Core.Tests;

public class TvPageStateTests
{
    private static readonly DateTime At = new(2026, 10, 16, 15, 0, 0, DateTimeKind.Utc);

    private const string Playing = """
        {"t":"state","watching":true,"videoId":"aqz-KE-bpKQ","title":"Big Buck Bunny","channel":"Blender","time":60.5,
         "duration":634.6,"playing":true,"ended":false,"ad":false,"adSkippable":false,"canNext":true,"canPrevious":false,
         "fullscreen":true}
        """;

    [Fact]
    public void Parse_reads_the_page_state()
    {
        var s = TvPageState.Parse(Playing, At)!;
        Assert.True(s.Watching);
        Assert.Equal("aqz-KE-bpKQ", s.VideoId);
        Assert.Equal("Blender", s.Channel);
        Assert.Equal(60.5, s.Time);
        Assert.Equal(634.6, s.Duration);
        Assert.True(s.Playing && s.CanNext && s.Fullscreen);
        Assert.False(s.Ad || s.AdSkippable || s.CanPrevious || s.Ended);
        Assert.Equal(At, s.ReceivedAt);
    }

    [Theory]
    [InlineData("""{"t":"hello","version":"1.0.0"}""")]
    [InlineData("""[1,2]""")]
    [InlineData("""not json""")]
    public void Parse_ignores_other_messages(string json) => Assert.Null(TvPageState.Parse(json, At));

    [Fact]
    public void Parse_tolerates_missing_and_odd_fields()
    {
        var s = TvPageState.Parse("""{"t":"state","watching":false,"time":"soon","title":5}""", At)!;
        Assert.False(s.Watching);
        Assert.Equal(0, s.Time);
        Assert.Null(s.Title);
    }

    [Fact]
    public void Position_moves_on_while_playing_and_stops_at_the_end()
    {
        var s = TvPageState.Parse(Playing, At)!;
        Assert.Equal(70.5, s.TimeAt(At.AddSeconds(10)), 3);
        Assert.Equal(634.6, s.TimeAt(At.AddHours(1)), 3);
        Assert.Equal(60.5, (s with { Playing = false }).TimeAt(At.AddSeconds(10)), 3);
    }

    [Fact]
    public void Only_changes_and_seeks_are_worth_a_new_state()
    {
        var first = TvPageState.Parse(Playing, At)!;
        Assert.True(first.DiffersFrom(null));
        // A second later, a second further: nothing new for the phones.
        var next = first with { Time = 61.5, ReceivedAt = At.AddSeconds(1) };
        Assert.False(next.DiffersFrom(first));
        // A seek, a pause, an ad.
        Assert.True((next with { Time = 200 }).DiffersFrom(first));
        Assert.True((next with { Playing = false }).DiffersFrom(first));
        Assert.True((next with { Ad = true }).DiffersFrom(first));
    }
}
