namespace Sofabird.Core.Session;

/// <summary>
/// A "line in / line out" transport (NDJSON). Implementations: WebSocket (M2), RFCOMM (M3),
/// in-memory for tests. One line = one JSON message without line breaks inside.
/// </summary>
public interface ILineTransport : IAsyncDisposable
{
    /// <summary>Description for logs, for example <c>ws 192.168.1.5:51234</c>.</summary>
    string Description { get; }

    /// <summary>Read the next line; <c>null</c> — the connection is closed.</summary>
    Task<string?> ReadLineAsync(CancellationToken ct);

    /// <summary>Send a line (without the trailing <c>\n</c>; the transport adds it itself if needed).</summary>
    Task WriteLineAsync(string line, CancellationToken ct);

    /// <summary>Close the connection gracefully.</summary>
    Task CloseAsync(string? reason, CancellationToken ct);
}
