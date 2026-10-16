using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;

namespace Sofabird.Companion.YouTube;

/// <summary>
/// The native messaging host Firefox starts for the TV extension (<c>Sofabird.exe &lt;manifest&gt; tv@sofabird.app</c>):
/// relays the extension's messages — a 4-byte length and UTF-8 JSON on stdin/stdout — to the running companion over
/// <see cref="TvExtension.PipeName"/>, one JSON message per line, and back. Messages that arrive while the companion
/// is not there are dropped (the page reports its state again every few seconds). Exits when Firefox closes stdin.
/// </summary>
internal static class TvExtensionHost
{
    /// <summary>Firefox refuses messages to the extension over 1 MB; ours are a few hundred bytes.</summary>
    private const int MaxToBrowser = 1024 * 1024;
    private const int MaxFromBrowser = 1024 * 1024;

    public static int Run()
    {
        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();
        using var stop = new CancellationTokenSource();
        var gate = new object();
        StreamWriter? toCompanion = null;

        var fromCompanion = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                using var pipe = new NamedPipeClientStream(".", TvExtension.PipeName, PipeDirection.InOut,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try
                {
                    await pipe.ConnectAsync(2000, stop.Token).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    continue; // the companion is not running (yet)
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException)
                {
                    await DelayAsync(1000, stop.Token).ConfigureAwait(false);
                    continue;
                }
                var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
                lock (gate) toCompanion = writer;
                try
                {
                    using var reader = new StreamReader(pipe, Encoding.UTF8);
                    while (await reader.ReadLineAsync(stop.Token).ConfigureAwait(false) is { } line)
                        WriteMessage(stdout, line);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
                {
                    // the companion went away (an update, a restart): connect again
                }
                lock (gate) toCompanion = null;
                await DelayAsync(1000, stop.Token).ConfigureAwait(false);
            }
        });

        while (ReadMessage(stdin) is { } message)
        {
            lock (gate)
            {
                try { toCompanion?.WriteLine(message); }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException) { toCompanion = null; }
            }
        }
        stop.Cancel();
        try { fromCompanion.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        return 0;
    }

    private static async Task DelayAsync(int ms, CancellationToken ct)
    {
        try { await Task.Delay(ms, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    /// <summary>One message from Firefox, or <c>null</c> when it closed stdin.</summary>
    private static string? ReadMessage(Stream stdin)
    {
        var header = new byte[4];
        if (!ReadExactly(stdin, header)) return null;
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > MaxFromBrowser) return null;
        var body = new byte[length];
        return ReadExactly(stdin, body) ? Encoding.UTF8.GetString(body) : null;
    }

    private static bool ReadExactly(Stream stream, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

    private static void WriteMessage(Stream stdout, string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        if (body.Length > MaxToBrowser) return;
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        lock (stdout)
        {
            stdout.Write(header);
            stdout.Write(body);
            stdout.Flush();
        }
    }
}
