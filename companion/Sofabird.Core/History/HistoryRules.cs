using Sofabird.Core.Protocol;

namespace Sofabird.Core.History;

/// <summary>
/// The product rules of the watch history (decided by the user, see protocol/README.md): pure functions, no I/O.
/// </summary>
public static class HistoryRules
{
    /// <summary>Resume is offered once at least this much was watched.</summary>
    public const long ResumeThresholdSeconds = 300;

    /// <summary>History entries not watched for this long are deleted.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(183);

    /// <summary>Video files of a series folder (the next episode is looked up among them).</summary>
    public static readonly IReadOnlySet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".mpg", ".mpeg", ".m2v", ".ts", ".m2ts", ".mts", ".webm",
        ".flv", ".vob", ".ogv", ".ogm", ".3gp", ".divx", ".xvid", ".rm", ".rmvb", ".asf", ".dv", ".f4v",
    };

    public static bool IsVideoFile(string? name) =>
        !string.IsNullOrEmpty(name) && VideoExtensions.Contains(Path.GetExtension(name));

    /// <summary>"Finished" = less than 5% left. Exactly 5% left is not finished yet.</summary>
    public static bool IsFinishedAt(long position, long length) =>
        length > 0 && position >= 0 && (length - position) * 20 < length;

    /// <summary>Ask whether to continue: not finished and at least <see cref="ResumeThresholdSeconds"/> watched.</summary>
    public static bool ShouldOfferResume(HistoryEntry? entry) =>
        entry is not null && !entry.Finished && entry.Position >= ResumeThresholdSeconds
        && (entry.Length <= 0 || !IsFinishedAt(entry.Position, entry.Length));

    /// <summary>0..1 for progress bars; a finished file is 1.</summary>
    public static double Progress(HistoryEntry entry)
    {
        if (entry.Finished) return 1.0;
        if (entry.Length <= 0) return 0.0;
        return Math.Round(Math.Clamp((double)entry.Position / entry.Length, 0.0, 1.0), 4);
    }

    /// <summary>File name without the extension, for items that have never been played.</summary>
    public static string DisplayName(string fileName) => Path.GetFileNameWithoutExtension(fileName) is { Length: > 0 } s ? s : fileName;

    /// <summary>
    /// The next episode after <paramref name="currentName"/>: the first video file after it in natural order that is
    /// not finished. <paramref name="files"/> are the file names of the folder (any order, any type).
    /// </summary>
    public static string? FindNextEpisode(string currentName, IEnumerable<string> files, Func<string, bool> isFinished)
    {
        var videos = files.Where(IsVideoFile).OrderBy(f => f, NaturalStringComparer.Instance).ToList();
        var index = videos.FindIndex(f => string.Equals(f, currentName, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;
        for (var i = index + 1; i < videos.Count; i++)
            if (!isFinished(videos[i])) return videos[i];
        return null;
    }

    /// <summary>
    /// "Continue watching": unfinished entries with at least <see cref="ResumeThresholdSeconds"/> watched, plus for each
    /// folder the next unfinished episode after the most recently finished one (<see cref="HistoryItem.Next"/>);
    /// newest first. <paramref name="listFolder"/> returns the file names of a folder (null — not available).
    /// </summary>
    public static List<HistoryItem> ContinueWatching(IReadOnlyCollection<HistoryEntry> entries, Func<string, IReadOnlyList<string>?> listFolder)
    {
        var result = new List<HistoryItem>();
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            if (e.Finished || e.Position < ResumeThresholdSeconds) continue;
            result.Add(ToItem(e));
            listed.Add(e.Uri);
        }

        var byUri = entries.ToDictionary(e => e.Uri, StringComparer.OrdinalIgnoreCase);
        foreach (var folder in entries.GroupBy(e => e.FolderUri, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(folder.Key)) continue;
            var lastFinished = folder.Where(e => e.Finished && IsVideoFile(e.Name)).MaxBy(e => e.LastWatched);
            if (lastFinished is null) continue;
            // Something in this folder was played after the last finished episode: that one is the current one.
            if (folder.Any(e => !e.Finished && e.LastWatched > lastFinished.LastWatched && e.Position >= ResumeThresholdSeconds)) continue;
            var files = listFolder(folder.Key);
            if (files is null) continue;
            var next = FindNextEpisode(lastFinished.Name, files,
                name => byUri.TryGetValue(MediaUri.Combine(folder.Key, name), out var x) && x.Finished);
            if (next is null) continue;
            var uri = MediaUri.Combine(folder.Key, next);
            if (listed.Contains(uri)) continue;
            byUri.TryGetValue(uri, out var known);
            var item = known is not null ? ToItem(known) : new HistoryItem
            {
                Uri = uri,
                Name = next,
                Title = DisplayName(next),
                FolderUri = folder.Key,
            };
            item.Next = true;
            item.LastWatched = lastFinished.LastWatched;
            result.Add(item);
            listed.Add(uri);
        }

        return result.OrderByDescending(i => i.LastWatched).ToList();
    }

    public static HistoryItem ToItem(HistoryEntry e) => new()
    {
        Uri = e.Uri,
        Title = string.IsNullOrEmpty(e.Title) ? DisplayName(e.Name) : e.Title,
        Name = e.Name,
        FolderUri = e.FolderUri,
        Position = e.Position,
        Length = e.Length,
        Progress = Progress(e),
        Finished = e.Finished,
        LastWatched = e.LastWatched,
        PlayCount = e.PlayCount,
    };

    // ------------------------------------------------------------------ track preferences

    public static TrackPreference ToPreference(TrackInfo track) => new()
    {
        Lang = string.IsNullOrWhiteSpace(track.Lang) ? null : track.Lang.Trim(),
        Name = string.IsNullOrWhiteSpace(track.Name) ? null : track.Name.Trim(),
        Codec = string.IsNullOrWhiteSpace(track.Codec) ? null : track.Codec.Trim(),
    };

    /// <summary>
    /// The track of a new file that best matches a remembered choice: language first, then name, then codec.
    /// A track must match the language or the name (the codec alone only counts when neither is known);
    /// ties go to the first track. <c>null</c> — nothing similar, leave VLC's choice.
    /// </summary>
    public static int? MatchTrack(TrackPreference pref, IReadOnlyList<TrackInfo> tracks)
    {
        var best = -1;
        int? bestId = null;
        var identityKnown = pref.Lang is not null || pref.Name is not null;
        foreach (var t in tracks)
        {
            if (t.Id < 0) continue;
            var lang = pref.Lang is not null && Eq(pref.Lang, t.Lang);
            var name = pref.Name is not null && Eq(pref.Name, t.Name);
            var codec = pref.Codec is not null && Eq(pref.Codec, t.Codec);
            if (identityKnown ? !(lang || name) : !codec) continue;
            var score = (lang ? 4 : 0) + (name ? 2 : 0) + (codec ? 1 : 0);
            if (score > best)
            {
                best = score;
                bestId = t.Id;
            }
        }
        return bestId;
    }

    /// <summary>
    /// Track commands that apply a folder's choices to a new file: (kind, id) pairs, <c>-1</c> = subtitles off.
    /// Tracks that are already selected are skipped.
    /// </summary>
    public static List<(string Kind, int Id)> PlanTrackSelection(FolderPreferences? prefs, TracksInfo tracks)
    {
        var plan = new List<(string, int)>();
        if (prefs is null) return plan;
        if (prefs.Audio is { } audio && MatchTrack(audio, tracks.Audio) is int a && a != tracks.CurrentAudio)
            plan.Add(("audio", a));
        if (prefs.Subtitle is { } sub)
        {
            if (sub.Off)
            {
                if (tracks.CurrentSubtitle != -1 && tracks.Subtitle.Count > 0) plan.Add(("subtitle", -1));
            }
            else if (sub.Track is { } st && MatchTrack(st, tracks.Subtitle) is int s && s != tracks.CurrentSubtitle)
            {
                plan.Add(("subtitle", s));
            }
        }
        return plan;
    }

    private static bool Eq(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>Natural order: "Episode 2" before "Episode 10"; case-insensitive.</summary>
public sealed class NaturalStringComparer : IComparer<string>
{
    public static NaturalStringComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                var si = i;
                var sj = j;
                while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                while (j < y.Length && char.IsAsciiDigit(y[j])) j++;
                var a = x.AsSpan(si, i - si).TrimStart('0');
                var b = y.AsSpan(sj, j - sj).TrimStart('0');
                if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
                var c = a.SequenceCompareTo(b);
                if (c != 0) return c;
                continue;
            }
            var cx = char.ToUpperInvariant(x[i]);
            var cy = char.ToUpperInvariant(y[j]);
            if (cx != cy) return cx.CompareTo(cy);
            i++;
            j++;
        }
        var rest = (x.Length - i).CompareTo(y.Length - j);
        return rest != 0 ? rest : string.CompareOrdinal(x, y);
    }
}
