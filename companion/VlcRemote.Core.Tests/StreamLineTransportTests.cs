using System.Text;
using System.Threading.Channels;
using VlcRemote.Core.Bluetooth;
using VlcRemote.Core.Protocol;
using VlcRemote.Core.Session;
using Xunit;

namespace VlcRemote.Core.Tests;

public sealed class StreamLineTransportTests
{
    /// <summary>
    /// Test stream: reads return the chunks fed by the test exactly as fed (one chunk, or its head, per read);
    /// writes go to a buffer one byte at a time with yields in between, so unsynchronized writers would interleave.
    /// </summary>
    private sealed class ChunkStream : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
        private readonly MemoryStream _written = new();
        private readonly object _writeLock = new();
        private byte[]? _current;
        private int _offset;
        private Exception? _readError;

        public int Reads { get; private set; }
        public bool Disposed { get; private set; }
        public int Flushes { get; private set; }

        public void Feed(byte[] chunk) => _chunks.Writer.TryWrite(chunk);
        public void Feed(string text) => Feed(Encoding.UTF8.GetBytes(text));
        public void End() => _chunks.Writer.TryComplete();
        public void Break(Exception ex)
        {
            _readError = ex;
            _chunks.Writer.TryComplete(ex);
        }

        public string Written
        {
            get { lock (_writeLock) return Encoding.UTF8.GetString(_written.ToArray()); }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            Reads++;
            if (_current is null || _offset >= _current.Length)
            {
                try
                {
                    if (!await _chunks.Reader.WaitToReadAsync(ct)) return 0;
                }
                catch (Exception ex) when (ex == _readError) { throw new IOException("connection reset", ex); }
                _current = await _chunks.Reader.ReadAsync(ct);
                _offset = 0;
            }
            var n = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, n).CopyTo(buffer);
            _offset += n;
            return n;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (Disposed) throw new ObjectDisposedException(nameof(ChunkStream));
            for (var i = 0; i < buffer.Length; i++)
            {
                lock (_writeLock) _written.WriteByte(buffer.Span[i]);
                if (i % 7 == 0) await Task.Yield();
            }
        }

        public override Task FlushAsync(CancellationToken ct)
        {
            Flushes++;
            return Task.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            _chunks.Writer.TryComplete(new ObjectDisposedException(nameof(ChunkStream)));
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => Flushes++;
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    }

    private static (StreamLineTransport transport, ChunkStream stream) Create(int maxLine = ProtocolConstants.MaxStreamLineBytes, Func<ValueTask>? onClose = null)
    {
        var stream = new ChunkStream();
        return (new StreamLineTransport(stream, stream, "test", onClose, maxLine), stream);
    }

    private static Task<string?> Read(StreamLineTransport t) => t.ReadLineAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

    [Fact]
    public async Task Line_split_over_several_reads_is_joined()
    {
        var (t, s) = Create();
        var read = Read(t);
        s.Feed("{\"t\":");
        await Task.Delay(50);
        Assert.False(read.IsCompleted); // waits for the separator
        s.Feed("\"ping\"");
        s.Feed("}\n");
        Assert.Equal("{\"t\":\"ping\"}", await read);
    }

    [Fact]
    public async Task Several_lines_in_one_chunk_come_out_one_by_one()
    {
        var (t, s) = Create();
        s.Feed("{\"a\":1}\n{\"b\":2}\r\n\n{\"c\":");
        Assert.Equal("{\"a\":1}", await Read(t));
        Assert.Equal("{\"b\":2}", await Read(t)); // CRLF tolerated
        Assert.Equal("", await Read(t));         // empty line is passed through (the session skips it)
        var pending = Read(t);
        s.Feed("3}\n");
        Assert.Equal("{\"c\":3}", await pending);
        Assert.Equal(2, s.Reads); // one read for the whole first chunk, one more for the tail
    }

    [Fact]
    public async Task Utf8_character_split_across_chunks_is_decoded()
    {
        var (t, s) = Create();
        var bytes = Encoding.UTF8.GetBytes("{\"name\":\"Фильм 🎬\"}\n");
        var cyrillic = Array.IndexOf(bytes, (byte)0xD0); // first byte of "Ф"
        var emoji = Array.IndexOf(bytes, (byte)0xF0);    // first byte of the 4-byte emoji
        s.Feed(bytes[..(cyrillic + 1)]);
        s.Feed(bytes[(cyrillic + 1)..(emoji + 2)]);
        s.Feed(bytes[(emoji + 2)..]);
        Assert.Equal("{\"name\":\"Фильм 🎬\"}", await Read(t));
    }

    [Fact]
    public async Task Line_at_the_limit_is_accepted_and_longer_one_is_rejected()
    {
        var (t, s) = Create(maxLine: 16);
        s.Feed(new string('x', 16) + "\n");
        Assert.Equal(new string('x', 16), await Read(t));

        s.Feed(new string('y', 10));
        s.Feed(new string('y', 7) + "\n");
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(t));
        Assert.Null(await Read(t)); // unusable afterwards
    }

    [Fact]
    public async Task Default_limit_is_one_mebibyte()
    {
        var (t, s) = Create();
        var big = new byte[ProtocolConstants.MaxStreamLineBytes + 1];
        Array.Fill(big, (byte)'a');
        for (var i = 0; i < big.Length; i += 100_000) s.Feed(big[i..Math.Min(big.Length, i + 100_000)]);
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(t));

        var (t2, s2) = Create();
        var ok = new byte[ProtocolConstants.MaxStreamLineBytes + 1];
        Array.Fill(ok, (byte)'b');
        ok[^1] = (byte)'\n';
        s2.Feed(ok);
        Assert.Equal(ProtocolConstants.MaxStreamLineBytes, (await Read(t2))!.Length);
    }

    [Fact]
    public async Task End_of_stream_returns_last_partial_line_then_null()
    {
        var (t, s) = Create();
        s.Feed("{\"t\":\"ping\"}\n{\"t\":\"pong\"}");
        s.End();
        Assert.Equal("{\"t\":\"ping\"}", await Read(t));
        Assert.Equal("{\"t\":\"pong\"}", await Read(t));
        Assert.Null(await Read(t));
        Assert.Null(await Read(t));
    }

    [Fact]
    public async Task Broken_connection_returns_null_and_drops_half_line()
    {
        var (t, s) = Create();
        s.Feed("{\"t\":\"pi");
        var read = Read(t);
        await Task.Delay(30);
        s.Break(new InvalidOperationException("radio off"));
        Assert.Null(await read);
    }

    [Fact]
    public async Task Cancelled_read_does_not_lose_data()
    {
        var (t, s) = Create();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t.ReadLineAsync(cts.Token));
        s.Feed("{\"t\":\"hello\"}\n");
        Assert.Equal("{\"t\":\"hello\"}", await Read(t));
        Assert.Equal(1, s.Reads); // the pending read was continued, not restarted
    }

    [Fact]
    public async Task Writes_add_newline_flush_and_do_not_interleave()
    {
        var (t, s) = Create();
        var lines = Enumerable.Range(0, 50).Select(i => $"{{\"t\":\"state\",\"n\":{i},\"pad\":\"{new string('ж', 40 + i)}\"}}").ToArray();
        await Task.WhenAll(lines.Select(l => Task.Run(() => t.WriteLineAsync(l, CancellationToken.None))));
        var written = s.Written;
        Assert.EndsWith("\n", written);
        var got = written.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(lines.OrderBy(x => x), got.OrderBy(x => x));
        Assert.Equal(50, s.Flushes);
    }

    [Fact]
    public async Task Write_rejects_embedded_newline()
    {
        var (t, _) = Create();
        await Assert.ThrowsAsync<ArgumentException>(() => t.WriteLineAsync("a\nb", CancellationToken.None));
    }

    [Fact]
    public async Task Close_disposes_streams_once_and_unblocks_reader()
    {
        var closes = 0;
        var (t, s) = Create(onClose: () => { closes++; return ValueTask.CompletedTask; });
        var read = Read(t);
        await Task.Delay(30);
        await t.CloseAsync("bye", CancellationToken.None);
        await t.CloseAsync("again", CancellationToken.None);
        await t.DisposeAsync();
        Assert.Null(await read);
        Assert.True(s.Disposed);
        Assert.Equal(1, closes);
        Assert.True(t.IsClosed);
        await Assert.ThrowsAsync<IOException>(() => t.WriteLineAsync("{}", CancellationToken.None));
    }

    [Theory]
    [InlineData(0x0468749A2BFCUL, "04:68:74:9A:2B:FC")]
    [InlineData(0UL, "00:00:00:00:00:00")]
    [InlineData(0xFFFFFFFFFFFFUL, "FF:FF:FF:FF:FF:FF")]
    [InlineData(0xABCD_0011_2233_4455UL, "00:11:22:33:44:55")] // bits above 48 are ignored
    public void Bluetooth_address_is_formatted_upper_case_with_colons(ulong value, string expected)
    {
        Assert.Equal(expected, BluetoothFormat.FormatAddress(value));
        Assert.True(BluetoothFormat.TryParseAddress(expected.ToLowerInvariant().Replace(':', '-'), out var back));
        Assert.Equal(value & 0xFFFF_FFFF_FFFFUL, back);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("04:68:74:9A:2B")]
    [InlineData("04:68:74:9A:2B:GG")]
    [InlineData("0468749A2BFC")]
    public void Bluetooth_address_parse_rejects_garbage(string? text) => Assert.False(BluetoothFormat.TryParseAddress(text, out _));

    [Fact]
    public void Sdp_service_name_is_a_text_data_element()
    {
        var bytes = BluetoothFormat.EncodeSdpText(ProtocolConstants.BluetoothServiceName);
        Assert.Equal(0x25, bytes[0]);
        Assert.Equal(10, bytes[1]);
        Assert.Equal("VLC Remote", Encoding.UTF8.GetString(bytes, 2, bytes.Length - 2));

        var longText = BluetoothFormat.EncodeSdpText(new string('z', 300));
        Assert.Equal(new byte[] { 0x26, 0x01, 0x2C }, longText[..3]);
        Assert.Equal(303, longText.Length);
    }

    [Fact]
    public void Service_uuid_matches_the_protocol()
    {
        Assert.Equal("47fe5bd6-98ad-4d9f-9812-174b3394037d", ProtocolConstants.BluetoothServiceUuid.ToString());
    }
}
