using Sofabird.Core.Library;
using Xunit;

namespace Sofabird.Core.Tests;

public class LibraryRescanPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 3, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(LibraryScanReason.FoldersChanged)]
    [InlineData(LibraryScanReason.Periodic)]
    [InlineData(LibraryScanReason.PhoneAsked)]
    public void Local_folders_and_never_scanned_ones_are_always_read(LibraryScanReason reason)
    {
        Assert.True(LibraryRescanPolicy.ShouldScan(network: false, reason, Now.AddMinutes(-1), Now));
        Assert.True(LibraryRescanPolicy.ShouldScan(network: true, reason, lastScan: null, Now));
    }

    [Fact]
    public void A_network_folder_is_not_read_by_the_timer_so_its_computer_can_sleep()
    {
        Assert.False(LibraryRescanPolicy.ShouldScan(network: true, LibraryScanReason.Periodic, Now.AddHours(-10), Now));
        Assert.True(LibraryRescanPolicy.ShouldScan(network: true, LibraryScanReason.FoldersChanged, Now.AddMinutes(-1), Now));
        // A phone asks for the library: read again only when the last scan is old.
        Assert.False(LibraryRescanPolicy.ShouldScan(network: true, LibraryScanReason.PhoneAsked, Now.AddMinutes(-10), Now));
        Assert.True(LibraryRescanPolicy.ShouldScan(network: true, LibraryScanReason.PhoneAsked, Now.AddMinutes(-30), Now));
    }

    [Theory]
    [InlineData(@"\\192.168.0.43\shared\", true)]
    [InlineData(@"\\nas\movies\Films", true)]
    [InlineData(@"relative\path", false)]
    [InlineData("", false)]
    public void Network_paths(string path, bool network) => Assert.Equal(network, LibraryRescanPolicy.IsNetwork(path));
}
