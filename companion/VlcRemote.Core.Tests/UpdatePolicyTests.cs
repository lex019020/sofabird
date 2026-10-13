using VlcRemote.Core.Updates;
using Xunit;

namespace VlcRemote.Core.Tests;

public class UpdatePolicyTests
{
    [Theory]
    [InlineData("0.4.0", true)]
    [InlineData("0.4.0-beta.312", true)]
    [InlineData("0.4.0-local.202610131950", false)]
    public void LocalBuilds_DoNotUpdateThemselves(string version, bool expected) =>
        Assert.Equal(expected, UpdatePolicy.UpdatesItself(version));

    [Theory]
    [InlineData(null, "0.4.0-beta.312", true)]
    [InlineData(null, "0.4.0", false)]
    [InlineData("beta", "0.4.0", true)]
    [InlineData("stable", "0.4.0-beta.312", false)]
    public void TestBuilds_FollowTheSettingOrTheInstalledBuild(string? channel, string version, bool expected) =>
        Assert.Equal(expected, UpdatePolicy.IncludesTestBuilds(channel, version));

    [Fact]
    public void Update_WaitsWhileVlcPlays()
    {
        Assert.False(UpdatePolicy.CanApplyNow(vlcPlaying: true));
        Assert.True(UpdatePolicy.CanApplyNow(vlcPlaying: false));
    }
}
