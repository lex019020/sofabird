using VlcRemote.Core.Vlc;
using Xunit;

namespace VlcRemote.Core.Tests;

public class VlcJsonParserTests
{
    [Fact]
    public void ParseStatus_Playing_ReadsCoreFieldsAndStreams()
    {
        var s = VlcJsonParser.ParseStatus(Fixtures.Read("status-playing.json"));

        Assert.Equal("playing", s.State);
        Assert.True(s.IsPlaying);
        Assert.Equal(904, s.Time);
        Assert.Equal(10705, s.Length);
        Assert.InRange(s.Position, 0.084, 0.085);
        Assert.Equal(1.0, s.Rate);
        Assert.Equal(0, s.Volume);
        Assert.False(s.Fullscreen);
        Assert.Equal(3, s.CurrentPlId);
        Assert.Equal(3, s.ApiVersion);
        Assert.Equal("3.0.23 Vetinari", s.Version);
        Assert.Equal(10, s.SeekSec);
        Assert.Equal("default", s.AspectRatio);
        Assert.False(s.Random);

        var info = Assert.IsType<VlcInformation>(s.Information);
        Assert.Equal(2, info.Chapter);
        Assert.Equal(40, info.ChapterCount);
        Assert.Equal(0, info.Title);
        Assert.Equal(1, info.TitleCount);
        Assert.Equal("The Lord of the Rings: The Fellowship of the Ring / 2001 / Warner Bros.", info.Meta["title"]);
        Assert.StartsWith("file:///C:/Users/", info.Meta["artwork_url"]);

        // 13 потоков: 1 видео (0), 6 аудио (1..6), 6 субтитров (7..12); ID = N из "Stream N".
        Assert.Equal(13, info.Streams.Count);
        Assert.Equal(Enumerable.Range(0, 13), info.Streams.Select(x => x.Id));
        Assert.Equal(6, info.Streams.Count(x => x.IsAudio));
        Assert.Equal(6, info.Streams.Count(x => x.IsSubtitle));
        Assert.Single(info.Streams, x => x.IsVideo);

        var ru = info.Streams.Single(x => x.Id == 1);
        Assert.Equal("Audio", ru.Type);
        Assert.Equal("Russian", ru.Language);
        Assert.Equal("Dub Мосфильм / AC3 5.1 @ 448 kbps", ru.Description);
        Assert.Equal("A52 Audio (aka AC3) (a52 )", ru.Codec);
    }

    [Fact]
    public void ParseStatus_Stopped_FullscreenZeroAndNoInformation()
    {
        var s = VlcJsonParser.ParseStatus(Fixtures.Read("status-stopped.json"));

        Assert.Equal("stopped", s.State);
        Assert.False(s.Fullscreen); // "fullscreen":0
        Assert.Equal(-1, s.CurrentPlId);
        Assert.Equal(0, s.Time);
        Assert.Equal(0, s.Length);
        Assert.Equal(266, s.Volume);
        Assert.Null(s.Information);
    }

    [Fact]
    public void ParseStatus_ToleratesEqualizerObjectAndLocalizedCategoryKeys()
    {
        const string json = """
            {"state":"paused","time":5,"length":10,"position":0.5,"rate":1.5015,"volume":256,"fullscreen":true,
             "currentplid":7,"apiversion":3,"version":"3.0.23","equalizer":{"preamp":0,"bands":{}},
             "information":{"chapter":0,"chapters":[0],"title":0,"titles":[0],
               "category":{"Поток 0":{"Type":"Video","Codec":"hevc"},
                           "Поток 1":{"Type":"Audio","Language":"Russian","Original_ID":"42"},
                           "meta":{"filename":"x.mkv"}}}}
            """;
        var s = VlcJsonParser.ParseStatus(json);
        Assert.True(s.Fullscreen);
        Assert.Equal("paused", s.State);
        var streams = s.Information!.Streams;
        Assert.Equal(2, streams.Count);
        Assert.Equal(0, streams[0].Id);
        Assert.Equal(42, streams[1].Id); // Original_ID имеет приоритет над N
        Assert.True(streams[1].IsAudio);
        Assert.Equal("x.mkv", s.Information.Meta["filename"]);
    }

