namespace Sofabird.Core.Power;

/// <summary>What the sleep timer does when it runs out.</summary>
public enum SleepTimerAction
{
    /// <summary>Pause what plays on the TV (VLC, YouTube).</summary>
    Pause,
    /// <summary>Pause, then put the PC to sleep.</summary>
    Sleep,
}

/// <summary><c>state.system.sleepTimer</c>: when it runs out, or at the end of what plays now.</summary>
public sealed class SleepTimerInfo
{
    /// <summary>Unix time in ms when it runs out; <c>null</c> with <see cref="EndOfItem"/>.</summary>
    public long? EndsAt { get; set; }
    /// <summary>It runs out when the movie, episode or video that plays now ends.</summary>
    public bool? EndOfItem { get; set; }
    /// <summary><c>pause</c> or <c>sleep</c>.</summary>
    public string Action { get; set; } = "pause";
}

/// <summary>
/// The sleep timer (owner's request): after N minutes, or at the end of what plays now, pause the TV and maybe put the
/// PC to sleep. It lives in the companion, so it works with the phone asleep; it is not kept over a restart.
/// Thread-safe; <see cref="Fired"/> is raised on a timer thread or the caller of <see cref="OnItemEnded"/>.
/// </summary>
public sealed class SleepTimer : IDisposable
{
    public const int MaxMinutes = 12 * 60;

    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private ITimer? _timer;
    private DateTimeOffset? _endsAt;
    private bool _endOfItem;
    private SleepTimerAction _action;

    public SleepTimer(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>It ran out: do <see cref="SleepTimerAction"/>. Raised once; the timer is off afterwards.</summary>
    public event Action<SleepTimerAction>? Fired;

    /// <summary>Set, cancelled or ran out: the state to show changed.</summary>
    public event Action? Changed;

    /// <summary>For the state; <c>null</c> — off.</summary>
    public SleepTimerInfo? Info
    {
        get
        {
            lock (_lock)
            {
                if (_endsAt is null && !_endOfItem) return null;
                return new SleepTimerInfo
                {
                    EndsAt = _endsAt?.ToUnixTimeMilliseconds(),
                    EndOfItem = _endOfItem ? true : null,
                    Action = _action == SleepTimerAction.Sleep ? "sleep" : "pause",
                };
            }
        }
    }

    /// <summary>Runs out in <paramref name="minutes"/> (1..<see cref="MaxMinutes"/>).</summary>
    public void SetMinutes(int minutes, SleepTimerAction action)
    {
        if (minutes < 1 || minutes > MaxMinutes) throw new ArgumentOutOfRangeException(nameof(minutes));
        lock (_lock)
        {
            StopTimer();
            _endOfItem = false;
            _action = action;
            _endsAt = _time.GetUtcNow().AddMinutes(minutes);
            _timer = _time.CreateTimer(_ => Fire(), null, TimeSpan.FromMinutes(minutes), Timeout.InfiniteTimeSpan);
        }
        Changed?.Invoke();
    }

    /// <summary>Runs out when what plays now ends (<see cref="OnItemEnded"/>).</summary>
    public void SetEndOfItem(SleepTimerAction action)
    {
        lock (_lock)
        {
            StopTimer();
            _endsAt = null;
            _endOfItem = true;
            _action = action;
        }
        Changed?.Invoke();
    }

    public void Cancel()
    {
        bool was;
        lock (_lock)
        {
            was = _endsAt is not null || _endOfItem;
            StopTimer();
            _endsAt = null;
            _endOfItem = false;
        }
        if (was) Changed?.Invoke();
    }

    /// <summary>A movie, episode or video ended on the TV: an "end of this" timer runs out.</summary>
    public void OnItemEnded()
    {
        lock (_lock)
        {
            if (!_endOfItem) return;
        }
        Fire();
    }

    private void Fire()
    {
        SleepTimerAction action;
        lock (_lock)
        {
            if (_endsAt is null && !_endOfItem) return; // cancelled meanwhile
            action = _action;
            StopTimer();
            _endsAt = null;
            _endOfItem = false;
        }
        Changed?.Invoke();
        Fired?.Invoke(action);
    }

    private void StopTimer()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public void Dispose()
    {
        lock (_lock) StopTimer();
    }
}
