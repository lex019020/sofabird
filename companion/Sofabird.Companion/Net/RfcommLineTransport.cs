using Microsoft.Extensions.Logging;
using Sofabird.Core.Session;
using Windows.Networking.Sockets;

namespace Sofabird.Companion.Net;

/// <summary>
/// NDJSON over an RFCOMM <see cref="StreamSocket"/>: the WinRT socket streams wrapped into the platform-independent
/// <see cref="StreamLineTransport"/> (framing, 1 MiB line limit, serialized writes — tested in Core).
/// </summary>
public static class RfcommLineTransport
{
    public static StreamLineTransport Create(StreamSocket socket, string description, ILogger? log = null)
    {
        // bufferSize 0: no adapter-level buffering; StreamLineTransport buffers reads itself and flushes every line.
        var input = socket.InputStream.AsStreamForRead(0);
        var output = socket.OutputStream.AsStreamForWrite(0);
        return new StreamLineTransport(input, output, description, () =>
        {
            socket.Dispose();
            return ValueTask.CompletedTask;
        }, log: log);
    }
}