    [Fact]
    public void ParsePlaylist_ReturnsLeavesOfPlaylistNodeOnly()
    {
        var items = VlcJsonParser.ParsePlaylist(Fixtures.Read("playlist.json"));

        var item = Assert.Single(items);
        Assert.Equal(3, item.PlId);
        Assert.True(item.Current);
        Assert.Equal(10705, item.Duration);
        Assert.StartsWith("file:///D:/shared/", item.Uri);
        Assert.Contains("Fellowship", item.Name);
    }

    [Fact]
    public void ParseBrowse_SkipsParentEntryAndKeepsTypes()
    {
        const string json = """
            {"element":[
              {"type":"dir","path":"D:\\","name":"..","uri":"file:///D:/","access_time":0,"uid":0,"creation_time":0,"gid":0,"modification_time":0,"mode":16895,"size":0},
              {"type":"dir","path":"D:\\shared\\sub","name":"sub","uri":"file:///D:/shared/sub","modification_time":1700000000,"size":0},
              {"type":"file","path":"D:\\shared\\a.mkv","name":"a.mkv","uri":"file:///D:/shared/a.mkv","modification_time":1700000001,"size":123456789}
            ]}
            """;
        var entries = VlcJsonParser.ParseBrowse(json);
        Assert.Equal(2, entries.Count);
        Assert.Equal("dir", entries[0].Type);
        Assert.Equal("sub", entries[0].Name);
        Assert.Equal("file", entries[1].Type);
        Assert.Equal(123456789, entries[1].Size);
        Assert.Equal(1700000001, entries[1].ModificationTime);
        Assert.Equal("file:///D:/shared/a.mkv", entries[1].Uri);
    }

    [Fact]
    public void ParseTracksPage_ReadsCurrentAndChapterNames()
    {
        var page = VlcJsonParser.ParseTracksPage(Fixtures.Read("tracks-custom-page.json"));

        Assert.Equal(4, page.Audio!.CurrentInt);
        Assert.Equal(7, page.Audio.Items.Count); // включая "-1 - Disable"
        Assert.Equal(-1, page.Audio.Items[0].IntId);
        Assert.Equal(10, page.Spu!.CurrentInt);
        Assert.Equal(0, page.Video!.CurrentInt);
        Assert.Equal(2, page.Chapter!.CurrentInt);
        Assert.Equal(40, page.Chapter.Items.Count);
        Assert.Equal(" The Shire", page.Chapter.Items[1].Name);
        Assert.Equal("", page.Aspect!.Current);
        Assert.Equal("16:9", page.Aspect.Items[1].Id);
        Assert.Null(page.Zoom!.Items[0].IntId); // 0.25 — не целое
        Assert.Equal("0.25", page.Zoom.Items[0].Id);
    }

    [Fact]
    public void ParseTracksPage_NullObjectsWhenNothingPlays()
    {
        var page = VlcJsonParser.ParseTracksPage("""{"vlcremote":1,"audio":null,"spu":null,"chapter":null}""");
        Assert.Equal(1, page.VlcRemote);
        Assert.Null(page.Audio);
        Assert.Null(page.Chapter);
    }

    [Fact]
    public void ParseSetVar_Ok()
    {
        var r = VlcJsonParser.ParseSetVar("""{"ok":true,"current":5}""");
        Assert.True(r.Ok);
        Assert.Equal("5", r.Current);
        var e = VlcJsonParser.ParseSetVar("""{"ok":false,"err":"no such object or nothing is playing"}""");
        Assert.False(e.Ok);
        Assert.Contains("nothing is playing", e.Error);
    }
}
