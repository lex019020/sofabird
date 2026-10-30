using Sofabird.Core.Vlc;
using Xunit;

namespace Sofabird.Core.Tests;

public sealed class VlcDownloadTests
{
    [Fact]
    public void The_newest_3_0_release_is_picked_by_version_not_by_text()
    {
        // As VideoLAN's listing shows them: sorted as text, other series and folders around.
        const string listing = """
            <a href="../">../</a>
            <a href="2.2.8/">2.2.8/</a>
            <a href="3.0.24/">3.0.24/</a>
            <a href="3.0.3/">3.0.3/</a>
            <a href="3.0.7.1/">3.0.7.1/</a>
            <a href="3.0.9.2/">3.0.9.2/</a>
            <a href="4.0.0/">4.0.0/</a>
            <a href="last/">last/</a>
            """;
        Assert.Equal("3.0.24", VlcDownload.LatestVersion(listing));
        Assert.Null(VlcDownload.LatestVersion("<a href=\"last/\">last/</a>"));
    }

    [Fact]
    public void Installer_and_checksum_urls()
    {
        Assert.Equal("https://download.videolan.org/pub/videolan/vlc/3.0.24/win64/vlc-3.0.24-win64.exe",
            VlcDownload.InstallerUrl("3.0.24").ToString());
        Assert.Equal("https://download.videolan.org/pub/videolan/vlc/3.0.24/win64/vlc-3.0.24-win64.exe.sha256",
            VlcDownload.ChecksumUrl("3.0.24").ToString());
    }

    [Fact]
    public void Checksum_file_gives_the_hash_of_that_file_only()
    {
        const string hash = "D711E1E1FE52052748C39080C7DCE63F6B7E4C315EFEDF1774A3F1957B782FF3";
        Assert.Equal(hash.ToLowerInvariant(), VlcDownload.ParseChecksum($"{hash}  vlc-3.0.24-win64.exe\n", "vlc-3.0.24-win64.exe"));
        Assert.Equal(hash.ToLowerInvariant(), VlcDownload.ParseChecksum($"{hash} *vlc-3.0.24-win64.exe", "vlc-3.0.24-win64.exe"));
        Assert.Null(VlcDownload.ParseChecksum($"{hash}  vlc-3.0.24-win64.msi", "vlc-3.0.24-win64.exe"));
        Assert.Null(VlcDownload.ParseChecksum("<html>Not found</html>", "vlc-3.0.24-win64.exe"));
    }
}
