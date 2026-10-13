using Microsoft.Extensions.Time.Testing;
using VlcRemote.Core.History;
using VlcRemote.Core.Protocol;
using Xunit;

namespace VlcRemote.Core.Tests;

/// <summary>In-memory <see cref="IHistoryFileSystem"/>: files by path, media sizes and folder listings.</summary>
internal sealed class FakeHistoryFileSystem : IHistoryFileSystem
{
    public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, long> Sizes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int Writes { get; private set; }

    public string? ReadAllText(string path) => Files.TryGetValue(path, out var t) ? t : null;

    public void WriteAllTextAtomic(string path, string content)
    {
        Files[path] = content;
        Writes++;
    }

    public void Move(string from, string to)
    {
        Files[to] = Files[from];
        Files.Remove(from);
    }

    public long? TryGetFileSize(string path) => Sizes.TryGetValue(path, out var s) ? s : null;

    public IReadOnlyList<string>? TryListFiles(string directory) =>
        Folders.TryGetValue(directory.TrimEnd('\\'), out var f) ? f : null;
}

public sealed class MediaUriTests
{
    [Theory]
    [InlineData("file:///D:/shared/The%20Movie.mkv", "file:///d:/shared/The%20Movie.mkv")]
    [InlineData("file:///d:/shared/The Movie.mkv", "file:///d:/shared/The%20Movie.mkv")]
    [InlineData("FILE:///D:/Shared/a%2bb.mkv", "file:///d:/Shared/a+b.mkv")]
    [InlineData("file:///D:/%D0%A4%D0%B8%D0%BB%D1%8C%D0%BC%D1%8B/x.mkv", "file:///d:/%D0%A4%D0%B8%D0%BB%D1%8C%D0%BC%D1%8B/x.mkv")]
    [InlineData("file:///D:/Фильмы/x.mkv", "file:///d:/%D0%A4%D0%B8%D0%BB%D1%8C%D0%BC%D1%8B/x.mkv")]
    [InlineData("file:///D:/a%5bb%5d%20%23%25.mkv", "file:///d:/a%5Bb%5D%20%23%25.mkv")]
    [InlineData("file://NAS/Video/x.mkv", "file://nas/Video/x.mkv")]
    [InlineData("file://localhost/C:/x.mkv", "file:///c:/x.mkv")]
    [InlineData("file:/C:/x.mkv", "file:///c:/x.mkv")]
    public void Normalizes_spellings_of_the_same_file(string input, string expected)
    {
        Assert.Equal(expected, MediaUri.Normalize(input));
    }

    [Theory]
    [InlineData("http://example.com/x.mkv")]
    [InlineData("dvd:///D:/")]
    [InlineData("file:///relative.mkv")]
    [InlineData("")]
    [InlineData(null)]
    public void Ignores_non_file_uris(string? input) => Assert.Null(MediaUri.Normalize(input));

    [Fact]
    public void Round_trips_with_the_local_file_browser()
    {
        var path = @"D:\Сериалы\Show (2020)\S01E02 #1 50%.mkv";
        var uri = Vlc.LocalFileBrowser.ToUri(path, isDir: false);
        Assert.Equal(path, MediaUri.ToLocalPath(uri));
        Assert.Equal(path, MediaUri.ToLocalPath(MediaUri.Normalize(uri)));
        Assert.Equal("S01E02 #1 50%.mkv", MediaUri.FileName(uri));
        Assert.Equal("file:///d:/%D0%A1%D0%B5%D1%80%D0%B8%D0%B0%D0%BB%D1%8B/Show%20(2020)/", MediaUri.FolderOf(uri));
        Assert.True(MediaUri.Same(uri, "file:///d:/Сериалы/show (2020)/s01e02 %231 50%25.MKV"));
        Assert.Equal(MediaUri.Normalize(uri), MediaUri.Combine(MediaUri.FolderOf(uri)!, "S01E02 #1 50%.mkv"));
        Assert.Equal(@"\\nas\Video\x.mkv", MediaUri.ToLocalPath("file://nas/Video/x.mkv"));
    }
}

public sealed class HistoryRulesTests
{
    [Theory]
    [InlineData(950, 1000, false)] // exactly 5% left: not yet
    [InlineData(951, 1000, true)]
    [InlineData(1000, 1000, true)]
    [InlineData(0, 0, false)] // unknown length
    [InlineData(5699, 6000, false)] // 301 s left of 6000 (5% = 300)
    [InlineData(5701, 6000, true)]
    public void Finished_when_less_than_five_percent_is_left(long position, long length, bool finished) =>
        Assert.Equal(finished, HistoryRules.IsFinishedAt(position, length));

