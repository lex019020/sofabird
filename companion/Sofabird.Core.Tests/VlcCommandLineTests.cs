using Sofabird.Core.Vlc;
using Xunit;

namespace Sofabird.Core.Tests;

public class VlcCommandLineTests
{
    private const string Exe = @"C:\Program Files\VideoLAN\VLC\vlc.exe";

    [Fact]
    public void OpenedFromExplorer_ReturnsFile()
    {
        // VLC file association: "vlc.exe" --started-from-file "%1"
        Assert.Equal(@"D:\Movies\Фильм (2001).mkv",
            VlcCommandLine.ExtractMedia([Exe, "--started-from-file", @"D:\Movies\Фильм (2001).mkv"]));
    }

    [Fact]
    public void StartMenu_NoMedia()
    {
        Assert.Null(VlcCommandLine.ExtractMedia([Exe]));
        Assert.Null(VlcCommandLine.ExtractMedia([Exe, "--started-from-file"]));
    }

    [Fact]
    public void OptionValuesAreNotMedia()
    {
        Assert.Null(VlcCommandLine.ExtractMedia([Exe, "--extraintf", "http:rc", "--http-port", "8080", "--rc-host", "127.0.0.1:4212", "vlc://quit"]));
        Assert.Equal("file:///D:/x.mkv",
            VlcCommandLine.ExtractMedia([Exe, "--extraintf", "http:rc", "--http-password", "p", "file:///D:/x.mkv", "--fullscreen"]));
    }

    [Fact]
    public void UncAndUrls()
    {
        Assert.Equal(@"\\nas\video\a.mkv", VlcCommandLine.ExtractMedia([Exe, @"\\nas\video\a.mkv"]));
        Assert.Equal("https://example.org/s.m3u8", VlcCommandLine.ExtractMedia([Exe, "https://example.org/s.m3u8"]));
        // Several files — the last one.
        Assert.Equal(@"D:\b.mkv", VlcCommandLine.ExtractMedia([Exe, @"D:\a.mkv", @"D:\b.mkv"]));
    }

    private static readonly VlcLaunchOptions Options = new("0.0.0.0", 8080, "dummy", 4212, Fullscreen: true,
        ExtraArgs: "--no-video-title-show --sub-language=\"rus, eng\"");

    [Fact]
    public void LaunchArgs_InterfacesFullscreenExtrasAndTheFileLast()
    {
        var args = VlcCommandLine.BuildLaunchArgs(Options, @"D:\Movies\a b.mkv", fullscreenFollowsWindow: true).ToList();
        Assert.Equal(
            ["--extraintf", "http:rc", "--http-host", "0.0.0.0", "--http-port", "8080", "--http-password", "dummy",
             "--rc-host", "127.0.0.1:4212", "--rc-quiet", "--qt-fullscreen-screennumber=-1", "--fullscreen",
             "--no-video-title-show", "--sub-language=rus, eng", @"D:\Movies\a b.mkv"],
            args);
    }

    [Fact]
    public void LaunchArgs_WithoutFileAndWindowPlacement()
    {
        var args = VlcCommandLine.BuildLaunchArgs(Options with { Fullscreen = false, ExtraArgs = null }, null, fullscreenFollowsWindow: false).ToList();
        Assert.DoesNotContain("--fullscreen", args);
        Assert.DoesNotContain("--qt-fullscreen-screennumber=-1", args);
        Assert.Equal("--rc-quiet", args[^1]);
    }

    [Fact]
    public void RedactArgs_HidesThePasswordAndQuotesSpaces() =>
        Assert.Equal(@"--http-password *** --rc-quiet ""D:\a b.mkv""",
            VlcCommandLine.RedactArgs(["--http-password", "dummy", "--rc-quiet", @"D:\a b.mkv"]));

    [Fact]
    public void SplitArgs_QuotesGroupWords() =>
        Assert.Equal(["--a", "--b=x y", "c"], VlcCommandLine.SplitArgs("  --a   \"--b=x y\" c ").ToList());
}
