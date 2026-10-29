using System.Text.Json;
using Sofabird.Core.Pairing;
using Sofabird.Core.Protocol;
using Sofabird.Core.Session;
using Sofabird.Core.Vlc;
using Xunit;

namespace Sofabird.Core.Tests;

public class SessionTests
{
    private sealed class FakeHost : ISessionHost
    {
        public FakeHost(PairingService pairing) => Pairing = pairing;

        public string MachineName => "HTPC";
        public string InstallationId => "pc-guid";
        public IReadOnlyList<string> Caps { get; } = [Core.Protocol.Caps.CurrentTrack, Core.Protocol.Caps.PushEvents];
        public VlcInfo VlcInfo { get; } = new() { Running = true, Version = "3.0.23", ApiVersion = 3, Level = 2 };
        public PairingService Pairing { get; }
        public string? BluetoothAddress { get; set; } = "02:1A:2B:3C:4D:60";
        public IReadOnlyList<string>? WakeMacs { get; set; } = ["02:1A:2B:3C:4D:5E"];
        public TlsInfo? Tls { get; set; } = new() { Port = 47557, Sha256 = "AB12" };
        public List<(string Name, CommandArgs Args)> Commands { get; } = new();
        public bool VlcDown { get; set; }
        public List<DeviceInfo?> PairingRequests { get; } = new();

        public void RequestPairing(DeviceInfo? device) => PairingRequests.Add(device);

        public Task<StateMessage> GetStateAsync(CancellationToken ct) =>
            Task.FromResult(new StateMessage { Vlc = VlcInfo, Playback = new PlaybackInfo { State = "paused", Time = 10 } });

        public Task ExecuteCommandAsync(string name, CommandArgs args, CancellationToken ct)
        {
            if (VlcDown) throw new VlcUnavailableException("connection refused");
            if (name == "boom") throw new InvalidOperationException("kaboom");
            if (name == "seek" && !args.Has("seconds") && !args.Has("delta")) throw ProtocolException.BadRequest("seek requires seconds or delta");
            if (name == "unknown") throw ProtocolException.Unsupported("unknown command");
            Commands.Add((name, args));
            return Task.CompletedTask;
        }

        public Task<Message?> ExecuteSysAsync(string name, CommandArgs args, CancellationToken ct)
        {
            if (name == "listDisplays")
                return Task.FromResult<Message?>(new DisplaysMessage { Items = [new DisplayEntry { Index = 0, Name = "Main", Primary = true }] });
            Commands.Add(("sys:" + name, args));
            return Task.FromResult<Message?>(null);
        }

        public Task<IReadOnlyList<BrowseEntry>> BrowseAsync(string uri, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<BrowseEntry>>([new BrowseEntry { Name = "D:\\", Uri = "file:///D:/", Type = "dir" }]);

        public Task<IReadOnlyList<PlaylistEntry>> GetPlaylistAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PlaylistEntry>>([new PlaylistEntry { PlId = 3, Name = "LOTR", Uri = "file:///D:/x.mkv", DurationSec = 10705, Current = true }]);

        public Task<TracksInfo> GetTracksAsync(CancellationToken ct) =>
            Task.FromResult(new TracksInfo { Audio = [new TrackInfo { Id = 1, Name = "A" }], CurrentAudio = 1 });

        public Task<IReadOnlyList<AudioDeviceEntry>> GetAudioDevicesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AudioDeviceEntry>>([new AudioDeviceEntry { Id = "", Name = "Default", Current = true }]);

        public List<string> HistoryCalls { get; } = new();

        public Task<LibraryMessage> GetLibraryAsync(CancellationToken ct) =>
            Task.FromResult(new LibraryMessage { Revision = 7, Movies = [new MovieItem { Key = "the matrix|1999", Title = "The Matrix" }] });

        public Task<ShowMessage?> GetShowAsync(string key, CancellationToken ct) =>
            Task.FromResult(key == "severance" ? new ShowMessage { Show = new ShowItem { Key = key, Title = "Severance" } } : null);

        public Task<HistoryMessage> GetHistoryAsync(string filter, int limit, int offset, CancellationToken ct)
        {
            HistoryCalls.Add($"get {filter} {limit} {offset}");
            return Task.FromResult(new HistoryMessage
            {
                Total = 3,
                Items = [new HistoryItem { Uri = "file:///d:/s/e02.mkv", Title = "E02", Name = "e02.mkv", FolderUri = "file:///d:/s/", Position = 0, Length = 0, Next = true, LastWatched = 5 }],
            });
        }

        public Task UpdateHistoryAsync(string uri, bool? finished, bool remove, CancellationToken ct)
        {
            HistoryCalls.Add($"update {uri} {finished?.ToString() ?? "-"} {remove}");
            return Task.CompletedTask;
        }

        public Task ClearHistoryAsync(CancellationToken ct)
        {
            HistoryCalls.Add("clear");
            return Task.CompletedTask;
        }
    }