    [Theory]
    [InlineData(299, false, false)]
    [InlineData(300, false, true)]
    [InlineData(4000, false, true)]
    [InlineData(4000, true, false)]
    public void Resume_is_offered_from_five_minutes(long position, bool finished, bool offered) =>
        Assert.Equal(offered, HistoryRules.ShouldOfferResume(new HistoryEntry { Position = position, Length = 6000, Finished = finished }));

    [Fact]
    public void Natural_sort_orders_episodes_by_number()
    {
        string[] files = ["Show E10.mkv", "show e2.mkv", "Show E1.mkv", "Show E02b.mkv", "Show E100.mkv"];
        Assert.Equal(["Show E1.mkv", "show e2.mkv", "Show E02b.mkv", "Show E10.mkv", "Show E100.mkv"],
            files.OrderBy(f => f, NaturalStringComparer.Instance));
    }

    [Fact]
    public void Next_episode_skips_finished_and_non_video_files()
    {
        string[] files = ["E1.mkv", "E1.srt", "E2.mkv", "E3.mp4", "cover.jpg", "E10.avi"];
        Assert.Equal("E2.mkv", HistoryRules.FindNextEpisode("E1.mkv", files, _ => false));
        Assert.Equal("E3.mp4", HistoryRules.FindNextEpisode("e1.MKV", files, f => f == "E2.mkv"));
        Assert.Equal("E10.avi", HistoryRules.FindNextEpisode("E3.mp4", files, _ => false));
        Assert.Null(HistoryRules.FindNextEpisode("E10.avi", files, _ => false));
        Assert.Null(HistoryRules.FindNextEpisode("missing.mkv", files, _ => false));
    }

    private static readonly IReadOnlyList<TrackInfo> Audio =
    [
        new() { Id = 1, Name = "Original", Lang = "English", Codec = "a52" },
        new() { Id = 2, Name = "Dub Studio A", Lang = "Russian", Codec = "a52" },
        new() { Id = 3, Name = "MVO Studio B", Lang = "Russian", Codec = "aac" },
        new() { Id = 4, Name = "Commentary", Lang = "English", Codec = "aac" },
    ];

    [Fact]
    public void Track_matching_prefers_language_then_name_then_codec()
    {
        Assert.Equal(3, HistoryRules.MatchTrack(new TrackPreference { Lang = "Russian", Name = "MVO Studio B", Codec = "aac" }, Audio));
        Assert.Equal(2, HistoryRules.MatchTrack(new TrackPreference { Lang = "russian", Name = "Unknown dub", Codec = "a52" }, Audio));
        Assert.Equal(3, HistoryRules.MatchTrack(new TrackPreference { Lang = "Russian", Name = "Other", Codec = "aac" }, Audio));
        // The name alone (language not tagged in the new file).
        Assert.Equal(4, HistoryRules.MatchTrack(new TrackPreference { Lang = "German", Name = "Commentary" }, Audio));
        // A codec alone is not a reason to switch.
        Assert.Null(HistoryRules.MatchTrack(new TrackPreference { Lang = "German", Name = "X", Codec = "aac" }, Audio));
        Assert.Equal(3, HistoryRules.MatchTrack(new TrackPreference { Codec = "aac" }, [Audio[2], Audio[3]]));
    }

    [Fact]
    public void Plan_applies_folder_choices_and_keeps_subtitles_off()
    {
        var tracks = new TracksInfo
        {
            Audio = Audio,
            Subtitle = [new() { Id = 7, Name = "Forced", Lang = "Russian" }, new() { Id = 8, Name = "Full", Lang = "Russian" }],
            CurrentAudio = 1,
            CurrentSubtitle = 7,
        };
        var prefs = new FolderPreferences
        {
            Audio = new TrackPreference { Lang = "Russian", Name = "Dub Studio A" },
            Subtitle = SubtitleChoice.Disabled,
        };
        Assert.Equal([("audio", 2), ("subtitle", -1)], HistoryRules.PlanTrackSelection(prefs, tracks));

        prefs.Subtitle = SubtitleChoice.Of(new TrackPreference { Lang = "Russian", Name = "Full" });
        Assert.Equal([("audio", 2), ("subtitle", 8)], HistoryRules.PlanTrackSelection(prefs, tracks));

        tracks.CurrentAudio = 2;
        tracks.CurrentSubtitle = 8;
        Assert.Empty(HistoryRules.PlanTrackSelection(prefs, tracks)); // already selected
        Assert.Empty(HistoryRules.PlanTrackSelection(null, tracks));
    }
}

