using Sofabird.Core.Vlc;
using Xunit;

namespace Sofabird.Core.Tests;

public class RcParserTests
{
    [Fact]
    public void ParseAudioDevices_FromFixture()
    {
        var lines = Fixtures.ReadLines("rc-adev.txt");
        var devices = RcParser.ParseAudioDevices(lines);

        Assert.Equal(4, devices.Count);
        Assert.Equal("", devices[0].Id);
        Assert.Equal("Default", devices[0].Text);
        Assert.True(devices[0].Current);
        Assert.Equal("{0.0.0.00000000}.{6818d89e-1693-4344-b875-b940b8d98b67}", devices[1].Id);
        Assert.Equal("Speakers (High Definition Audio Device)", devices[1].Text);
        Assert.False(devices[1].Current);
        Assert.Equal("1 - L27q-35 (AMD High Definition Audio Device)", devices[3].Text);
    }

    [Fact]
    public void StatusChange_LinesFromFixtureAreEvents()
    {
        var lines = Fixtures.ReadLines("rc-adev.txt");
        var events = lines.Where(RcParser.IsStatusChange).ToList();
        Assert.Equal(2, events.Count);

        Assert.True(RcParser.TryParseStatusChange(events[0], out var input));
        Assert.Equal(RcStatusKind.NewInput, input.Kind);
        Assert.StartsWith("file:///D:/shared/", input.Text);

        Assert.True(RcParser.TryParseStatusChange(events[1], out var play));
        Assert.Equal(RcStatusKind.PlayState, play.Kind);
        Assert.Equal(3, play.Number);
    }

    [Theory]
    [InlineData("status change: ( audio volume: 266 )", RcStatusKind.AudioVolume, 266)]
    [InlineData("status change: ( stop state: 0 )", RcStatusKind.StopState, 0)]
    [InlineData("status change: ( pause state: 4 ): Pause", RcStatusKind.PauseState, 4)]
    [InlineData("status change: ( time: 12s )", RcStatusKind.Time, 12)]
    public void StatusChange_Numbers(string line, RcStatusKind kind, long number)
    {
        Assert.True(RcParser.TryParseStatusChange(line, out var ev));
        Assert.Equal(kind, ev.Kind);
        Assert.Equal(number, ev.Number);
    }

    [Fact]
    public void StatusChange_Quit()
    {
        Assert.True(RcParser.TryParseStatusChange("status change: ( quit )", out var ev));
        Assert.Equal(RcStatusKind.Quit, ev.Kind);
    }

    [Fact]
    public void Returned_Line()
    {
        Assert.True(RcParser.TryParseReturned("adev: returned 0 (no error)", out var cmd, out var code, out var msg));
        Assert.Equal("adev", cmd);
        Assert.Equal(0, code);
        Assert.Equal("no error", msg);
        Assert.False(RcParser.TryParseReturned("| foo - bar", out _, out _, out _));
    }

    [Fact]
    public void HelpBlock_IsExtractedWithoutFrame()
    {
        var lines = Fixtures.ReadLines("rc-help.txt");
        var block = RcParser.ExtractBlockLines(lines);
        Assert.Contains(block, l => l.Contains("adev [device]"));
        Assert.DoesNotContain(block, l => l.StartsWith("+----", StringComparison.Ordinal));
        Assert.True(RcParser.TryParseBlockStart("+----[ Remote control commands ]", out var name));
        Assert.Equal("Remote control commands", name);
        Assert.False(RcParser.TryParseBlockStart("+----[ end of help ]", out _));
        Assert.True(RcParser.TryParseBlockEnd("+----[ end of help ]", out var end));
        Assert.Equal("help", end);
    }

    [Fact]
    public void ParseTracks_WithLanguageAndCurrentMarker()
    {
        string[] lines =
        [
            "+----[ Audio Track ]",
            "| -1 - Disable",
            "| 1 - Dub Мосфильм / AC3 5.1 @ 448 kbps - [Russian]",
            "| 2 - MVO Позитив-Мультимедиа / DTS-HD MA 5.1-ES @ 4241 kbps - [Russian] *",
            "| 4 - BD Warner Bros. / DTS-HD MA 5.1-ES @ 4213 kbps - [English]",
            "+----[ end of Audio Track ]",
            "atrack: returned 0 (no error)",
        ];
        var tracks = RcParser.ParseTracks(lines);
        Assert.Equal(4, tracks.Count);
        Assert.Equal(-1, tracks[0].Id);
        Assert.Equal("Disable", tracks[0].Name);
        Assert.Null(tracks[0].Language);
        Assert.Equal(2, tracks.Single(t => t.Current).Id);
        Assert.Equal("Russian", tracks[2].Language);
        Assert.Equal("MVO Позитив-Мультимедиа / DTS-HD MA 5.1-ES @ 4241 kbps", tracks[2].Name);
    }
}