    private static (InMemoryLineTransport transport, Core.Session.Session session, FakeHost host, PairingService pairing, Task run) Start(string? pairedToken = null)
    {
        var store = new InMemoryTokenStore();
        var pairing = new PairingService(store);
        if (pairedToken is not null)
            store.Save([new PairedDevice { TokenHash = PairingService.Hash(pairedToken), DeviceName = "old" }]);
        var host = new FakeHost(pairing);
        var transport = new InMemoryLineTransport();
        var session = new Core.Session.Session(transport, host) { HelloTimeout = TimeSpan.FromSeconds(2) };
        var run = session.RunAsync(CancellationToken.None);
        return (transport, session, host, pairing, run);
    }

    private static JsonElement Recv(InMemoryLineTransport t) => JsonDocument.Parse(t.PhoneReceiveAsync().GetAwaiter().GetResult()).RootElement;

    [Fact]
    public async Task Hello_WithoutToken_RequestsPairingAndCloses()
    {
        var (t, _, host, _, run) = Start();
        await t.PhoneSendAsync("""{"t":"hello","v":1,"device":{"name":"Pixel 8","id":"d"},"token":null,"pairCode":null}""");
        var err = Recv(t);
        Assert.Equal("err", err.GetProperty("t").GetString());
        Assert.Equal("pairing_required", err.GetProperty("code").GetString());
        Assert.Equal("pairing required", err.GetProperty("msg").GetString());
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(t.Closed);
        var request = Assert.Single(host.PairingRequests);
        Assert.Equal("Pixel 8", request!.Name);
        Assert.Equal("d", request.Id);
    }

    [Fact]
    public async Task Hello_WithUnknownToken_RequestsPairing()
    {
        var (t, _, host, _, run) = Start("tok");
        await t.PhoneSendAsync("""{"t":"hello","v":1,"device":{"name":"P","id":"d"},"token":"forgotten"}""");
        Assert.Equal("pairing_required", Recv(t).GetProperty("code").GetString());
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(host.PairingRequests);
    }

    [Fact]
    public async Task Hello_PairCodeWithoutActivePairing_UnauthorizedAndRequestsNewCode()
    {
        var (t, _, host, _, run) = Start();
        await t.PhoneSendAsync("""{"t":"hello","v":1,"device":{"name":"P","id":"d"},"pairCode":"123456"}""");
        Assert.Equal("unauthorized", Recv(t).GetProperty("code").GetString());
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(host.PairingRequests); // the code expired/did not exist — a window with a new code
    }