public sealed class HistoryStoreTests
{
    private const string FilePath = @"C:\data\history.json";
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeHistoryFileSystem _fs = new();

    private HistoryStore Create() => new(FilePath, _clock, _fs);

    [Fact]
    public void Persists_entries_and_folder_choices_round_trip()
    {
        _fs.Sizes[@"D:\Сериалы\Show\E1.mkv"] = 1000;
        using (var store = Create())
        {
            var start = store.BeginPlayback("file:///D:/Сериалы/Show/E1.mkv", "Show — E1", 2400, 1000)!;
            Assert.Null(start.Before);
            Assert.Equal(1, start.After.PlayCount);
            store.UpdatePosition("file:///d:/%D0%A1%D0%B5%D1%80%D0%B8%D0%B0%D0%BB%D1%8B/Show/E1.mkv", 700, 2400, allowUnfinish: false, notify: true);
            store.SetAudioPreference("file:///D:/Сериалы/Show/", new TrackPreference { Lang = "Russian", Name = "Dub", Codec = "a52" });
            store.SetSubtitlePreference("file:///D:/Сериалы/Show/", SubtitleChoice.Disabled);
            Assert.Equal(0, _fs.Writes); // debounced
        }
        Assert.Equal(1, _fs.Writes); // flushed on dispose
        var json = _fs.Files[FilePath];
        Assert.Contains("\"version\":1", json);
        Assert.Contains("Show — E1", json); // readable, not \u-escaped
        Assert.Contains("\"subtitle\":\"off\"", json);

        using var again = Create();
        var e = again.Find("file:///D:/Сериалы/Show/E1.mkv")!;
        Assert.Equal(700, e.Position);
        Assert.Equal(2400, e.Length);
        Assert.Equal("E1.mkv", e.Name);
        Assert.Equal(1000, e.Size);
        Assert.Equal("Show — E1", e.Title);
        Assert.Equal(_clock.GetUtcNow().ToUnixTimeMilliseconds(), e.LastWatched);
        var prefs = again.GetFolderPreferences("file:///d:/Сериалы/Show/")!;
        Assert.Equal("Dub", prefs.Audio!.Name);
        Assert.True(prefs.Subtitle!.Off);
        Assert.True(again.Revision >= 2);
    }

    [Fact]
    public void Writes_are_debounced_for_two_seconds()
    {
        using var store = Create();
        store.BeginPlayback("file:///D:/m.mkv", "M", 6000, null);
        store.UpdatePosition("file:///D:/m.mkv", 10, 6000, false, notify: false);
        _clock.Advance(TimeSpan.FromSeconds(1.9));
        Assert.Equal(0, _fs.Writes);
        _clock.Advance(TimeSpan.FromSeconds(0.2));
        Assert.Equal(1, _fs.Writes);
        store.UpdatePosition("file:///D:/m.mkv", 20, 6000, false, notify: false);
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(2, _fs.Writes);
        _clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(2, _fs.Writes); // nothing changed
    }

    [Fact]
    public void Corrupt_file_is_kept_aside_and_history_starts_empty()
    {
        _fs.Files[FilePath] = "{\"version\":1,\"entries\":[{\"uri\":";
        using var store = Create();
        Assert.Equal(0, store.Count);
        Assert.False(_fs.Files.ContainsKey(FilePath));
        var aside = Assert.Single(_fs.Files.Keys, k => k.StartsWith(FilePath + ".corrupt-", StringComparison.Ordinal));
        Assert.StartsWith("{\"version\"", _fs.Files[aside]);

        store.BeginPlayback("file:///D:/m.mkv", "M", 100, null);
        store.Flush();
        Assert.Contains("m.mkv", _fs.Files[FilePath]);
    }

    [Fact]
    public void Retention_purges_entries_older_than_183_days()
    {
        using (var store = Create())
        {
            store.BeginPlayback("file:///D:/old/a.mkv", "A", 100, null);
            store.SetAudioPreference("file:///D:/old/", new TrackPreference { Lang = "English" });
            _clock.Advance(TimeSpan.FromDays(100));
            store.BeginPlayback("file:///D:/new/b.mkv", "B", 100, null);
            store.SetAudioPreference("file:///D:/new/", new TrackPreference { Lang = "Russian" });
            _clock.Advance(TimeSpan.FromDays(83));
            Assert.Equal(0, store.Purge()); // exactly 183 days: kept
            store.StartMaintenance();
            _clock.Advance(TimeSpan.FromDays(1)); // the daily purge
            Assert.Null(store.Find("file:///D:/old/a.mkv"));
            Assert.NotNull(store.Find("file:///D:/new/b.mkv"));
            Assert.Null(store.GetFolderPreferences("file:///D:/old/"));
            Assert.NotNull(store.GetFolderPreferences("file:///D:/new/"));
        }
        _clock.Advance(TimeSpan.FromDays(200));
        using var later = Create(); // purged at startup
        Assert.Equal(0, later.Count);
    }

