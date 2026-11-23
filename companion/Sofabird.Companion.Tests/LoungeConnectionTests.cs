using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Sofabird.Companion.Tests.Support;
using Sofabird.Companion.YouTube.Cast;
using Sofabird.Core.YouTube.Cast;
using Xunit;
using Xunit.Abstractions;

namespace Sofabird.Companion.Tests;

/// <summary>The companion's lounge connection against a scripted YouTube: the requests it makes, in order.</summary>
public sealed class LoungeConnectionTests(ITestOutputHelper output)
{
    private const string Init = "300\n[[0,[\"c\",\"SID1\",\"\",8]]\n,[1,[\"S\",\"GSID1\"]]\n," +
                                "[2,[\"loungeStatus\",{\"devices\":\"[]\"}]]\n]\n";
    private const string Cast = "100\n[[11,[\"remoteConnected\",{\"id\":\"phone-1\",\"name\":\"Pixel\"}]]\n]\n" +
                                "80\n[[12,[\"setPlaylist\",{\"videoId\":\"aqz-KE-bpKQ\",\"currentTime\":\"0\"}]]\n]\n";

    private ILogger Log()
    {
        using var lf = new TestLoggerFactory(output);
        return lf.CreateLogger("lounge");
    }

    [Fact]
    public async Task Gets_a_screen_and_a_token_then_listens_and_sends()
    {
        var youtube = new FakeYouTube { Polls = { Cast } };
        string? saved = null;
        var received = new ConcurrentQueue<string>();
        await using var lounge = new LoungeConnection(new HttpClient(youtube), new LoungeScreen("device-1", "TV PC"), () => saved,
            id => saved = id, m => { received.Enqueue(m.Name); return Task.CompletedTask; }, Log());
        lounge.Start();

        await Until(() => received.Contains("setPlaylist"));
        Assert.True(lounge.Bound);
        Assert.Equal("screen0123456789abcdef", saved);
        Assert.Equal(["loungeStatus", "remoteConnected", "setPlaylist"], received); // not c, S
        Assert.Contains("screen_ids=screen0123456789abcdef", youtube.Body("get_lounge_token_batch"));
        Assert.Contains("loungeIdToken=token-1", youtube.Url("bind", init: true));

        lounge.Send([new LoungeCommand("nowPlaying", [new("videoId", "aqz-KE-bpKQ")])]);
        await Until(() => !youtube.Sent.IsEmpty);
        var (url, body) = youtube.Sent.First();
        Assert.Contains("SID=SID1", url);
        Assert.Contains("gsessionid=GSID1", url);
        Assert.Contains("AID=12", url); // acknowledges what arrived
        Assert.Equal("count=1&ofs=0&req0__sc=nowPlaying&req0_videoId=aqz-KE-bpKQ", body);
    }

    [Fact]
    public async Task A_session_youtube_forgot_is_started_again()
    {
        var youtube = new FakeYouTube { PollStatus = HttpStatusCode.BadRequest };
        await using var lounge = new LoungeConnection(new HttpClient(youtube), new LoungeScreen("device-1", "TV PC"),
            () => "screen0123456789abcdef", _ => { }, _ => Task.CompletedTask, Log());
        lounge.Start();
        await Until(() => youtube.Inits >= 2);
        Assert.Equal(1, youtube.Count("get_lounge_token_batch")); // the token is still good
    }

    [Fact]
    public async Task A_screen_id_youtube_no_longer_knows_is_replaced()
    {
        var youtube = new FakeYouTube { KnownScreen = "screen0123456789abcdef" };
        var saved = "oldscreen0123456789";
        await using var lounge = new LoungeConnection(new HttpClient(youtube), new LoungeScreen("device-1", "TV PC"), () => saved,
            id => saved = id!, _ => Task.CompletedTask, Log());
        lounge.Start();
        await Until(() => lounge.Bound);
        Assert.Equal("screen0123456789abcdef", saved);
    }

    [Fact]
    public async Task Registers_a_phones_pairing_code_for_this_screen()
    {
        var youtube = new FakeYouTube();
        await using var lounge = new LoungeConnection(new HttpClient(youtube), new LoungeScreen("device-1", "TV PC"),
            () => "screen0123456789abcdef", _ => { }, _ => Task.CompletedTask, Log());
        await lounge.RegisterPairingCodeAsync("code-42", CancellationToken.None);
        var body = youtube.Body("register_pairing_code");
        Assert.Contains("access_type=permanent", body);
        Assert.Contains("app=sofabird", body);
        Assert.Contains("pairing_code=code-42", body);
        Assert.Contains("screen_id=screen0123456789abcdef", body);
        Assert.Contains("screen_name=TV+PC", body);
        Assert.Contains("device_id=device-1", body);
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("the condition did not come true");
            await Task.Delay(20);
        }
    }

    /// <summary>YouTube's lounge endpoints: canned answers; a poll streams its text, then stays open.</summary>
    private sealed class FakeYouTube : HttpMessageHandler
    {
        private readonly ConcurrentQueue<(string Url, string Body)> _requests = new();

        public string KnownScreen { get; init; } = "screen0123456789abcdef";
        public List<string> Polls { get; } = new();
        public HttpStatusCode PollStatus { get; init; } = HttpStatusCode.OK;
        public ConcurrentQueue<(string Url, string Body)> Sent { get; } = new();
        public int Inits;
        private int _polls;

        public string Url(string path, bool init) =>
            _requests.First(r => r.Url.Contains(path, StringComparison.Ordinal) && (!init || !r.Url.Contains("SID=", StringComparison.Ordinal))).Url;

        public string Body(string path) => _requests.First(r => r.Url.Contains(path, StringComparison.Ordinal)).Body;

        public int Count(string path) => _requests.Count(r => r.Url.Contains(path, StringComparison.Ordinal));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            _requests.Enqueue((url, body));
            var path = request.RequestUri.AbsolutePath;
            if (path.EndsWith("/generate_screen_id", StringComparison.Ordinal)) return Text(KnownScreen);
            if (path.EndsWith("/get_lounge_token_batch", StringComparison.Ordinal))
                return Text(body.Contains(KnownScreen, StringComparison.Ordinal)
                    ? $$"""{"screens":[{"screenId":"{{KnownScreen}}","loungeToken":"token-1","refreshIntervalInMillis":1123200000}]}"""
                    : """{"screens":[]}""");
            if (path.EndsWith("/register_pairing_code", StringComparison.Ordinal)) return Text("");
            if (!path.EndsWith("/bc/bind", StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (request.Method == HttpMethod.Get)
            {
                if (PollStatus != HttpStatusCode.OK) return new HttpResponseMessage(PollStatus);
                var n = Interlocked.Increment(ref _polls) - 1;
                var text = n < Polls.Count ? Polls[n] : "";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new OpenStream(text)) };
            }
            if (!url.Contains("SID=", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref Inits);
                return Text(Init);
            }
            Sent.Enqueue((url, body));
            return Text("7\n[1,7,0]");
        }

        private static HttpResponseMessage Text(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8) };
    }

    /// <summary>A long poll: its text, then nothing more until the reader gives up.</summary>
    private sealed class OpenStream(string text) : Stream
    {
        private readonly MemoryStream _data = new(Encoding.UTF8.GetBytes(text));

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var n = _data.Read(buffer.Span);
            if (n > 0) return n;
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
