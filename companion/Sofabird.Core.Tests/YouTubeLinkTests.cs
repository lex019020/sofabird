using Sofabird.Core.YouTube;
using Xunit;

namespace Sofabird.Core.Tests;

public class YouTubeLinkTests
{
    [Theory]
    [InlineData("https://youtu.be/aqz-KE-bpKQ?si=Xy12_abc", "aqz-KE-bpKQ")]
    [InlineData("Big Buck Bunny https://youtu.be/aqz-KE-bpKQ", "aqz-KE-bpKQ")] // what the YouTube app shares
    [InlineData("https://www.youtube.com/watch?v=aqz-KE-bpKQ&feature=share", "aqz-KE-bpKQ")]
    [InlineData("https://m.youtube.com/watch?feature=youtu.be&v=aqz-KE-bpKQ", "aqz-KE-bpKQ")]
    [InlineData("youtube.com/watch?v=aqz-KE-bpKQ", "aqz-KE-bpKQ")]
    [InlineData("https://www.youtube.com/shorts/aqz-KE-bpKQ", "aqz-KE-bpKQ")]
    [InlineData("https://www.youtube.com/live/aqz-KE-bpKQ?si=1", "aqz-KE-bpKQ")]
    [InlineData("https://music.youtube.com/watch?v=aqz-KE-bpKQ", "aqz-KE-bpKQ")]
    public void Finds_the_video_id(string text, string id) => Assert.Equal(id, YouTubeLink.Parse(text)?.VideoId);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no link here")]
    [InlineData("https://www.youtube.com/@Blender")] // a channel, not a video
    [InlineData("https://www.youtube.com/watch?v=short")] // not an 11-character id
    [InlineData("https://example.com/watch?v=aqz-KE-bpKQ")]
    [InlineData("https://youtube.com.evil.example/watch?v=aqz-KE-bpKQ")]
    public void Rejects_what_is_not_a_youtube_video(string? text) => Assert.Null(YouTubeLink.Parse(text));

    [Fact]
    public void Keeps_the_playlist_and_start_time_and_rebuilds_a_clean_url()
    {
        var video = YouTubeLink.Parse("https://www.youtube.com/watch?v=aqz-KE-bpKQ&list=PL0123456789abcdef&t=1m30s&si=tracking")!;
        Assert.Equal("PL0123456789abcdef", video.PlaylistId);
        Assert.Equal(90, video.StartSeconds);
        Assert.Equal("https://www.youtube.com/watch?v=aqz-KE-bpKQ&list=PL0123456789abcdef&t=90s", video.WatchUrl);
        Assert.Equal("https://www.youtube.com/watch?v=aqz-KE-bpKQ", YouTubeLink.Parse("https://youtu.be/aqz-KE-bpKQ?si=x")!.WatchUrl);
    }

    [Theory]
    [InlineData("90", 90)]
    [InlineData("90s", 90)]
    [InlineData("1m30s", 90)]
    [InlineData("1h2m3s", 3723)]
    [InlineData("2m", 120)]
    public void Parses_start_times(string value, int seconds) => Assert.Equal(seconds, YouTubeLink.ParseTime(value));

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("1x")]
    public void Ignores_odd_start_times(string value) => Assert.Null(YouTubeLink.ParseTime(value));
}
