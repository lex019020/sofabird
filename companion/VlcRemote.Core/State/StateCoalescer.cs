using VlcRemote.Core.Protocol;

namespace VlcRemote.Core.State;

/// <summary>
/// Ограничивает частоту отправки <c>state</c> (не чаще <see cref="MinInterval"/>, по умолчанию 4/с):
/// промежуточные состояния схлопываются, последнее доставляется по таймеру.
/// </summary>
public sealed class StateCoalescer : IDisposable
{
    private readonly Func<StateMessage, Task> _sink;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private readonly ITimer _timer;
    private StateMessage? _pending;
    private long _lastSentTicks = long.MinValue;
    private bool _timerArmed;
    private bool _disposed;

    public StateCoalescer(Func<StateMessage, Task> sink, TimeProvider? timeProvider = null, TimeSpan? minInterval = null)
    {
        _sink = sink;
        _time = timeProvider ?? TimeProvider.System;
        MinInterval = minInterval ?? TimeSpan.FromMilliseconds(250);
        _timer = _time.CreateTimer(_ => Flush(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public TimeSpan MinInterval { get; }

    /// <summary>Поставить состояние в очередь: отправится сразу или по таймеру.</summary>
    public void Publish(StateMessage state)
    {
        lock (_lock)
        {
            if (_disposed) return;
            var now = _time.GetTimestamp();
            var elapsed = _lastSentTicks == long.MinValue ? TimeSpan.MaxValue : _time.GetElapsedTime(_lastSentTicks, now);
            if (!_timerArmed && elapsed >= MinInterval)
            {
                _lastSentTicks = now;
                _ = SafeSend(state);
                return;
            }
            _pending = state;
            if (!_timerArmed)
            {
                _timerArmed = true;
                var wait = MinInterval - elapsed;
                if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
                _timer.Change(wait, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void Flush()
    {
        StateMessage? toSend;
        lock (_lock)
        {
            _timerArmed = false;
            toSend = _pending;
            _pending = null;
            if (toSend is null) return;
            _lastSentTicks = _time.GetTimestamp();
        }
        _ = SafeSend(toSend);
    }

    private async Task SafeSend(StateMessage state)
    {
        try { await _sink(state).ConfigureAwait(false); }
        catch { /* отправитель сам логирует */ }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _pending = null;
        }
        _timer.Dispose();
    }
}
