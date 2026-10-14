using Microsoft.Extensions.Time.Testing;
using Sofabird.Core.Protocol;
using Sofabird.Core.State;
using Xunit;

namespace Sofabird.Core.Tests;

public class StateCoalescerTests
{
    private static StateMessage S(long time) => new() { Playback = new PlaybackInfo { Time = time } };

    [Fact]
    public void FirstSendsImmediately_BurstIsCoalescedToLatest()
    {
        var time = new FakeTimeProvider();
        var sent = new List<long>();
        using var c = new StateCoalescer(s => { sent.Add(s.Playback.Time); return Task.CompletedTask; }, time, TimeSpan.FromMilliseconds(250));

        c.Publish(S(1));
        Assert.Equal([1], sent);

        time.Advance(TimeSpan.FromMilliseconds(50));
        c.Publish(S(2));
        c.Publish(S(3));
        c.Publish(S(4));
        Assert.Equal([1], sent); // waiting for the timer

        time.Advance(TimeSpan.FromMilliseconds(199));
        Assert.Equal([1], sent);
        time.Advance(TimeSpan.FromMilliseconds(2));
        Assert.Equal([1, 4], sent); // only the last one

        time.Advance(TimeSpan.FromMilliseconds(300));
        c.Publish(S(5));
        Assert.Equal([1, 4, 5], sent); // the interval has passed — at once
    }

    [Fact]
    public void RateNeverExceedsFourPerSecond()
    {
        var time = new FakeTimeProvider();
        var count = 0;
        using var c = new StateCoalescer(_ => { count++; return Task.CompletedTask; }, time, TimeSpan.FromMilliseconds(250));
        for (var i = 0; i < 100; i++)
        {
            c.Publish(S(i));
            time.Advance(TimeSpan.FromMilliseconds(10)); // 100 publications in 1 s
        }
        time.Advance(TimeSpan.FromMilliseconds(250));
        Assert.InRange(count, 4, 5);
    }
}
