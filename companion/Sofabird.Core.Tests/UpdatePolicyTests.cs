using Sofabird.Core.Updates;
using Xunit;

namespace Sofabird.Core.Tests;

public class UpdatePolicyTests
{
    [Theory]
    [InlineData("0.4.0", true)]
    [InlineData("0.4.0-beta.312", true)]
    [InlineData("0.4.0-beta0312", true)]
    [InlineData("0.4.0-local.202610131950", false)]
    public void LocalBuilds_DoNotUpdateThemselves(string version, bool expected) =>
        Assert.Equal(expected, UpdatePolicy.UpdatesItself(version));

    [Theory]
    [InlineData(null, "0.4.0-beta.312", true)]
    [InlineData(null, "0.4.0-beta0312", true)]
    [InlineData(null, "0.4.0", false)]
    [InlineData("beta", "0.4.0", true)]
    [InlineData("stable", "0.4.0-beta.312", false)]
    public void TestBuilds_FollowTheSettingOrTheInstalledBuild(string? channel, string version, bool expected) =>
        Assert.Equal(expected, UpdatePolicy.IncludesTestBuilds(channel, version));

    [Theory]
    [InlineData(null, "0.2.0-beta0037", "beta")]
    [InlineData(null, "0.2.0", "stable")]
    [InlineData("stable", "0.2.0-beta0037", null)]
    [InlineData(null, "0.2.0-local.202610131950", null)]
    public void FirstStart_RemembersTheChannelOfTheInstalledBuild(string? channel, string version, string? expected) =>
        Assert.Equal(expected, UpdatePolicy.ChannelToRemember(channel, version));

    [Fact]
    public void Update_WaitsWhileVlcPlays()
    {
        Assert.False(UpdatePolicy.CanApplyNow(vlcPlaying: true));
        Assert.True(UpdatePolicy.CanApplyNow(vlcPlaying: false));
    }
}
