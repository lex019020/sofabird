using VlcRemote.Core.Vlc;
using Xunit;

namespace VlcRemote.Core.Tests;

public class VlcRunStatePolicyTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void RunningVlc_ToleratesOneOffFailures(int failures) =>
        Assert.Null(VlcRunStatePolicy.AfterHttpFailure(VlcRunState.Running, failures, startedRecently: false));

    [Fact]
    public void RunningVlc_IsLostAfterThreeFailures() =>
        Assert.Equal(VlcRunState.RunningNoApi, VlcRunStatePolicy.AfterHttpFailure(VlcRunState.Running, 3, startedRecently: false));

    [Theory]
    [InlineData(VlcRunState.NotRunning)]
    [InlineData(VlcRunState.Starting)]
    [InlineData(VlcRunState.RunningNoApi)]
    public void NewProcess_IsStartingWithinTheGrace_ThenWithoutControl(VlcRunState current)
    {
        Assert.Equal(VlcRunState.Starting, VlcRunStatePolicy.AfterHttpFailure(current, 1, startedRecently: true));
        Assert.Equal(VlcRunState.RunningNoApi, VlcRunStatePolicy.AfterHttpFailure(current, 1, startedRecently: false));
    }

    [Theory]
    [InlineData(VlcRunState.Running, true, 500)]
    [InlineData(VlcRunState.Starting, false, 500)]
    [InlineData(VlcRunState.Running, false, 2000)]
    [InlineData(VlcRunState.NotRunning, false, 2000)]
    [InlineData(VlcRunState.RunningNoApi, true, 2000)]
    public void PollDelay(VlcRunState state, bool playing, int expectedMs) =>
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), VlcRunStatePolicy.PollDelay(state, playing));
}
