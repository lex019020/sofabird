using System.Text.Json;
using Sofabird.Core.Session;
using Sofabird.Core.Vlc;
using Xunit;

namespace Sofabird.Core.Tests;

public class VlcCommandMapTests
{
    private static CommandArgs Args(string json) => new(JsonDocument.Parse(json).RootElement);

    private static string? Map(string name, string json = "{}") =>
        VlcCommandMap.Map(name, Args(json)) is { } c
            ? c.Command + string.Concat(c.Args.Select(a => $"&{a.Name}={a.Value}"))
            : null;

    [Theory]
    [InlineData("pause", "pl_forcepause")]
    [InlineData("togglePause", "pl_pause")]
    [InlineData("stop", "pl_stop")]
    [InlineData("next", "pl_next")]
    [InlineData("prev", "pl_previous")]
    [InlineData("playlistClear", "pl_empty")]
    public void PlainCommands(string name, string vlc) => Assert.Equal(vlc, Map(name));

    [Fact]
    public void Seek_AbsoluteIsClampedAtZero_DeltaIsSigned()
    {
        Assert.Equal("seek&val=90", Map("seek", """{"seconds":90}"""));
        Assert.Equal("seek&val=0", Map("seek", """{"seconds":-5}"""));
        Assert.Equal("seek&val=+30", Map("seek", """{"delta":30}"""));
        Assert.Equal("seek&val=-10", Map("seek", """{"delta":-10}"""));
        Assert.Throws<ProtocolException>(() => Map("seek"));
    }

    [Fact]
    public void Tracks_DelaysRateTitleAspect()
    {
        Assert.Equal("audio_track&val=2", Map("track", """{"kind":"audio","id":2}"""));
        Assert.Equal("subtitle_track&val=-1", Map("track", """{"kind":"subtitle","id":-1}"""));
        Assert.Equal("video_track&val=0", Map("track", """{"kind":"video","id":0}"""));
        Assert.Throws<ProtocolException>(() => Map("track", """{"kind":"chapter","id":1}"""));
        Assert.Equal("audiodelay&val=-0.05", Map("delay", """{"kind":"audio","seconds":-0.05}"""));
        Assert.Equal("subdelay&val=1.5", Map("delay", """{"kind":"subtitle","seconds":1.5}"""));
        Assert.Equal("rate&val=1.25", Map("rate", """{"value":1.25}"""));
        Assert.Equal("title&val=3", Map("title", """{"index":3}"""));
        Assert.Equal("aspectratio&val=16:9", Map("aspect", """{"value":"16:9"}"""));
    }

    [Fact]
    public void ChapterByIndexIsStateless_ByDeltaIsNot()
    {
        Assert.Equal("chapter&val=7", Map("chapter", """{"index":7}"""));
        Assert.Null(Map("chapter", """{"delta":1}"""));
    }

    [Theory]
    [InlineData("play")]
    [InlineData("volume")]
    [InlineData("mute")]
    [InlineData("fullscreen")]
    [InlineData("open")]
    [InlineData("playlistRemove")]
    [InlineData("random")]
    [InlineData("whatever")]
    public void StatefulOrUnknownCommands_AreNotMapped(string name) => Assert.Null(Map(name));
}
