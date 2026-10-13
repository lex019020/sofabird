using Microsoft.Extensions.Logging;

namespace VlcRemote.Companion;

/// <summary>
/// Fire-and-forget work that lives as long as the companion: <see cref="Stop"/> (the first step of the shutdown)
/// cancels it, so nothing starts VLC or moves a window while the rest is being disposed; failures are logged.
/// </summary>
public sealed class BackgroundWork(ILogger<BackgroundWork> log) : IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Cancelled when the companion shuts down.</summary>
    public CancellationToken Stopping => _cts.Token;

    /// <param name="what">For the log: "Auto-launch of VLC", ….</param>
    public void Run(string what, Func<CancellationToken, Task> work)
    {
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            try { await work(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex) { log.LogWarning(ex, "{Work} failed", what); }
        }, CancellationToken.None);
    }

    public void Stop() => _cts.Cancel();

    public void Dispose() => _cts.Dispose();
}