    [Fact]
    public async Task Hello_WithPairCode_ReturnsWelcomeWithToken_ThenTokenWorks()
    {
        var (t, session, _, pairing, run) = Start();
        var code = pairing.BeginPairing();
        await t.PhoneSendAsync($$"""{"t":"hello","v":1,"device":{"name":"Pixel 8","id":"dev-1"},"pairCode":"{{code}}"}""");
        var welcome = Recv(t);
        Assert.Equal("welcome", welcome.GetProperty("t").GetString());
        Assert.Equal("HTPC", welcome.GetProperty("name").GetString());
        Assert.Equal("pc-guid", welcome.GetProperty("id").GetString());
        Assert.Equal(1, welcome.GetProperty("v").GetInt32());
        var token = welcome.GetProperty("token").GetString()!;
        Assert.True(pairing.ValidateToken(token));
        Assert.True(welcome.GetProperty("vlc").GetProperty("running").GetBoolean());
        Assert.Equal("02:1A:2B:3C:4D:60", welcome.GetProperty("bt").GetString());
        Assert.Equal("02:1A:2B:3C:4D:5E", welcome.GetProperty("macs")[0].GetString());
        Assert.Equal(47557, welcome.GetProperty("tls").GetProperty("port").GetInt32());
        Assert.Equal("AB12", welcome.GetProperty("tls").GetProperty("sha256").GetString());
        Assert.True(session.IsAuthenticated);
        Assert.Equal("Pixel 8", session.Device!.Name);

        t.PhoneDisconnect();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        // A second session with the issued token.
        var store2 = pairing;
        var host2 = new FakeHost(store2) { BluetoothAddress = null, WakeMacs = null }; // Bluetooth off — no "bt"; not known yet — no "macs"
        var t2 = new InMemoryLineTransport();
        var s2 = new Core.Session.Session(t2, host2);
        var run2 = s2.RunAsync(CancellationToken.None);
        await t2.PhoneSendAsync($$"""{"t":"hello","v":1,"device":{"name":"Pixel 8","id":"dev-1"},"token":"{{token}}"}""");
        var w2 = Recv(t2);
        Assert.Equal("welcome", w2.GetProperty("t").GetString());
        Assert.False(w2.TryGetProperty("token", out _)); // the token is issued only with pairCode
        Assert.False(w2.TryGetProperty("bt", out _));
        Assert.False(w2.TryGetProperty("macs", out _));
        t2.PhoneDisconnect();
        await run2.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Hello_WrongPairCode_Unauthorized()
    {
        var (t, _, host, pairing, run) = Start();
        var code = pairing.BeginPairing();
        var wrong = code == "111111" ? "222222" : "111111";
        await t.PhoneSendAsync($$"""{"t":"hello","v":1,"device":{"name":"P","id":"d"},"pairCode":"{{wrong}}"}""");
        var err = Recv(t);
        Assert.Equal("unauthorized", err.GetProperty("code").GetString());
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(pairing.IsPairingActive); // a wrong code does not close the window
        Assert.Empty(host.PairingRequests);
    }

    [Fact]
    public async Task FirstMessageNotHello_Unauthorized()
    {
        var (t, _, _, _, run) = Start();
        await t.PhoneSendAsync("""{"t":"cmd","id":"1","name":"play"}""");
        var err = Recv(t);
        Assert.Equal("unauthorized", err.GetProperty("code").GetString());
        Assert.Equal("1", err.GetProperty("id").GetString());
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Hello_WrongVersion_Unsupported()
    {
        var (t, _, _, _, run) = Start("tok");
        await t.PhoneSendAsync("""{"t":"hello","v":2,"token":"tok"}""");
        Assert.Equal("unsupported", Recv(t).GetProperty("code").GetString());
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Authenticated_RequestsAreDispatched()
    {
        var (t, session, host, _, run) = Start("tok");
        await t.PhoneSendAsync("""{"t":"hello","v":1,"device":{"name":"P","id":"d"},"token":"tok"}""");
        Assert.Equal("welcome", Recv(t).GetProperty("t").GetString());

        await t.PhoneSendAsync("""{"t":"ping"}""");
        Assert.Equal("pong", Recv(t).GetProperty("t").GetString());

        await t.PhoneSendAsync("""{"t":"cmd","id":"7","name":"seek","args":{"delta":30}}""");
        var ok = Recv(t);
        Assert.Equal("ok", ok.GetProperty("t").GetString());
        Assert.Equal("7", ok.GetProperty("id").GetString());
        Assert.Equal("seek", host.Commands.Single().Name);
        Assert.Equal(30, host.Commands.Single().Args.GetInt("delta"));

        await t.PhoneSendAsync("""{"t":"cmd","id":"8","name":"seek","args":{}}""");
        var bad = Recv(t);
        Assert.Equal("err", bad.GetProperty("t").GetString());
        Assert.Equal("bad_request", bad.GetProperty("code").GetString());
        Assert.Equal("8", bad.GetProperty("id").GetString());

        await t.PhoneSendAsync("""{"t":"cmd","id":"9","name":"unknown"}""");
        Assert.Equal("unsupported", Recv(t).GetProperty("code").GetString());

        await t.PhoneSendAsync("""{"t":"cmd","id":"10","name":"boom"}""");
        Assert.Equal("internal", Recv(t).GetProperty("code").GetString());

        host.VlcDown = true;
        await t.PhoneSendAsync("""{"t":"cmd","id":"11","name":"play"}""");
        Assert.Equal("vlc_not_running", Recv(t).GetProperty("code").GetString());
        host.VlcDown = false;

        await t.PhoneSendAsync("""{"t":"getState","id":"4"}""");
        var state = Recv(t);
        Assert.Equal("state", state.GetProperty("t").GetString());
        Assert.Equal("4", state.GetProperty("id").GetString());
        Assert.Equal("paused", state.GetProperty("playback").GetProperty("state").GetString());

        await t.PhoneSendAsync("""{"t":"browse","id":"3","uri":"file:///"}""");
        var entries = Recv(t);
        Assert.Equal("entries", entries.GetProperty("t").GetString());
        Assert.Equal("file:///", entries.GetProperty("uri").GetString());
        Assert.Equal("dir", entries.GetProperty("items")[0].GetProperty("type").GetString());

        await t.PhoneSendAsync("""{"t":"getPlaylist","id":"5"}""");
        var pl = Recv(t);
        Assert.Equal("playlist", pl.GetProperty("t").GetString());
        Assert.Equal(3, pl.GetProperty("items")[0].GetProperty("plId").GetInt32());
        Assert.True(pl.GetProperty("items")[0].GetProperty("current").GetBoolean());

        await t.PhoneSendAsync("""{"t":"getTracks","id":"6"}""");
        var tr = Recv(t);
        Assert.Equal("tracks", tr.GetProperty("t").GetString());
        Assert.Equal(1, tr.GetProperty("currentAudio").GetInt32());

        await t.PhoneSendAsync("""{"t":"getAudioDevices","id":"8"}""");
        var ad = Recv(t);
        Assert.Equal("audioDevices", ad.GetProperty("t").GetString());
        Assert.True(ad.GetProperty("items")[0].GetProperty("current").GetBoolean());

        await t.PhoneSendAsync("""{"t":"sys","id":"12","name":"listDisplays"}""");
        var disp = Recv(t);
        Assert.Equal("displays", disp.GetProperty("t").GetString());
        Assert.Equal("12", disp.GetProperty("id").GetString());

        await t.PhoneSendAsync("""{"t":"sys","id":"13","name":"keepAwake","args":{"on":true}}""");
        Assert.Equal("ok", Recv(t).GetProperty("t").GetString());
        Assert.Contains(host.Commands, c => c.Name == "sys:keepAwake" && c.Args.GetBool("on") == true);

        await t.PhoneSendAsync("not json at all");
        Assert.Equal("bad_request", Recv(t).GetProperty("code").GetString());

        await t.PhoneSendAsync("""{"t":"hello","v":1}""");
        Assert.Equal("bad_request", Recv(t).GetProperty("code").GetString());

        // Push from the PC
        await session.SendAsync(new EventMessage { Name = EventNames.VlcStarted });
        Assert.Equal("vlcStarted", Recv(t).GetProperty("name").GetString());

        t.PhoneDisconnect();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(t.Closed);
    }

    [Fact]
    public async Task LibraryRequests_AreAnswered()
    {
        var (t, _, _, _, run) = Start("tok");
        await t.PhoneSendAsync("""{"t":"hello","v":1,"device":{"name":"P","id":"d"},"token":"tok"}""");
        Assert.Equal("welcome", Recv(t).GetProperty("t").GetString());

        await t.PhoneSendAsync("""{"t":"getLibrary","id":"3"}""");
        var lib = Recv(t);
        Assert.Equal(("library", "3", 7L), (lib.GetProperty("t").GetString(), lib.GetProperty("id").GetString(), lib.GetProperty("revision").GetInt64()));
        Assert.Equal("The Matrix", lib.GetProperty("movies")[0].GetProperty("title").GetString());

        await t.PhoneSendAsync("""{"t":"getShow","id":"4","key":"severance"}""");
        var show = Recv(t);
        Assert.Equal(("show", "4", "Severance"), (show.GetProperty("t").GetString(), show.GetProperty("id").GetString(),
            show.GetProperty("show").GetProperty("title").GetString()));

        await t.PhoneSendAsync("""{"t":"getShow","id":"5","key":"nope"}""");
        Assert.Equal("bad_request", Recv(t).GetProperty("code").GetString());
        t.PhoneDisconnect();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task HistoryRequests_AreDispatchedAndValidated()
    {
        var (t, _, host, _, run) = Start("tok");
        await t.PhoneSendAsync("""{"t":"hello","v":1,"device":{"name":"P","id":"d","locale":"ru-RU"},"token":"tok"}""");
        Assert.Equal("welcome", Recv(t).GetProperty("t").GetString());

        await t.PhoneSendAsync("""{"t":"getHistory","id":"9","filter":"continue","limit":20,"offset":40}""");
        var h = Recv(t);
        Assert.Equal("history", h.GetProperty("t").GetString());
        Assert.Equal("9", h.GetProperty("id").GetString());
        Assert.Equal(3, h.GetProperty("total").GetInt32());
        var item = h.GetProperty("items")[0];
        Assert.True(item.GetProperty("next").GetBoolean());
        Assert.Equal("file:///d:/s/", item.GetProperty("folderUri").GetString());
        Assert.False(item.TryGetProperty("artUrl", out _));

        await t.PhoneSendAsync("""{"t":"getHistory","id":"10"}""");
        Assert.Equal("history", Recv(t).GetProperty("t").GetString());
        await t.PhoneSendAsync("""{"t":"getHistory","id":"11","limit":100000,"offset":-5}""");
        Recv(t);
        Assert.Equal(["get continue 20 40", "get all 50 0", "get all 500 0"], host.HistoryCalls);

        await t.PhoneSendAsync("""{"t":"getHistory","id":"12","filter":"recent"}""");
        var bad = Recv(t);
        Assert.Equal("bad_request", bad.GetProperty("code").GetString());
        Assert.Equal("12", bad.GetProperty("id").GetString());

        await t.PhoneSendAsync("""{"t":"historyUpdate","id":"13","uri":"file:///d:/x.mkv","finished":true}""");
        Assert.Equal("ok", Recv(t).GetProperty("t").GetString());
        await t.PhoneSendAsync("""{"t":"historyUpdate","id":"14","uri":"file:///d:/x.mkv","remove":true}""");
        Assert.Equal("ok", Recv(t).GetProperty("t").GetString());
        await t.PhoneSendAsync("""{"t":"historyUpdate","id":"15","uri":"file:///d:/x.mkv"}""");
        Assert.Equal("bad_request", Recv(t).GetProperty("code").GetString());
        await t.PhoneSendAsync("""{"t":"historyClear","id":"16"}""");
        var ok = Recv(t);
        Assert.Equal("ok", ok.GetProperty("t").GetString());
        Assert.Equal("16", ok.GetProperty("id").GetString());
        Assert.Equal(["update file:///d:/x.mkv True False", "update file:///d:/x.mkv - True", "clear"], host.HistoryCalls.Skip(3));

        // New commands go through the usual cmd path.
        await t.PhoneSendAsync("""{"t":"cmd","id":"17","name":"resume","args":{"choice":"resume"}}""");
        Assert.Equal("ok", Recv(t).GetProperty("t").GetString());
        await t.PhoneSendAsync("""{"t":"cmd","id":"18","name":"dismissNext"}""");
        Assert.Equal("ok", Recv(t).GetProperty("t").GetString());
        Assert.Equal(["resume", "dismissNext"], host.Commands.Select(c => c.Name));
        Assert.Equal("resume", host.Commands[0].Args.GetString("choice"));

        t.PhoneDisconnect();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void ResumeAndNextEvents_SerializeFlat()
    {
        var json = ProtocolJson.Serialize(new EventMessage
        {
            Name = EventNames.ResumePrompt, Uri = "file:///d:/x.mkv", Title = "X", Position = 1234, Length = 5000, Pending = true,
        });
        Assert.Equal("""{"t":"event","name":"resumePrompt","uri":"file:///d:/x.mkv","title":"X","position":1234,"length":5000,"pending":true}""", json);
        Assert.Equal("""{"t":"event","name":"sleeping"}""", ProtocolJson.Serialize(new EventMessage { Name = EventNames.Sleeping }));
        var next = ProtocolJson.Serialize(new EventMessage { Name = EventNames.NextEpisode, Uri = "file:///d:/e2.mkv", Title = "E2" });
        Assert.Equal("""{"t":"event","name":"nextEpisode","uri":"file:///d:/e2.mkv","title":"E2"}""", next);
    }

    [Fact]
    public async Task HelloTimeout_SendsUnauthorizedAndCloses()
    {
        var store = new InMemoryTokenStore();
        var host = new FakeHost(new PairingService(store));
        var t = new InMemoryLineTransport();
        var session = new Core.Session.Session(t, host) { HelloTimeout = TimeSpan.FromMilliseconds(200) };
        var run = session.RunAsync(CancellationToken.None);
        var err = Recv(t);
        Assert.Equal("unauthorized", err.GetProperty("code").GetString());
        Assert.Equal("hello timeout", err.GetProperty("msg").GetString());
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(t.Closed);
    }
}