    [Fact]
    public void Entry_follows_the_file_to_another_path_by_name_and_size()
    {
        using var store = Create();
        store.BeginPlayback("file:///D:/shared/Movie.mkv", "Movie", 7000, 123456);
        store.UpdatePosition("file:///D:/shared/Movie.mkv", 2000, 7000, false, true);

        // Same file through the network share: found by name + size, moved to the new URI.
        Assert.Equal(2000, store.Find("file://htpc/shared/Movie.mkv", 123456)!.Position);
        Assert.Null(store.Find("file://htpc/shared/Movie.mkv", 999));
        var start = store.BeginPlayback("file://htpc/shared/Movie.mkv", "Movie", 7000, 123456)!;
        Assert.Equal(2000, start.Before!.Position);
        Assert.Equal(2, start.After.PlayCount);
        Assert.Equal("file://htpc/shared/Movie.mkv", start.After.Uri);
        Assert.Equal("file://htpc/shared/", start.After.FolderUri);
        Assert.Equal(1, store.Count);
        Assert.Null(store.Find("file:///D:/shared/Movie.mkv"));
    }

    [Fact]
    public void Position_finish_and_rewatch_rules()
    {
        using var store = Create();
        const string uri = "file:///D:/m.mkv";
        store.BeginPlayback(uri, "M", 1000, null);
        Assert.False(store.UpdatePosition(uri, 950, 1000, false, false).BecameFinished); // exactly 5% left
        Assert.Equal(950, store.Find(uri)!.Position);
        var r = store.UpdatePosition(uri, 951, 1000, false, false);
        Assert.True(r.BecameFinished);
        Assert.True(store.Find(uri)!.Finished);
        Assert.Equal(0, store.Find(uri)!.Position);

        // Seeking back in the same playback keeps it finished.
        Assert.False(store.UpdatePosition(uri, 400, 1000, allowUnfinish: false, notify: false).BecameUnfinished);
        Assert.True(store.Find(uri)!.Finished);
        // Watching it again (finished before this playback) past the threshold: in progress again.
        Assert.False(store.UpdatePosition(uri, 200, 1000, allowUnfinish: true, notify: false).BecameUnfinished);
        Assert.True(store.UpdatePosition(uri, 400, 1000, allowUnfinish: true, notify: false).BecameUnfinished);
        Assert.Equal(400, store.Find(uri)!.Position);
    }

    [Fact]
    public void Manual_marks_remove_and_clear()
    {
        using var store = Create();
        var rev = store.Revision;
        var changes = 0;
        store.Changed += () => changes++;
        Assert.True(store.SetFinished("file:///D:/never/played.mkv", true)); // marking an unplayed file adds it
        var e = store.Find("file:///d:/never/played.mkv")!;
        Assert.True(e.Finished);
        Assert.Equal("played", e.Title);
        Assert.False(store.SetFinished("file:///D:/never/played.mkv", true)); // no change

        store.BeginPlayback("file:///D:/m.mkv", "M", 1000, null);
        store.UpdatePosition("file:///D:/m.mkv", 500, 1000, false, false);
        Assert.True(store.SetFinished("file:///D:/m.mkv", false)); // unwatched: position reset
        Assert.Equal(0, store.Find("file:///D:/m.mkv")!.Position);
        Assert.False(store.SetFinished("file:///D:/unknown.mkv", false));

        Assert.True(store.Remove("file:///D:/m.mkv"));
        Assert.False(store.Remove("file:///D:/m.mkv"));
        store.SetAudioPreference("file:///D:/", new TrackPreference { Lang = "English" });
        store.Clear();
        Assert.Equal(0, store.Count);
        Assert.Null(store.GetFolderPreferences("file:///D:/"));
        Assert.True(store.Revision > rev);
        Assert.Equal(5, changes);
        Assert.Throws<ArgumentException>(() => store.SetFinished("http://x/y.mkv", true));
    }

