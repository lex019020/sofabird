using System.Text.Json;
using Sofabird.Core.Protocol;
using Xunit;

namespace Sofabird.Core.Tests;

public class ProtocolJsonTests
{
    [Fact]
    public void Hello_RoundTrip()
    {
        const string line = """{"t":"hello","v":1,"device":{"name":"Pixel 8","id":"abc"},"token":null,"pairCode":"123456"}""";
        var msg = Assert.IsType<HelloMessage>(ProtocolJson.Deserialize(line));
        Assert.Equal(1, msg.V);
        Assert.Equal("Pixel 8", msg.Device!.Name);
        Assert.Null(msg.Token);
        Assert.Equal("123456", msg.PairCode);

        var json = ProtocolJson.Serialize(msg);
        Assert.StartsWith("{\"t\":\"hello\"", json);
        Assert.DoesNotContain("token", json); // null is not written
    }

    [Fact]
    public void System_carries_the_moonlight_host_and_stream()
    {
        var json = ProtocolJson.Serialize(new StateMessage
        {
            System = new SystemInfo { MoonlightHost = "GAMING-PC", MoonlightStreaming = true },
        });
        Assert.Contains("\"moonlightHost\":\"GAMING-PC\"", json, StringComparison.Ordinal);
        Assert.Contains("\"moonlightStreaming\":true", json, StringComparison.Ordinal);

        // Without Moonlight on the PC both fields are absent.
        var none = ProtocolJson.Serialize(new StateMessage { System = new SystemInfo { KeepAwake = false } });
        Assert.DoesNotContain("moonlight", none, StringComparison.Ordinal);
    }

