using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sofabird.Core.YouTube;

namespace Sofabird.Companion.YouTube;

/// <summary>
/// The companion's end of the TV extension's link: a named pipe of the current user that <see cref="TvExtensionHost"/>
/// connects to while the TV browser runs. Keeps the page's last state and sends the companion's commands to it.
/// </summary>
public sealed class TvExtensionBridge : IDisposable
{
    private readonly ILogger _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private TvPageState? _state;

    public TvExtensionBridge(ILogger<TvExtensionBridge> log) => _log = log;

    /// <summary>The page state changed in a way phones should see (not every position report).</summary>
    public event Action? Changed;

    /// <summary>The extension is connected (the TV browser runs with it).</summary>
    public bool Connected
    {
        get { lock (_gate) return _writer is not null; }
    }

    /// <summary>The page's last report, or <c>null</c> while the extension is not connected.</summary>
    public TvPageState? State
    {
        get { lock (_gate) return _state; }
    }

    public void Start() => _ = Task.Run(AcceptLoopAsync);

    /// <summary>
    /// Sends a command to the page (<c>play</c>, <c>pause</c>, <c>toggle</c>, <c>seek</c>, <c>next</c>,
    /// <c>previous</c>, <c>fullscreen</c>, <c>skipAd</c>) or to the extension (<c>open</c> with a watch URL).
    /// <c>false</c> — not connected.
    /// </summary>
    public bool Send(string action, double? time = null, string? url = null)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("t", "cmd");
            w.WriteString("action", action);
            if (time is double t) w.WriteNumber("time", Math.Round(t, 2));
            if (url is not null) w.WriteString("url", url);
            w.WriteEndObject();
        }
        lock (_gate)
        {
            if (_writer is null) return false;
            try
            {
                _writer.WriteLine(Encoding.UTF8.GetString(buffer.ToArray()));
                return true;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                _log.LogDebug(ex, "TV extension: sending {Action} failed", action);
                return false;
            }
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = new NamedPipeServerStream(TvExtension.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch (IOException ex)
            {
                _log.LogWarning(ex, "TV extension: the pipe could not be created");
                await DelayAsync(5000).ConfigureAwait(false);
                continue;
            }
            using (server)
            {
                try
                {
                    await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException ex)
                {
                    _log.LogDebug(ex, "TV extension: waiting for the host failed");
                    continue;
                }
                await ServeAsync(server).ConfigureAwait(false);
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream server)
    {
        _log.LogInformation("TV extension connected");
        lock (_gate) _writer = new StreamWriter(server, new UTF8Encoding(false)) { AutoFlush = true };
        try
        {
            using var reader = new StreamReader(server, Encoding.UTF8);
            while (await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false) is { } line)
                OnMessage(line);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // the browser closed
        }
        lock (_gate)
        {
            _writer = null;
            _state = null;
        }
        _log.LogInformation("TV extension disconnected");
        RaiseChanged();
    }

    private void OnMessage(string line)
    {
        var state = TvPageState.Parse(line, DateTime.UtcNow);
        if (state is null)
        {
            if (line.Contains("\"hello\"", StringComparison.Ordinal)) _log.LogInformation("TV extension: {Hello}", line);
            return;
        }
        bool changed;
        lock (_gate)
        {
            changed = state.DiffersFrom(_state);
            _state = state;
        }
        if (changed) RaiseChanged();
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { _log.LogWarning(ex, "TV extension: Changed handler failed"); }
    }

    private async Task DelayAsync(int ms)
    {
        try { await Task.Delay(ms, _stop.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
