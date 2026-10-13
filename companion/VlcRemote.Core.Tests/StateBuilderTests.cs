using VlcRemote.Core.Protocol;
using VlcRemote.Core.State;
using VlcRemote.Core.Vlc;
using Xunit;

namespace VlcRemote.Core.Tests;

public class StateBuilderTests
{
    private static VlcInfo Vlc => new() { Running = true, Version = "3.0.23", ApiVersion = 3, Level = 2 };

    [Fact]
    public void Build_PlayingWithTracksPage_ProducesFullState()
    {
        var status = VlcJsonParser.ParseStatus(Fixtures.Read("status-playing.json"));
        var page = VlcJsonParser.ParseTracksPage(Fixtures.Read("tracks-custom-page.json"));
        var playlist = VlcJsonParser.ParsePlaylist(Fixtures.Read("playlist.json"));

        var state = StateBuilder.Build(new StateInputs
        {
            Vlc = Vlc,
            Status = status,
            TracksPage = page,
            Playlist = playlist,
            ArtBaseUrl = "http://192.168.1.20:47555",
            NowUnixMs = 1_760_280_000_123,
            System = new SystemInfo { Volume = 60, Muted = false, KeepAwake = true },
        });

        Assert.Equal("playing", state.Playback.State);
        Assert.Equal(904, state.Playback.Time);
        Assert.Equal(10705, state.Playback.Length);
        Assert.Equal(1_760_280_000_123, state.Playback.UpdatedAt);

        var item = Assert.IsType<ItemInfo>(state.Item);
        Assert.Equal(3, item.PlId);
        Assert.StartsWith("file:///D:/shared/", item.Uri);
        Assert.Contains("Fellowship", item.Title);
        Assert.Equal("http://192.168.1.20:47555/art?plId=3", item.ArtUrl);

        var tracks = Assert.IsType<TracksInfo>(state.Tracks);
        Assert.Equal(6, tracks.Audio.Count);
        Assert.Equal(6, tracks.Subtitle.Count);
        Assert.Single(tracks.Video);
        var ru = tracks.Audio.Single(t => t.Id == 1);
        Assert.Equal("Dub Мосфильм / AC3 5.1", ru.Name); // the bitrate is cut off
        Assert.Equal("Russian", ru.Lang);
        Assert.Equal("a52", ru.Codec);
        Assert.Equal("hevc", tracks.Video[0].Codec);
        Assert.Equal("subt", tracks.Subtitle.Single(t => t.Id == 7).Codec);
        Assert.Equal(4, tracks.CurrentAudio);
        Assert.Equal(10, tracks.CurrentSubtitle);
        Assert.Equal(0, tracks.CurrentVideo);

        var chapters = Assert.IsType<ChaptersInfo>(state.Chapters);
        Assert.Equal(2, chapters.Index);
        Assert.Equal(40, chapters.Count);
        Assert.Equal(40, chapters.Names!.Count);
        Assert.Equal("The Shire", chapters.Names[1]);

        Assert.Equal(0, state.Titles!.Index);
        Assert.Equal(1, state.Titles.Count);
        Assert.Equal(0, state.Volume.Value);
        Assert.False(state.Video.Fullscreen);
        Assert.Equal("default", state.Video.Aspect);
        Assert.Equal(60, state.System!.Volume);

        // Serialized through the source-generated context with the discriminator.
        var json = ProtocolJson.Serialize(state);
        Assert.StartsWith("{\"t\":\"state\"", json);
        Assert.Contains("\"currentSubtitle\":10", json);
        Assert.DoesNotContain("\"id\":", json.Substring(0, 30));
    }

    [Fact]
    public void Build_WithoutTracksPage_UsesRcSelectionAndOmitsChapterNames()
    {
        var status = VlcJsonParser.ParseStatus(Fixtures.Read("status-playing.json"));
        var state = StateBuilder.Build(new StateInputs
        {
            Vlc = Vlc,
            Status = status,
            RcTracks = new RcTrackSelection(2, -1, 0),
        });
        Assert.Equal(2, state.Tracks!.CurrentAudio);
        Assert.Equal(-1, state.Tracks.CurrentSubtitle);
        Assert.Null(state.Chapters!.Names);
        Assert.Equal(40, state.Chapters.Count);
        Assert.Null(state.Item!.ArtUrl); // no base URL
        Assert.Contains("Fellowship", state.Item.Title); // from meta.title
    }

    [Fact]
    public void Build_WithoutCurrentSources_OmitsCurrentFields()
    {
        var status = VlcJsonParser.ParseStatus(Fixtures.Read("status-playing.json"));
        var state = StateBuilder.Build(new StateInputs { Vlc = Vlc, Status = status });
        Assert.Null(state.Tracks!.CurrentAudio);
        var json = ProtocolJson.Serialize(state);
        Assert.DoesNotContain("currentAudio", json);
    }

    [Fact]
    public void Build_Stopped_HasNoItemTracksOrChapters()
    {
        var status = VlcJsonParser.ParseStatus(Fixtures.Read("status-stopped.json"));
        var state = StateBuilder.Build(new StateInputs { Vlc = Vlc, Status = status, Muted = true });
        Assert.Equal("stopped", state.Playback.State);
        Assert.Null(state.Item);
        Assert.Null(state.Tracks);
        Assert.Null(state.Chapters);
        Assert.Equal(266, state.Volume.Value);
        Assert.True(state.Volume.Muted);
        Assert.False(state.Video.Fullscreen);
    }

    [Fact]
    public void Build_NoStatus_VlcNotRunning()
    {
        var state = StateBuilder.Build(new StateInputs { Vlc = new VlcInfo { Running = false } });
        Assert.False(state.Vlc.Running);
        Assert.Equal("stopped", state.Playback.State);
        Assert.Null(state.Item);
    }

    [Theory]
    [InlineData("A52 Audio (aka AC3) (a52 )", "a52")]
    [InlineData("MPEG-H Part2/HEVC (H.265) (hevc)", "hevc")]
    [InlineData("BD PGS subtitles (bdpg)", "bdpg")]
    [InlineData("Weird codec", "Weird codec")]
    [InlineData(null, null)]
    public void ShortCodec(string? input, string? expected) => Assert.Equal(expected, StateBuilder.ShortCodec(input));
}