    [Fact]
    public void Browse_entries_get_progress_and_finished()
    {
        using var store = Create();
        store.BeginPlayback("file:///D:/s/E1.mkv", "E1", 1000, 10);
        store.UpdatePosition("file:///D:/s/E1.mkv", 990, 1000, false, false);
        store.BeginPlayback("file:///D:/s/E2.mkv", "E2", 1000, 20);
        store.UpdatePosition("file:///D:/s/E2.mkv", 250, 1000, false, false);
        store.BeginPlayback("file:///D:/old/E3.mkv", "E3", 1000, 30);
        store.UpdatePosition("file:///D:/old/E3.mkv", 500, 1000, false, false);
        var items = new List<BrowseEntry>
        {
            new() { Name = "sub", Uri = "file:///D:/s/sub/", Type = "dir" },
            new() { Name = "E1.mkv", Uri = "file:///D:/s/E1.mkv", Type = "file", Size = 10 },
            new() { Name = "E2.mkv", Uri = "file:///D:/s/E2.mkv", Type = "file", Size = 20 },
            new() { Name = "E3.mkv", Uri = "file:///D:/moved/E3.mkv", Type = "file", Size = 30 }, // by name + size
            new() { Name = "E4.mkv", Uri = "file:///D:/s/E4.mkv", Type = "file", Size = 40 },
        };
        store.Annotate(items);
        Assert.Null(items[0].Progress);
        Assert.Equal(1.0, items[1].Progress);
        Assert.True(items[1].Finished);
        Assert.Equal(0.25, items[2].Progress);
        Assert.False(items[2].Finished);
        Assert.Equal(0.5, items[3].Progress);
        Assert.Null(items[4].Progress);
        Assert.Null(items[4].Finished);
    }

    [Fact]
    public void Continue_watching_lists_unfinished_and_the_next_episode()
    {
        _fs.Folders[@"D:\Show"] = ["Show E01.mkv", "Show E02.mkv", "Show E03.mkv", "Show E10.mkv", "Show E02.srt", "notes.txt"];
        _fs.Folders[@"D:\Done"] = ["Last.mkv"];
        using var store = Create();

        // A movie in progress.
        store.BeginPlayback("file:///D:/Movies/Long.mkv", "Long", 7000, null);
        store.UpdatePosition("file:///D:/Movies/Long.mkv", 3000, 7000, false, true);
        _clock.Advance(TimeSpan.FromHours(1));
        // A movie barely started: not in "continue".
        store.BeginPlayback("file:///D:/Movies/Short.mkv", "Short", 7000, null);
        store.UpdatePosition("file:///D:/Movies/Short.mkv", 120, 7000, false, true);
        _clock.Advance(TimeSpan.FromHours(1));
        // Series: E01 and E02 finished → next is E03.
        foreach (var ep in new[] { "Show%20E01.mkv", "Show%20E02.mkv" })
        {
            store.BeginPlayback("file:///D:/Show/" + ep, null, 1500, null);
            store.UpdatePosition("file:///D:/Show/" + ep, 1490, 1500, false, true);
            _clock.Advance(TimeSpan.FromHours(1));
        }
        // A finished series without more episodes.
        store.BeginPlayback("file:///D:/Done/Last.mkv", null, 1500, null);
        store.UpdatePosition("file:///D:/Done/Last.mkv", 1499, 1500, false, true);

        var (total, items) = store.Query("continue", 50, 0);
        Assert.Equal(2, total);
        var next = items[0];
        Assert.True(next.Next);
        Assert.Equal("file:///d:/Show/Show%20E03.mkv", next.Uri);
        Assert.Equal("Show E03", next.Title);
        Assert.Equal("Show E03.mkv", next.Name);
        Assert.Equal("file:///d:/Show/", next.FolderUri);
        Assert.False(next.Finished);
        Assert.Equal("file:///d:/Movies/Long.mkv", items[1].Uri);
        Assert.Null(items[1].Next);
        Assert.Equal(3000, items[1].Position);
        Assert.Equal(Math.Round(3000.0 / 7000, 4), items[1].Progress);

        // E03 in progress: it is the one to continue, no separate "next" item.
        _clock.Advance(TimeSpan.FromHours(1));
        store.BeginPlayback("file:///D:/Show/Show%20E03.mkv", null, 1500, null);
        store.UpdatePosition("file:///D:/Show/Show%20E03.mkv", 600, 1500, false, true);
        (total, items) = store.Query("continue", 50, 0);
        Assert.Equal(2, total);
        Assert.Equal("file:///d:/Show/Show%20E03.mkv", items[0].Uri);
        Assert.Null(items[0].Next);
        Assert.Equal(600, items[0].Position);

        // "all": everything, newest first, paged.
        (total, items) = store.Query("all", 2, 1);
        Assert.Equal(6, total);
        Assert.Equal(2, items.Count);
        Assert.Equal("file:///d:/Done/Last.mkv", items[0].Uri);
        Assert.True(items[0].Finished);
        Assert.Equal(1.0, items[0].Progress);
    }
}