    [Fact]
    public void State_carries_youtube_under_its_protocol_name()
    {
        var json = ProtocolJson.Serialize(new StateMessage
        {
            YouTube = new YouTubeInfo { Open = true, Playing = true, VideoId = "aqz-KE-bpKQ", Title = "Big Buck Bunny", Time = 60, Length = 634.6 },
        });
        using var doc = JsonDocument.Parse(json);
        var yt = doc.RootElement.GetProperty("youtube");
        Assert.True(yt.GetProperty("open").GetBoolean());
        Assert.Equal("aqz-KE-bpKQ", yt.GetProperty("videoId").GetString());
        Assert.Equal(634.6, yt.GetProperty("length").GetDouble());
        Assert.DoesNotContain("youTube", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Discriminator_MayComeLast()
    {
        var msg = ProtocolJson.Deserialize("""{"id":"7","name":"seek","args":{"delta":30},"t":"cmd"}""");
        var cmd = Assert.IsType<CmdMessage>(msg);
        Assert.Equal("7", cmd.Id);
        Assert.Equal("seek", cmd.Name);
        Assert.Equal(30, cmd.Args!.Value.GetProperty("delta").GetInt32());
    }

    [Fact]
    public void UnknownType_Throws()
    {
        Assert.ThrowsAny<JsonException>(() => ProtocolJson.Deserialize("""{"t":"bogus"}"""));
        Assert.ThrowsAny<JsonException>(() => ProtocolJson.Deserialize("""{"id":"1"}"""));
    }

    [Fact]
    public void Welcome_UsesIdForPcGuid()
    {
        var json = ProtocolJson.Serialize(new WelcomeMessage
        {
            Id = "pc-guid",
            Name = "HTPC",
            Caps = [Caps.CurrentTrack, Caps.Power],
            Token = "tok",
            Vlc = new VlcInfo { Running = true, Status = VlcStatusNames.Running, Version = "3.0.23", ApiVersion = 3, Level = 2 },
        });
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("welcome", root.GetProperty("t").GetString());
        Assert.Equal("pc-guid", root.GetProperty("id").GetString());
        Assert.Equal(1, root.GetProperty("v").GetInt32());
        Assert.Equal("currentTrack", root.GetProperty("caps")[0].GetString());
        Assert.Equal(2, root.GetProperty("vlc").GetProperty("level").GetInt32());
        Assert.Equal("running", root.GetProperty("vlc").GetProperty("status").GetString());
    }

    [Fact]
    public void VlcStatus_NoControl_InStateAndInfo()
    {
        var vlc = new VlcInfo { Running = false, Status = VlcStatusNames.NoControl, Version = "3.0.23" };
        var state = ProtocolJson.Serialize(new StateMessage { Vlc = vlc });
        Assert.Contains("\"vlc\":{\"running\":false,\"status\":\"noControl\",\"version\":\"3.0.23\"", state);

        using var info = JsonDocument.Parse(ProtocolJson.SerializeInfo(new InfoResponse { Name = "HTPC", Vlc = vlc }));
        Assert.Equal("noControl", info.RootElement.GetProperty("vlc").GetProperty("status").GetString());

        // An old companion without status — the field is simply absent.
        var old = ProtocolJson.Serialize(new StateMessage { Vlc = new VlcInfo { Running = true } });
        Assert.DoesNotContain("status", old);
        Assert.Equal(
            new[] { "notInstalled", "notRunning", "starting", "noControl", "running" },
            new[] { VlcStatusNames.NotInstalled, VlcStatusNames.NotRunning, VlcStatusNames.Starting, VlcStatusNames.NoControl, VlcStatusNames.Running });
    }

    [Fact]
    public void Err_And_Ok_And_Pong()
    {
        Assert.Equal("""{"t":"err","id":"3","code":"unauthorized","msg":"pairing required"}""",
            ProtocolJson.Serialize(new ErrMessage { Id = "3", Code = ErrorCodes.Unauthorized, Msg = "pairing required" }));
        Assert.Equal("""{"t":"err","code":"pairing_required","msg":"pairing required"}""",
            ProtocolJson.Serialize(new ErrMessage { Code = ErrorCodes.PairingRequired, Msg = "pairing required" }));
        Assert.Equal("""{"t":"ok","id":"4"}""", ProtocolJson.Serialize(new OkMessage { Id = "4" }));
        Assert.Equal("""{"t":"pong"}""", ProtocolJson.Serialize(new PongMessage()));
        Assert.IsType<PingMessage>(ProtocolJson.Deserialize("""{"t":"ping"}"""));
    }

    [Fact]
    public void Beacon_Shape()
    {
        var json = ProtocolJson.Serialize(new BeaconMessage { Id = "g", Name = "PC", WsPort = 47555, NeedsPairing = true });
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("beacon", doc.RootElement.GetProperty("t").GetString());
        Assert.Equal(47555, doc.RootElement.GetProperty("wsPort").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("protocol").GetInt32());
        Assert.True(doc.RootElement.GetProperty("needsPairing").GetBoolean());
        Assert.False(doc.RootElement.TryGetProperty("bt", out _)); // no adapter / radio off — no field
    }

    [Fact]
    public void Bt_Field_InInfoBeaconWelcome()
    {
        const string addr = "02:1A:2B:3C:4D:60";
        using var info = JsonDocument.Parse(ProtocolJson.SerializeInfo(new InfoResponse { Name = "PC", Bt = addr }));
        Assert.Equal(addr, info.RootElement.GetProperty("bt").GetString());
        Assert.False(JsonDocument.Parse(ProtocolJson.SerializeInfo(new InfoResponse { Name = "PC" })).RootElement.TryGetProperty("bt", out _));

        var beacon = Assert.IsType<BeaconMessage>(ProtocolJson.Deserialize(ProtocolJson.Serialize(new BeaconMessage { Id = "g", Name = "PC", Bt = addr })));
        Assert.Equal(addr, beacon.Bt);

        var welcome = ProtocolJson.Serialize(new WelcomeMessage { Id = "g", Name = "PC", Caps = [Caps.Bluetooth], Bt = addr });
        Assert.Contains("\"bt\":\"02:1A:2B:3C:4D:60\"", welcome);
        Assert.Contains("\"caps\":[\"bluetooth\"]", welcome);
    }

    [Fact]
    public void Entries_Playlist_Tracks_Displays_RoundTrip()
    {
        var entries = new EntriesMessage { Id = "3", Uri = "file:///", Items = [new BrowseEntry { Name = "D:\\", Uri = "file:///D:/", Type = "dir" }] };
        var back = Assert.IsType<EntriesMessage>(ProtocolJson.Deserialize(ProtocolJson.Serialize(entries)));
        Assert.Equal("file:///D:/", back.Items[0].Uri);

        var tracks = TracksMessage.From(new TracksInfo { Audio = [new TrackInfo { Id = 1, Name = "A" }], CurrentAudio = 1 }, "6");
        var tj = ProtocolJson.Serialize(tracks);
        Assert.Contains("\"currentAudio\":1", tj);
        Assert.Contains("\"id\":\"6\"", tj);

        var displays = new DisplaysMessage { Items = [new DisplayEntry { Index = 1, Name = "TV", Bounds = new DisplayBounds { X = 1920, Width = 3840, Height = 2160 } }] };
        var dj = ProtocolJson.Serialize(displays);
        Assert.Contains("\"bounds\":{\"x\":1920", dj);
    }

    [Fact]
    public void Cyrillic_IsNotEscaped()
    {
        var json = ProtocolJson.Serialize(new EventMessage { Name = "paired", Device = "Телефон Алексея" });
        Assert.Contains("\"device\":\"Телефон Алексея\"", json);
        Assert.DoesNotContain("\\u04", json);
        var back = Assert.IsType<EventMessage>(ProtocolJson.Deserialize(json));
        Assert.Equal("Телефон Алексея", back.Device);
    }

    [Fact]
    public void Info_Shape()
    {
        var json = ProtocolJson.SerializeInfo(new InfoResponse { Name = "PC", Version = "0.2.0", WsPort = 47555, NeedsPairing = true, PcId = "g" });
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("PC", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("protocol").GetInt32());
        Assert.Equal("g", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal(47555, doc.RootElement.GetProperty("wsPort").GetInt32());
    }
}
