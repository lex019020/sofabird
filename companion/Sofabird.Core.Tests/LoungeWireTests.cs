using Sofabird.Core.YouTube.Cast;
using Xunit;

namespace Sofabird.Core.Tests;

/// <summary>The lounge channel's wire format, on responses captured from YouTube (2026-11-23; ids replaced).</summary>
public class LoungeWireTests
{
    /// <summary>The answer to a session's first request: its ids, then who is in the lounge.</summary>
    private const string InitResponse = """
        412
        [[0,["c","3398EF34D60B2F30","",8]]
        ,[1,["S","O1JnKMYTyONkpXy2hS-mx0KwGFmMZ4qc"]]
        ,[2,["loungeStatus",{"queueId":"RQ8mF8RTqSTyOiITL6is1iu0drLbw","devices":"[{\"app\":\"sofabird\",\"name\":\"TV PC\",\"type\":\"LOUNGE_SCREEN\",\"id\":\"screen-1\"},{\"app\":\"android-phone-21.40.163\",\"name\":\"Pixel\",\"type\":\"REMOTE_CONTROL\",\"id\":\"phone-1\"}]"}]]
        ]

        """;

    /// <summary>What a long poll streams when a phone connects and casts (an argument holds brackets in a string).</summary>
    private const string PollStream = """
        25
        [[8,["getNowPlaying"]]
        ]
        77
        [[9,["getSubtitlesTrack"]]
        ,[10,["getVolume"]]
        ,[11,["getPartyGamesMode"]]
        ]
        331
        [[12,["setPlaylist",{"listId":"RQ8mF8RTqSTyOiITL6is1iu0drLbw","currentTime":"12","eventDetails":"{\"eventType\":\"PLAYLIST_SET\",\"videoIds\":[\"aqz-KE-bpKQ\",\"jNQXAC9IVRw\"]}","sourceDeviceType":"REMOTE_CONTROL","audioOnly":"false","videoId":"aqz-KE-bpKQ","params":"","videoIds":"aqz-KE-bpKQ,jNQXAC9IVRw","currentIndex":"0"}]]
        ]
        16
        [[13,["noop"]]
        ]

        """;

    [Fact]
    public void Reads_the_session_ids_and_the_lounge()
    {
        var messages = LoungeChunkReader.ParseAll(InitResponse);
        Assert.Equal(["c", "S", "loungeStatus"], messages.Select(m => m.Name));
        Assert.Equal(2, messages[2].Aid);
        var channel = new LoungeChannel(new LoungeScreen("device-1", "TV PC"), new Random(1));
        foreach (var m in messages) channel.Observe(m);
        Assert.True(channel.Bound);
        Assert.Equal("3398EF34D60B2F30", channel.Sid);
        Assert.Equal("O1JnKMYTyONkpXy2hS-mx0KwGFmMZ4qc", channel.Gsessionid);
        Assert.Equal(2, channel.LastAid);
        Assert.Equal(["phone-1"], CastSession.RemoteIds(messages[2].Get("devices")));
    }

    [Fact]
    public void Reads_a_stream_split_anywhere()
    {
        var whole = LoungeChunkReader.ParseAll(PollStream);
        Assert.Equal(["getNowPlaying", "getSubtitlesTrack", "getVolume", "getPartyGamesMode", "setPlaylist", "noop"], whole.Select(m => m.Name));
        foreach (var size in new[] { 1, 3, 7, 50 })
        {
            var reader = new LoungeChunkReader();
            var names = new List<string>();
            for (var i = 0; i < PollStream.Length; i += size)
                names.AddRange(reader.Feed(PollStream.Substring(i, Math.Min(size, PollStream.Length - i))).Select(m => m.Name));
            Assert.Equal(whole.Select(m => m.Name), names);
        }
    }

    [Fact]
    public void Gives_the_fields_of_a_command()
    {
        var set = LoungeChunkReader.ParseAll(PollStream).Single(m => m.Name == "setPlaylist");
        Assert.Equal(12, set.Aid);
        Assert.Equal("aqz-KE-bpKQ", set.Get("videoId"));
        Assert.Equal(12, set.GetNumber("currentTime"));
        Assert.Equal("aqz-KE-bpKQ,jNQXAC9IVRw", set.Get("videoIds"));
        Assert.Null(set.Get("missing"));
        Assert.Null(set.GetNumber("listId"));
    }

    [Fact]
    public void Keeps_text_beyond_ascii_and_ignores_what_is_not_a_message_list()
    {
        var messages = new LoungeChunkReader().Feed("7\n[1,7,0]50\n[[3,[\"remoteConnected\",{\"name\":\"Пиксель «7» ]\",\"id\":\"p\"}]]\n]\n");
        var connected = Assert.Single(messages);
        Assert.Equal("Пиксель «7» ]", connected.Get("name"));
    }

    [Fact]
    public void Builds_the_requests_of_a_session()
    {
        var channel = new LoungeChannel(new LoungeScreen("device-1", "TV PC"), new Random(1));
        var rid = channel.Rid;
        var init = channel.InitUrl("token-1");
        Assert.StartsWith(LoungeChannel.BindUrl + "?device=LOUNGE_SCREEN&id=device-1&", init);
        Assert.Contains("&name=TV%20PC&app=sofabird&theme=cl&", init);
        Assert.Contains("&loungeIdToken=token-1&VER=8&v=2&", init);
        Assert.Contains($"&RID={rid}&CVER=1", init);
        Assert.Contains("&deviceInfo=%7B%22brand%22%3A%22Sofabird%22", init);

        foreach (var m in LoungeChunkReader.ParseAll(InitResponse)) channel.Observe(m);
        var poll = channel.PollUrl("token-1");
        Assert.Contains("&RID=rpc&SID=3398EF34D60B2F30&CI=0&AID=2&gsessionid=O1JnKMYTyONkpXy2hS-mx0KwGFmMZ4qc&TYPE=xmlhttp", poll);
        var send = channel.SendUrl("token-1");
        Assert.Contains($"&SID=3398EF34D60B2F30&RID={rid + 1}&AID=2&gsessionid=", send);
    }

    [Fact]
    public void Numbers_the_messages_it_sends()
    {
        var channel = new LoungeChannel(new LoungeScreen("device-1", "TV PC"));
        var first = channel.Form([new LoungeCommand("onVolumeChanged", [new("volume", "40"), new("muted", "false")])]);
        Assert.Equal(
            ["count=1", "ofs=0", "req0__sc=onVolumeChanged", "req0_volume=40", "req0_muted=false"],
            first.Select(p => $"{p.Key}={p.Value}"));
        var second = channel.Form([new LoungeCommand("nowPlaying"), new LoungeCommand("onStateChange", [new("state", "1")])]);
        Assert.Equal(
            ["count=2", "ofs=1", "req0__sc=nowPlaying", "req1__sc=onStateChange", "req1_state=1"],
            second.Select(p => $"{p.Key}={p.Value}"));
        channel.Reset();
        Assert.Equal("0", channel.Form([new LoungeCommand("nowPlaying")]).Single(p => p.Key == "ofs").Value);
    }
}
