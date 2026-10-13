using System.IO.Pipelines;
using System.Text;
using VlcRemote.Core.Vlc;
using Xunit;

namespace VlcRemote.Core.Tests;

public class VlcRcClientTests
{
    /// <summary>Two Pipes = a bidirectional in-memory stream; the test plays the role of VLC's RC server.</summary>
    private sealed class FakeRcServer
    {
        private readonly Pipe _clientToServer = new();
        private readonly Pipe _serverToClient = new();

        public Stream ClientStream => new DuplexStream(_serverToClient.Reader.AsStream(), _clientToServer.Writer.AsStream());

        public async Task<string> ReadCommandAsync()
        {
            using var reader = new StreamReader(_clientToServer.Reader.AsStream(leaveOpen: true), Encoding.UTF8, false, 1024, leaveOpen: true);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            return (await reader.ReadLineAsync(cts.Token))!;
        }

        public async Task SendAsync(params string[] lines)
        {
            foreach (var l in lines)
                await _serverToClient.Writer.WriteAsync(Encoding.UTF8.GetBytes(l + "\r\n"));
            await _serverToClient.Writer.FlushAsync();
        }

        public void Close() => _serverToClient.Writer.Complete();

        private sealed class DuplexStream(Stream read, Stream write) : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() => write.Flush();
            public override Task FlushAsync(CancellationToken ct) => write.FlushAsync(ct);
            public override int Read(byte[] buffer, int offset, int count) => read.Read(buffer, offset, count);
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => read.ReadAsync(buffer, ct);
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => write.Write(buffer, offset, count);
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => write.WriteAsync(buffer, ct);
            protected override void Dispose(bool disposing)
            {
                if (disposing) { read.Dispose(); write.Dispose(); }
                base.Dispose(disposing);
            }
        }
    }

    [Fact]
    public async Task Adev_ResponseIsCorrelatedAndStatusChangesAreEvents()
    {
        var server = new FakeRcServer();
        await using var client = new VlcRcClient(_ => Task.FromResult(server.ClientStream));
        var events = new List<RcStatusChange>();
        client.StatusChanged += events.Add;

        await client.ConnectAsync();
        // On connect VLC immediately prints the current state.
        await server.SendAsync("status change: ( new input: file:///D:/x.mkv )", "status change: ( play state: 3 )");

        var task = client.ListAudioDevicesAsync();
        Assert.Equal("adev", await server.ReadCommandAsync());
        await server.SendAsync(Fixtures.ReadLines("rc-adev.txt").Where(l => !RcParser.IsStatusChange(l)).ToArray());
        var devices = await task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(4, devices.Count);
        Assert.True(devices[0].Current);
        Assert.Equal("{0.0.0.00000000}.{98351b19-b7cb-44c1-92e3-e22d7d917a19}", devices[2].Id);
        await Task.Delay(50);
        Assert.Equal(2, events.Count);
        Assert.Equal(RcStatusKind.NewInput, events[0].Kind);
    }

    [Fact]
    public async Task Atrack_WhilePaused_ReturnsNull()
    {
        var server = new FakeRcServer();
        await using var client = new VlcRcClient(_ => Task.FromResult(server.ClientStream));
        var task = client.ListTracksAsync(VlcTrackKind.Audio);
        Assert.Equal("atrack", await server.ReadCommandAsync());
        await server.SendAsync("Press pause to continue.", "atrack: returned 1 (generic error)");
        Assert.Null(await task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Atrack_ParsesCurrentMarker_AndInterleavedEvents()
    {
        var server = new FakeRcServer();
        await using var client = new VlcRcClient(_ => Task.FromResult(server.ClientStream));
        var task = client.ListTracksAsync(VlcTrackKind.Subtitle);
        Assert.Equal("strack", await server.ReadCommandAsync());
        await server.SendAsync(
            "+----[ Subtitle Track ]",
            "| -1 - Disable",
            "status change: ( audio volume: 266 )",
            "| 7 - Мосфильм / SRT - [Russian] *",
            "| 10 - Warner Bros. / SRT - [English]",
            "+----[ end of Subtitle Track ]",
            "strack: returned 0 (no error)");
        var tracks = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(tracks);
        Assert.Equal(3, tracks.Count);
        Assert.Equal(7, tracks.Single(t => t.Current).Id);
        Assert.Equal("English", tracks[2].Language);
    }

    [Fact]
    public async Task UnknownCommand_Throws()
    {
        var server = new FakeRcServer();
        await using var client = new VlcRcClient(_ => Task.FromResult(server.ClientStream));
        var task = client.SendAsync("adev");
        await server.ReadCommandAsync();
        await server.SendAsync("Unknown command `adev'. Type `help' for help.");
        await Assert.ThrowsAsync<VlcRcException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Timeout_WithoutEcho_Throws()
    {
        var server = new FakeRcServer();
        await using var client = new VlcRcClient(_ => Task.FromResult(server.ClientStream));
        var task = client.SendAsync("adev", timeout: TimeSpan.FromMilliseconds(200));
        await server.ReadCommandAsync();
        await Assert.ThrowsAsync<VlcRcException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task CommandWithoutEcho_CompletesOnIdle()
    {
        var server = new FakeRcServer();
        await using var client = new VlcRcClient(_ => Task.FromResult(server.ClientStream)) { IdleCompletion = TimeSpan.FromMilliseconds(100) };
        var task = client.SendAsync("get_time");
        Assert.Equal("get_time", await server.ReadCommandAsync());
        await server.SendAsync("904");
        var r = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("904", Assert.Single(r.Lines));
        Assert.Null(r.ReturnCode);
    }

    [Fact]
    public async Task ServerClose_FailsPendingAndRaisesDisconnected()
    {
        var server = new FakeRcServer();
        await using var client = new VlcRcClient(_ => Task.FromResult(server.ClientStream));
        Exception? disconnected = null;
        var signal = new TaskCompletionSource();
        client.Disconnected += e => { disconnected = e; signal.TrySetResult(); };
        var task = client.SendAsync("adev");
        await server.ReadCommandAsync();
        server.Close();
        await Assert.ThrowsAsync<VlcUnavailableException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        await signal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(client.IsConnected);
        Assert.Null(disconnected);
    }

    [Fact]
    public async Task Multiline_And_Empty_Commands_AreRejected()
    {
        await using var client = new VlcRcClient(_ => throw new InvalidOperationException());
        await Assert.ThrowsAsync<ArgumentException>(() => client.SendAsync(""));
        await Assert.ThrowsAsync<ArgumentException>(() => client.SendAsync("adev\nquit"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.SetAudioDeviceAsync(""));
    }
}
