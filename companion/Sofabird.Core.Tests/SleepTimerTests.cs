using Microsoft.Extensions.Time.Testing;
using Sofabird.Core.Power;
using Xunit;

namespace Sofabird.Core.Tests;

public sealed class SleepTimerTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-29T21:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void Runs_out_after_the_minutes_once_and_is_off_then()
    {
        using var timer = new SleepTimer(_time);
        var fired = new List<SleepTimerAction>();
        timer.Fired += fired.Add;

        timer.SetMinutes(30, SleepTimerAction.Sleep);
        var info = timer.Info!;
        Assert.Equal(_time.GetUtcNow().AddMinutes(30).ToUnixTimeMilliseconds(), info.EndsAt);
        Assert.Null(info.EndOfItem);
        Assert.Equal("sleep", info.Action);

        _time.Advance(TimeSpan.FromMinutes(29));
        Assert.Empty(fired);
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal([SleepTimerAction.Sleep], fired);
        Assert.Null(timer.Info);

        _time.Advance(TimeSpan.FromHours(1));
        Assert.Single(fired);
    }

    [Fact]
    public void A_new_time_replaces_the_old_one_and_cancel_stops_it()
    {
        using var timer = new SleepTimer(_time);
        var fired = 0;
        var changed = 0;
        timer.Fired += _ => fired++;
        timer.Changed += () => changed++;

        timer.SetMinutes(10, SleepTimerAction.Pause);
        timer.SetMinutes(60, SleepTimerAction.Pause);
        _time.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(0, fired);

        timer.Cancel();
        _time.Advance(TimeSpan.FromHours(2));
        Assert.Equal(0, fired);
        Assert.Null(timer.Info);
        Assert.Equal(3, changed);

        timer.Cancel(); // already off: nothing to tell
        Assert.Equal(3, changed);
    }

    [Fact]
    public void End_of_item_runs_out_when_what_plays_ends()
    {
        using var timer = new SleepTimer(_time);
        var fired = new List<SleepTimerAction>();
        timer.Fired += fired.Add;

        timer.OnItemEnded(); // nothing set
        Assert.Empty(fired);

        timer.SetEndOfItem(SleepTimerAction.Pause);
        Assert.True(timer.Info!.EndOfItem);
        Assert.Null(timer.Info!.EndsAt);
        _time.Advance(TimeSpan.FromHours(5));
        Assert.Empty(fired);

        timer.OnItemEnded();
        Assert.Equal([SleepTimerAction.Pause], fired);
        Assert.Null(timer.Info);

        // A timer by minutes does not care about items ending.
        timer.SetMinutes(20, SleepTimerAction.Pause);
        timer.OnItemEnded();
        Assert.Single(fired);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(SleepTimer.MaxMinutes + 1)]
    public void Minutes_out_of_range_are_refused(int minutes)
    {
        using var timer = new SleepTimer(_time);
        Assert.Throws<ArgumentOutOfRangeException>(() => timer.SetMinutes(minutes, SleepTimerAction.Pause));
    }
}
