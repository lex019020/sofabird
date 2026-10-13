using VlcRemote.Core.Vlc;
using Xunit;

namespace VlcRemote.Core.Tests;

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
}
