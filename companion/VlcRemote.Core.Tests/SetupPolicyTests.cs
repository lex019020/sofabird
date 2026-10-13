using VlcRemote.Core.Setup;
using Xunit;

namespace VlcRemote.Core.Tests;

public class SetupPolicyTests
{
    private static readonly FirewallProgram P = FirewallPlanner.Programs(@"C:\a\VlcRemote.Companion.exe", null)[0];

    private static FirewallStatus Status(bool allowed, bool blocked, bool enabled = true) =>
        new(enabled, [new FirewallProgramStatus(P, allowed,
            blocked ? [new FirewallRule(0, "x", P.Path, FwDirection.In, FwAction.Block, true, FwProfiles.Private, FwProtocol.Tcp)] : [])]);

    [Theory]
    [InlineData(false, 0, true)]
    [InlineData(false, SetupPolicy.CurrentVersion, true)]
    [InlineData(true, SetupPolicy.CurrentVersion - 1, true)]
    [InlineData(true, SetupPolicy.CurrentVersion, false)]
    [InlineData(true, SetupPolicy.CurrentVersion + 1, false)]
    public void Window_shows_until_the_current_version_is_completed(bool completed, int version, bool expected) =>
        Assert.Equal(expected, SetupPolicy.ShouldShowOnStart(completed, version));

    [Fact]
    public void Blocked_is_reported_even_before_setup_is_completed()
    {
        Assert.Equal(StartupNotice.FirewallBlocked, SetupPolicy.Decide(false, true, Status(true, true)));
        Assert.Equal(StartupNotice.FirewallBlocked, SetupPolicy.Decide(true, true, Status(false, true)));
    }

    [Fact]
    public void Missing_is_reported_only_after_setup_was_completed()
    {
        Assert.Equal(StartupNotice.None, SetupPolicy.Decide(false, true, Status(false, false)));
        Assert.Equal(StartupNotice.FirewallMissing, SetupPolicy.Decide(true, true, Status(false, false)));
    }

    [Fact]
    public void Nothing_when_ok_unknown_off_or_not_managed()
    {
        Assert.Equal(StartupNotice.None, SetupPolicy.Decide(true, true, Status(true, false)));
        Assert.Equal(StartupNotice.None, SetupPolicy.Decide(true, true, null));
        Assert.Equal(StartupNotice.None, SetupPolicy.Decide(true, true, Status(false, true, enabled: false)));
        Assert.Equal(StartupNotice.None, SetupPolicy.Decide(true, false, Status(false, true)));
    }

    [Fact]
    public void Listeners_wait_only_when_the_window_is_due_and_the_firewall_needs_action()
    {
        Assert.True(SetupPolicy.DeferListeners(true, Status(false, false)));
        Assert.True(SetupPolicy.DeferListeners(true, Status(true, true)));
        Assert.False(SetupPolicy.DeferListeners(true, Status(true, false)));
        Assert.False(SetupPolicy.DeferListeners(true, null));
        Assert.False(SetupPolicy.DeferListeners(false, Status(false, false)));
    }
}
