using Sofabird.Companion.YouTube.Cast;
using Sofabird.Core.Protocol;
using Sofabird.Core.YouTube.Cast;
using Xunit;

namespace Sofabird.Companion.Tests;

public class YouTubeCastPlayerTests
{
    [Fact]
    public void A_closed_browser_shows_nothing() =>
        Assert.Equal(CastPlayback.Nothing, YouTubeCastPlayer.From(new YouTubeInfo { Open = false, VideoId = "aqz-KE-bpKQ" }));

    [Fact]
    public void An_ad_or_a_page_still_opening_is_loading()
    {
        Assert.Equal(CastState.Loading, YouTubeCastPlayer.From(new YouTubeInfo { Open = true, VideoId = "aqz-KE-bpKQ", Ad = true, Time = 3, Length = 15 }).State);
        Assert.Equal(CastState.Loading, YouTubeCastPlayer.From(new YouTubeInfo { Open = true, VideoId = "aqz-KE-bpKQ" }).State);
    }

    [Fact]
    public void A_video_plays_or_is_paused_where_the_page_says()
    {
        var playing = YouTubeCastPlayer.From(new YouTubeInfo
        {
            Open = true, VideoId = "aqz-KE-bpKQ", Playing = true, Time = 42.5, Length = 596, CanNext = true,
        });
        Assert.Equal(new CastPlayback("aqz-KE-bpKQ", CastState.Playing, 42.5, 596, CanNext: true), playing);
        Assert.Equal(CastState.Paused, YouTubeCastPlayer.From(new YouTubeInfo { Open = true, VideoId = "aqz-KE-bpKQ", Time = 1 }).State);
    }
}
