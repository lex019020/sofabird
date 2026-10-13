using System.Buffers;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VlcRemote.Core.Protocol;

namespace VlcRemote.Core.Session;

/// <summary>
/// NDJSON over a byte stream (RFCOMM, M3): UTF-8, one message per line, <c>\n</c> separator, at most
/// <see cref="ProtocolConstants.MaxStreamLineBytes"/> bytes per line. Platform-independent so it can be tested
/// without WinRT; the Bluetooth server wraps the socket streams into it.
/// </summary>
/// <remarks>
/// One reader at a time (the session loop); writes may come from any thread and are serialized.
/// Cancelling a read does not lose data: the pending stream read is kept and the next
/// <see cref="ReadLineAsync"/> continues it (some stream adapters ignore cancellation tokens).
/// </remarks>
public sealed class StreamLineTransport : ILineTransport
{
    private const int ReadBufferSize = 8 * 1024;
    private const int KeepLineBufferBytes = 64 * 1024;

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly Func<ValueTask>? _onClose;
    private readonly int _maxLineBytes;
    private readonly ILogger _log;
    private readonly byte[] _buffer = new byte[ReadBufferSize];
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private ArrayBufferWriter<byte> _line = new();
    private int _start;
    private int _end;
    private bool _eof;
    private Task<int>? _pendingRead;
    private int _closed;

    /// <param name="input">Stream to read from.</param>
    /// <param name="output">Stream to write to (may be the same object as <paramref name="input"/>).</param>
    /// <param name="description">For logs, e.g. <c>bt Pixel 8 (AA:BB:CC:DD:EE:FF)</c>.</param>
    /// <param name="onClose">Called once after the streams are disposed (e.g. dispose the socket).</param>
    /// <param name="maxLineBytes">Longest accepted incoming line in bytes, excluding the separator.</param>
    public StreamLineTransport(Stream input, Stream output, string description, Func<ValueTask>? onClose = null,
        int maxLineBytes = ProtocolConstants.MaxStreamLineBytes, ILogger? log = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLineBytes);
        _input = input;
        _output = output;
        Description = description;
        _onClose = onClose;
        _maxLineBytes = maxLineBytes;
        _log = log ?? NullLogger.Instance;
    }

    public string Description { get; }

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>
    /// Next line without the <c>\n</c> (a trailing <c>\r</c> is dropped too). <c>null</c> — end of stream or the
    /// connection broke. A final line without <c>\n</c> before a clean end of stream is still returned.
    /// </summary>
    /// <exception cref="InvalidDataException">The line is longer than the limit; the transport is unusable afterwards.</exception>
    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        while (true)
        {
            if (_start < _end)
            {
                var span = _buffer.AsSpan(_start, _end - _start);
                var nl = span.IndexOf((byte)'\n');
                if (nl >= 0)
                {
                    Append(span[..nl]);
                    _start += nl + 1;
                    return TakeLine();
                }
                Append(span);
                _start = _end = 0;
            }

            if (_eof)
            {
                if (_line.WrittenCount == 0) return null;
                return TakeLine();
            }

            ct.ThrowIfCancellationRequested();
            var read = _pendingRead ??= StartRead();
            int n;
            try
            {
                n = await read.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // keep _pendingRead: the next call continues (or consumes) it
            }
            catch (Exception ex)
            {
                _pendingRead = null;
                if (!IsClosed) _log.LogDebug("Read from {Desc} failed: {Error}", Description, ex.Message);
                _eof = true;
                _line.Clear(); // a broken connection never yields a half line
                return null;
            }
            _pendingRead = null;
            if (n <= 0)
            {
                _eof = true;
                continue;
            }
            _start = 0;
            _end = n;
        }
    }

    private Task<int> StartRead()
    {
        try
        {
            // Cancellation is applied by WaitAsync; the read itself survives so no bytes are lost.
            return _input.ReadAsync(_buffer.AsMemory(), CancellationToken.None).AsTask();
        }
        catch (Exception ex)
        {
            return Task.FromException<int>(ex);
        }
    }

    private void Append(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return;
        if ((long)_line.WrittenCount + bytes.Length > _maxLineBytes)
        {
            _eof = true;
            _line.Clear();
            _start = _end = 0;
            throw new InvalidDataException($"line from {Description} exceeds {_maxLineBytes} bytes");
        }
        _line.Write(bytes);
    }

    private string TakeLine()
    {
        var bytes = _line.WrittenSpan;
        if (!bytes.IsEmpty && bytes[^1] == (byte)'\r') bytes = bytes[..^1];
        var text = Encoding.UTF8.GetString(bytes);
        if (_line.Capacity > KeepLineBufferBytes) _line = new ArrayBufferWriter<byte>();
        else _line.Clear();
        return text;
    }

    /// <summary>Writes <paramref name="line"/> plus <c>\n</c> and flushes. Safe to call concurrently.</summary>
    /// <exception cref="ArgumentException">The line contains <c>\n</c>.</exception>
    /// <exception cref="IOException">The transport is closed.</exception>
    public async Task WriteLineAsync(string line, CancellationToken ct)
    {
        if (line.Contains('\n')) throw new ArgumentException("a line must not contain '\\n'", nameof(line));
        var count = Encoding.UTF8.GetByteCount(line);
        var bytes = new byte[count + 1];
        Encoding.UTF8.GetBytes(line, bytes);
        bytes[count] = (byte)'\n';

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsClosed) throw new IOException($"{Description} is closed");
            await _output.WriteAsync(bytes, ct).ConfigureAwait(false);
            await _output.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Lets an in-flight write finish (up to 2 s), flushes and disposes the streams, then calls <c>onClose</c>.
    /// A byte stream has no close frame, so <paramref name="reason"/> is only logged. Idempotent.
    /// </summary>
    public async Task CloseAsync(string? reason, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _log.LogDebug("Closing {Desc} ({Reason})", Description, reason ?? "-");
        try
        {
            if (await _writeGate.WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false))
            {
                try
                {
                    using var flushCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    flushCts.CancelAfter(TimeSpan.FromSeconds(2));
                    await _output.FlushAsync(flushCts.Token).ConfigureAwait(false);
                }
                finally
                {
                    _writeGate.Release();
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug("Flush of {Desc} on close failed: {Error}", Description, ex.Message);
        }
        await ReleaseAsync().ConfigureAwait(false);
    }

    private async Task ReleaseAsync()
    {
        // Observe a read that is still pending: it fails once the stream is gone.
        _pendingRead?.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try { await _input.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
        if (!ReferenceEquals(_input, _output))
        {
            try { await _output.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
        }
        if (_onClose is not null)
        {
            try { await _onClose().ConfigureAwait(false); }
            catch (Exception ex) { _log.LogDebug("Close callback of {Desc} failed: {Error}", Description, ex.Message); }
        }
    }

    public async ValueTask DisposeAsync() => await CloseAsync(null, CancellationToken.None).ConfigureAwait(false);
}
