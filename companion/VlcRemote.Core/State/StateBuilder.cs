using System.Text.RegularExpressions;
using VlcRemote.Core.Protocol;
using VlcRemote.Core.Vlc;

namespace VlcRemote.Core.State;

/// <summary>Current tracks obtained from RC (<c>atrack</c>/<c>strack</c>/<c>vtrack</c> with <c>*</c>).</summary>
public sealed record RcTrackSelection(int? Audio, int? Subtitle, int? Video);

/// <summary>Input data for building a <see cref="StateMessage"/>.</summary>
public sealed class StateInputs
{
    public required VlcInfo Vlc { get; init; }
    public VlcStatus? Status { get; init; }
    public VlcTracksPage? TracksPage { get; init; }
    public IReadOnlyList<VlcPlaylistItem>? Playlist { get; init; }
    public RcTrackSelection? RcTracks { get; init; }
    public SystemInfo? System { get; init; }
    /// <summary>Base URL of the companion for <c>artUrl</c>, for example <c>http://192.168.1.20:47555</c>.</summary>
    public string? ArtBaseUrl { get; init; }
    public bool Muted { get; init; }
    /// <summary>Pending resume question (<c>state.resume</c>).</summary>
    public ResumeInfo? Resume { get; init; }
    /// <summary>Offered next episode (<c>state.next</c>).</summary>
    public NextInfo? Next { get; init; }
    /// <summary>Watch history revision (<c>state.history.revision</c>); <c>null</c> — no history.</summary>
    public long? HistoryRevision { get; init; }
    public long NowUnixMs { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>
/// Builds the full protocol <c>state</c> object from the HTTP status, the <c>tracks.json</c> page
/// and the RC data. No I/O — a pure function, easy to test.
/// </summary>
public static partial class StateBuilder
{
    [GeneratedRegex(@"\s*@\s*\d+(\.\d+)?\s*kb(p)?s\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex BitrateSuffix();

    [GeneratedRegex(@"\(([A-Za-z0-9 ]{4})\)\s*$")]
    private static partial Regex FourCc();

    public static StateMessage Build(StateInputs input)
    {
        var state = new StateMessage
        {
            Vlc = input.Vlc.Clone(),
            System = input.System,
            Resume = input.Resume,
            Next = input.Next,
            History = input.HistoryRevision is long rev ? new HistoryInfo { Revision = rev } : null,
        };

        var st = input.Status;
        if (st is null)
        {
            state.Playback = new PlaybackInfo { State = "stopped", UpdatedAt = input.NowUnixMs };
            state.Volume = new VolumeInfo { Value = 0, Muted = input.Muted };
            return state;
        }

        state.Playback = new PlaybackInfo
        {
            State = NormalizeState(st.State),
            Time = st.Time,
            Length = st.Length,
            Position = st.Position,
            Rate = st.Rate,
            UpdatedAt = input.NowUnixMs,
        };
        state.Volume = new VolumeInfo { Value = st.Volume, Muted = input.Muted || st.Volume == 0 && input.Muted };
        state.Video = new VideoInfo { Fullscreen = st.Fullscreen, Aspect = string.IsNullOrEmpty(st.AspectRatio) ? "default" : st.AspectRatio };
        state.Delays = new DelaysInfo { Audio = st.AudioDelay, Subtitle = st.SubtitleDelay };
        state.Flags = new FlagsInfo { Random = st.Random, Loop = st.Loop, Repeat = st.Repeat };

        if (st.CurrentPlId >= 0 || !st.IsStopped)
            state.Item = BuildItem(st, input);

        if (st.Information is { } info)
        {
            state.Tracks = BuildTracks(info, input.TracksPage, input.RcTracks);
            state.Chapters = BuildChapters(info, input.TracksPage);
            state.Titles = new TitlesInfo { Index = info.Title, Count = info.TitleCount };
        }
        else if (input.TracksPage is { } page && (page.Audio?.Items.Count > 0 || page.Chapter?.Items.Count > 0))
        {
            // information is missing (for example, right after the start), but the page already answers.
            state.Tracks = BuildTracksFromPage(page, input.RcTracks);
            state.Chapters = BuildChapters(null, page);
        }

        return state;
    }

    private static string NormalizeState(string s) => s switch
    {
        "playing" or "paused" or "stopped" => s,
        _ => "stopped",
    };

    private static ItemInfo BuildItem(VlcStatus st, StateInputs input)
    {
        var item = new ItemInfo { PlId = st.CurrentPlId };
        var pl = input.Playlist?.FirstOrDefault(p => p.PlId == st.CurrentPlId)
                 ?? input.Playlist?.FirstOrDefault(p => p.Current);
        if (pl is not null)
        {
            item.Uri = pl.Uri;
            item.Title = pl.Name;
        }
        if (st.Information?.Meta is { } meta)
        {
            if (string.IsNullOrEmpty(item.Title) && meta.TryGetValue("title", out var t) && !string.IsNullOrWhiteSpace(t))
                item.Title = t;
            if (string.IsNullOrEmpty(item.Title) && meta.TryGetValue("filename", out var f) && !string.IsNullOrWhiteSpace(f))
                item.Title = f;
            if (meta.TryGetValue("artwork_url", out var art) && !string.IsNullOrWhiteSpace(art) && input.ArtBaseUrl is { } baseUrl)
                item.ArtUrl = $"{baseUrl.TrimEnd('/')}/art?plId={st.CurrentPlId}";
        }
        return item;
    }

    private static TracksInfo BuildTracks(VlcInformation info, VlcTracksPage? page, RcTrackSelection? rc)
    {
        var audio = new List<TrackInfo>();
        var subtitle = new List<TrackInfo>();
        var video = new List<TrackInfo>();
        foreach (var s in info.Streams)
        {
            var track = new TrackInfo
            {
                Id = s.Id,
                Name = TrackName(s),
                Lang = string.IsNullOrWhiteSpace(s.Language) ? null : s.Language,
                Codec = ShortCodec(s.Codec),
            };
            if (s.IsAudio) audio.Add(track);
            else if (s.IsSubtitle) subtitle.Add(track);
            else if (s.IsVideo) video.Add(track);
        }

        // If HTTP for some reason gave no streams but the page exists, take the names from there.
        if (audio.Count == 0 && subtitle.Count == 0 && video.Count == 0 && page is not null)
            return BuildTracksFromPage(page, rc);

        var tracks = new TracksInfo { Audio = audio, Subtitle = subtitle, Video = video };
        ApplyCurrent(tracks, page, rc);
        return tracks;
    }

    private static TracksInfo BuildTracksFromPage(VlcTracksPage page, RcTrackSelection? rc)
    {
        var tracks = new TracksInfo
        {
            Audio = FromVarList(page.Audio),
            Subtitle = FromVarList(page.Spu),
            Video = FromVarList(page.Video),
        };
        ApplyCurrent(tracks, page, rc);
        return tracks;
    }

    private static void ApplyCurrent(TracksInfo tracks, VlcTracksPage? page, RcTrackSelection? rc)
    {
        tracks.CurrentAudio = page?.Audio?.CurrentInt ?? rc?.Audio;
        tracks.CurrentSubtitle = page?.Spu?.CurrentInt ?? rc?.Subtitle;
        tracks.CurrentVideo = page?.Video?.CurrentInt ?? rc?.Video;
    }

    private static List<TrackInfo> FromVarList(VlcVarList? list)
    {
        if (list is null) return [];
        var result = new List<TrackInfo>();
        foreach (var it in list.Items)
        {
            if (it.IntId is not int id || id < 0) continue; // "-1 - Disable" is not a track
            var name = it.Name;
            string? lang = null;
            var idx = name.LastIndexOf(" - [", StringComparison.Ordinal);
            if (idx >= 0 && name.EndsWith(']'))
            {
                lang = name[(idx + 4)..^1];
                name = name[..idx];
            }
            result.Add(new TrackInfo { Id = id, Name = BitrateSuffix().Replace(name, "").Trim(), Lang = lang });
        }
        return result;
    }

    private static ChaptersInfo BuildChapters(VlcInformation? info, VlcTracksPage? page)
    {
        var chapters = new ChaptersInfo
        {
            Index = info?.Chapter ?? page?.Chapter?.CurrentInt ?? -1,
            Count = info?.ChapterCount ?? page?.Chapter?.Items.Count ?? 0,
        };
        if (page?.Chapter is { Items.Count: > 0 } ch)
        {
            var names = ch.Items.OrderBy(i => i.IntId ?? int.MaxValue).Select(i => i.Name.Trim()).ToList();
            chapters.Names = names;
            if (chapters.Count == 0) chapters.Count = names.Count;
            if (chapters.Index < 0 && ch.CurrentInt is int cur) chapters.Index = cur;
        }
        return chapters;
    }

    internal static string TrackName(VlcStreamInfo s)
    {
        var name = s.Description;
        if (string.IsNullOrWhiteSpace(name))
            name = s.Codec is { Length: > 0 } c ? c : $"Track {s.Id}";
        name = BitrateSuffix().Replace(name, "").Trim();
        return name;
    }

    /// <summary>"A52 Audio (aka AC3) (a52 )" → "a52"; "MPEG-H Part2/HEVC (H.265) (hevc)" → "hevc".</summary>
    internal static string? ShortCodec(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec)) return null;
        var m = FourCc().Match(codec);
        if (m.Success) return m.Groups[1].Value.Trim();
        return codec.Trim();
    }
}
