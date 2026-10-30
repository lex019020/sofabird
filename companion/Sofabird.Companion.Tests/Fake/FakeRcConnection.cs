using System.Buffers;
using System.IO.Pipelines;
using System.Text;

namespace Sofabird.Companion.Tests.Fake;

/// <summary>
/// One connection to the fake RC interface: two in-memory pipes. The companion gets <see cref="ClientStream"/>; the
/// fake reads its commands (<see cref="ReadCommandAsync"/>) and writes response and <c>status change:</c> lines.
/// </summary>
internal sealed class FakeRcConnection : IDisposable
{
    private readonly Pipe _toVlc = new();
    private readonly Pipe _toClient = new();
    private readonly object _writeGate = new();
    private readonly StreamReader _commands;
    private readonly CancellationTokenSource _closing = new();
    private bool _closed;

    public FakeRcConnection()
    {
        ClientStream = new DuplexStream(_toClient.Reader.AsStream(), _toVlc.Writer.AsStream());
        _commands = new StreamReader(_toVlc.Reader.AsStream(), new UTF8Encoding(false), false, 1024, leaveOpen: false);
    }

    public Stream ClientStream { get; }

    /// <summary>The next command line of the companion; <c>null</c> once either side closed the connection.</summary>
    public async Task<string?> ReadCommandAsync()
    {
        try
        {
            return await _commands.ReadLineAsync(_closing.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Lines as VLC prints them (<c>\r\n</c>); dropped once the connection is closed.</summary>
    public void Write(params string[] lines)
    {
        lock (_writeGate)
        {
            if (_closed) return;
            try
            {
                foreach (var line in lines)
                    _toClient.Writer.Write(Encoding.UTF8.GetBytes(line + "\r\n"));
                // Far below the pipe's pause threshold, so the flush completes synchronously.
                var flush = _toClient.Writer.FlushAsync().AsTask().GetAwaiter().GetResult();
                if (flush.IsCompleted) _closed = true; // the companion stopped reading
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                _closed = true;
            }
        }
    }

    /// <summary>VLC is gone: the companion reads the end of the stream.</summary>
    public void Close()
    {
        lock (_writeGate)
        {
            if (!_closed)
            {
                _closed = true;
                _toClient.Writer.Complete();
            }
        }
        _closing.Cancel();
    }

    /// <summary>After the fake stopped serving the connection.</summary>
    public void Dispose()
    {
        Close();
        _commands.Dispose();
    }

    private sealed class DuplexStream(Stream read, Stream write) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => write.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => write.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => read.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => read.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => write.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => write.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                read.Dispose();
                write.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
