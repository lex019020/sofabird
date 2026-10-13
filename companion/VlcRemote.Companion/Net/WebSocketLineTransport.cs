using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using VlcRemote.Core.Session;

namespace VlcRemote.Companion.Net;

/// <summary>NDJSON over WebSocket: one text message = one line.</summary>
public sealed class WebSocketLineTransport : ILineTransport
{
    private readonly WebSocket _ws;
    private readonly byte[] _buffer = new byte[16 * 1024];
    private const int MaxLine = 64 * 1024;

    public WebSocketLineTransport(WebSocket ws, string description)
    {
        _ws = ws;
        Description = description;
    }

    public string Description { get; }

    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        var writer = new ArrayBufferWriter<byte>();
        while (true)
        {
            ValueWebSocketReceiveResult r;
            try
            {
                r = await _ws.ReceiveAsync(_buffer.AsMemory(), ct).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                return null;
            }
            if (r.MessageType == WebSocketMessageType.Close) return null;
            if (r.MessageType == WebSocketMessageType.Binary)
            {
                // The protocol has no binary frames — skip the whole message.
                if (r.EndOfMessage) { writer.Clear(); continue; }
                continue;
            }
            writer.Write(_buffer.AsSpan(0, r.Count));
            if (writer.WrittenCount > MaxLine) throw new InvalidOperationException("message too large");
            if (r.EndOfMessage) break;
        }
        var text = Encoding.UTF8.GetString(writer.WrittenSpan);
        return text.TrimEnd('\n', '\r');
    }

    public Task WriteLineAsync(string line, CancellationToken ct)
    {
        if (_ws.State != WebSocketState.Open) throw new InvalidOperationException("websocket is not open");
        var bytes = Encoding.UTF8.GetBytes(line);
        return _ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    public async Task CloseAsync(string? reason, CancellationToken ct)
    {
        if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            try { await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, reason ?? "bye", cts.Token).ConfigureAwait(false); }
            catch { /* ignore */ }
        }
    }

    public ValueTask DisposeAsync()
    {
        _ws.Dispose();
        return ValueTask.CompletedTask;
    }
}
