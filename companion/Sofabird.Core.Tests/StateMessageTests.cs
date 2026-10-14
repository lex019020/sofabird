using Sofabird.Core.Protocol;
using Xunit;

namespace Sofabird.Core.Tests;

public class StateMessageTests
{
    [Fact]
    public void WithId_LeavesTheSharedStateWithoutAnId()
    {
        var shared = new StateMessage { Playback = new PlaybackInfo { State = "playing", UpdatedAt = 42 } };
        var reply = shared.WithId("7");
        Assert.Equal("7", reply.Id);
        Assert.Null(shared.Id);
        Assert.Same(shared.Playback, reply.Playback);
    }

    [Fact]
    public void WithoutTimestamp_DoesNotTouchTheOriginal()
    {
        var state = new StateMessage { Playback = new PlaybackInfo { State = "playing", Time = 10, UpdatedAt = 42 } };
        var compared = state.WithoutTimestamp();
        Assert.Equal(0, compared.Playback.UpdatedAt);
        Assert.Equal(10, compared.Playback.Time);
        Assert.Equal(42, state.Playback.UpdatedAt);
    }
}
