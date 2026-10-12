using System.Threading.Channels;
using VlcRemote.Core.Session;

namespace VlcRemote.Core.Tests;

/// <summary>Пара каналов: тест пишет «от телефона», читает «от ПК».</summary>
internal sealed class InMemoryLineTransport : ILineTransport
{
    private readonly Channel<string> _toPc = Channel.CreateUnbounded<string>();
    private readonly Channel<string> _toPhone = Channel.CreateUnbounded<string>();

    public string Description => "mem";
    public bool Closed { get; private set; }
    public string? CloseReason { get; private set; }

    // Сторона телефона
    public ValueTask PhoneSendAsync(string line) => _toPc.Writer.WriteAsync(line);
    public void PhoneDisconnect() => _toPc.Writer.TryComplete();

    public async Task<string> PhoneReceiveAsync(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
        return await _toPhone.Reader.ReadAsync(cts.Token);
    }

    public bool PhoneHasPending => _toPhone.Reader.TryPeek(out _);

    // Сторона ПК (Session)
    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        try
        {
            return await _toPc.Reader.ReadAsync(ct);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public Task WriteLineAsync(string line, CancellationToken ct)
    {
        if (Closed) throw new IOException("closed");
        return _toPhone.Writer.WriteAsync(line, ct).AsTask();
    }

    public Task CloseAsync(string? reason, CancellationToken ct)
    {
        Closed = true;
        CloseReason = reason;
        _toPhone.Writer.TryComplete();
        _toPc.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
