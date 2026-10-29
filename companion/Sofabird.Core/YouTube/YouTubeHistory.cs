using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sofabird.Core.History;
using Sofabird.Core.Protocol;

namespace Sofabird.Core.YouTube;

/// <summary>A video played in the TV browser.</summary>
public sealed class YouTubeHistoryEntry
{
    public string VideoId { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Channel { get; set; }
    /// <summary>Where it stopped, seconds (0 when finished).</summary>
    public long Position { get; set; }
    /// <summary>Seconds; 0 — unknown (a live stream).</summary>
    public long Length { get; set; }
    public bool Finished { get; set; }
    /// <summary>Unix ms of the last playback.</summary>
    public long LastWatched { get; set; }
    public int PlayCount { get; set; }

    public YouTubeHistoryEntry Clone() => (YouTubeHistoryEntry)MemberwiseClone();
}

/// <summary>Contents of <c>youtube-history.json</c>.</summary>
public sealed class YouTubeHistoryFile
{
    public int Version { get; set; } = 1;
    public long Revision { get; set; }
    public List<YouTubeHistoryEntry> Entries { get; set; } = [];
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(YouTubeHistoryFile))]
public partial class YouTubeHistoryJsonContext : JsonSerializerContext
{
    /// <summary>Same options plus relaxed escaping, so Cyrillic titles stay readable in the file.</summary>
    public static YouTubeHistoryJsonContext File { get; } = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
}

/// <summary>
/// The videos played on the TV (sent from a phone, or the next one YouTube played by itself) and where they stopped,
/// from what the TV extension reports (<see cref="Record"/>): <c>youtube-history.json</c> in the data dir. Phones see
/// them in the history (<c>kind: "youtube"</c>) and in "Continue watching" once at least
/// <see cref="ContinueThresholdSeconds"/> were watched; opening one continues where it stopped (<see cref="ResumeAt"/>).
/// Thread-safe; written at most once per save delay; entries older than <see cref="HistoryRules.Retention"/> go.
/// </summary>
public sealed class YouTubeHistory : IDisposable
{
    /// <summary>
    /// Watched this long, a video is worth continuing: YouTube videos are short, and closing one after 40 s to watch
    /// later is common (a minute left such a video out of "Continue watching").
    /// </summary>
    public const long ContinueThresholdSeconds = 15;
    public const int MaxEntries = 500;
    public const string Kind = "youtube";
    private static readonly TimeSpan PositionEvery = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly Dictionary<string, YouTubeHistoryEntry> _entries = new(StringComparer.Ordinal);
    private readonly IHistoryFileSystem _files;
    private readonly ILogger _log;
    private readonly TimeSpan _saveDelay;
    private long _revision;
    private bool _dirty;
    private ITimer? _saveTimer;
    private string? _current;
    private bool _currentPlaying;
    private DateTimeOffset _positionSavedAt;
    private bool _disposed;

    public YouTubeHistory(string path, TimeProvider? clock = null, IHistoryFileSystem? files = null, ILogger? logger = null,
        TimeSpan? saveDelay = null)
    {
        Path = path;
        Clock = clock ?? TimeProvider.System;
        _files = files ?? PhysicalHistoryFileSystem.Instance;
        _log = logger ?? NullLogger.Instance;
        _saveDelay = saveDelay ?? HistoryStore.DefaultSaveDelay;
        Load();
        Purge();
    }

    public string Path { get; }
    public TimeProvider Clock { get; }

    /// <summary>Changes whenever a list would show something new (not on every position save).</summary>
    public long Revision { get { lock (_gate) return _revision; } }

    /// <summary>The revision changed; raised outside the lock.</summary>
    public event Action? Changed;

    /// <summary>The files' and the videos' items in one list, newest first (each list keeps its order among equals).</summary>
    public static List<HistoryItem> Merge(IEnumerable<HistoryItem> files, IEnumerable<HistoryItem> videos) =>
        files.Concat(videos).OrderByDescending(i => i.LastWatched).ToList();

    /// <summary>The watch page of a video: the history's <c>uri</c>.</summary>
    public static string UriOf(string videoId) => "https://www.youtube.com/watch?v=" + videoId;

    /// <summary>The video of a history <c>uri</c> (any YouTube link); <c>null</c> — not YouTube.</summary>
    public static string? VideoIdOf(string? uri) => YouTubeLink.Parse(uri)?.VideoId;

    /// <summary>
    /// What the TV page shows now (every change the extension reports, and a timer while it plays). A video (not an
    /// ad, with a length) is added or updated; the position is kept every <see cref="PositionEvery"/>; less than 5%
    /// left — finished. <c>null</c> or a page without a video: the video was left (the lists get its last position).
    /// </summary>
    public void Record(TvPageState? page)
    {
        bool notify;
        lock (_gate)
        {
            if (_disposed) return;
            notify = page is { Watching: true, Ad: false, VideoId: { } id, Duration: > 0 }
                ? RecordLocked(page, id)
                : LeftLocked();
        }
        if (notify) RaiseChanged();
    }

    private bool RecordLocked(TvPageState page, string id)
    {
        var now = Clock.GetUtcNow();
        var notify = false;
        if (!_entries.TryGetValue(id, out var e))
        {
            e = new YouTubeHistoryEntry { VideoId = id };
            _entries[id] = e;
            TrimLocked();
        }
        if (_current != id)
        {
            _current = id;
            e.PlayCount++;
            notify = true;
        }
        if (!string.IsNullOrWhiteSpace(page.Title) && e.Title != page.Title.Trim()) { e.Title = page.Title.Trim(); notify = true; }
        if (!string.IsNullOrWhiteSpace(page.Channel) && e.Channel != page.Channel.Trim()) { e.Channel = page.Channel.Trim(); notify = true; }
        if (_currentPlaying != page.Playing) { _currentPlaying = page.Playing; notify = true; } // paused: lists show where
        e.Length = (long)page.Duration;
        e.LastWatched = now.ToUnixTimeMilliseconds();
        var position = (long)page.TimeAt(now.UtcDateTime);
        if (page.Ended || HistoryRules.IsFinishedAt(position, e.Length))
        {
            notify |= !e.Finished;
            e.Finished = true;
            e.Position = 0;
        }
        else if (e.Finished)
        {
            // Watched again: unfinished once it is worth continuing.
            if (position >= ContinueThresholdSeconds)
            {
                e.Finished = false;
                e.Position = position;
                notify = true;
            }
        }
        else if (notify || now - _positionSavedAt >= PositionEvery || Math.Abs(position - e.Position) > PositionEvery.TotalSeconds * 2)
        {
            e.Position = position;
            _positionSavedAt = now;
        }
        else
        {
            return false;
        }
        MarkChangedLocked(notify);
        return notify;
    }

    private bool LeftLocked()
    {
        if (_current is null) return false;
        _current = null;
        _currentPlaying = false;
        MarkChangedLocked(notify: true);
        return true;
    }

    /// <summary>Where to open a video: its unfinished position, or <c>null</c> (from the start).</summary>
    public int? ResumeAt(string videoId)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(videoId, out var e) && !e.Finished && e.Position >= ContinueThresholdSeconds
                ? (int)e.Position
                : null;
        }
    }

    /// <summary>
    /// The history items for <c>getHistory</c>: <c>continue</c> — unfinished ones watched at least
    /// <see cref="ContinueThresholdSeconds"/>; anything else — all of them. Newest first.
    /// </summary>
    public List<HistoryItem> Items(string filter)
    {
        lock (_gate)
        {
            return _entries.Values
                .Where(e => filter != "continue" || (!e.Finished && e.Length > 0 && e.Position >= ContinueThresholdSeconds))
                .OrderByDescending(e => e.LastWatched)
                .Select(ToItem)
                .ToList();
        }
    }

    public IReadOnlyList<YouTubeHistoryEntry> Snapshot()
    {
        lock (_gate) return _entries.Values.Select(e => e.Clone()).ToList();
    }

    /// <summary>Mark watched / unwatched (unwatched starts it from the beginning); whether anything changed.</summary>
    public bool SetFinished(string videoId, bool finished)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(videoId, out var e) || (e.Finished == finished && e.Position == 0)) return false;
            e.Finished = finished;
            e.Position = 0;
            MarkChangedLocked(notify: true);
        }
        RaiseChanged();
        return true;
    }

    public bool Remove(string videoId)
    {
        lock (_gate)
        {
            if (!_entries.Remove(videoId)) return false;
            if (_current == videoId) _current = null;
            MarkChangedLocked(notify: true);
        }
        RaiseChanged();
        return true;
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (_entries.Count == 0) return;
            _entries.Clear();
            _current = null;
            MarkChangedLocked(notify: true);
        }
        RaiseChanged();
    }

    /// <summary>Deletes entries not watched for <see cref="HistoryRules.Retention"/>.</summary>
    public int Purge()
    {
        int removed;
        lock (_gate)
        {
            var cutoff = Clock.GetUtcNow().Subtract(HistoryRules.Retention).ToUnixTimeMilliseconds();
            var old = _entries.Values.Where(e => e.LastWatched < cutoff).Select(e => e.VideoId).ToList();
            old.ForEach(id => _entries.Remove(id));
            removed = old.Count;
            if (removed == 0) return 0;
            MarkChangedLocked(notify: true);
        }
        RaiseChanged();
        return removed;
    }

    /// <summary>Writes pending changes now.</summary>
    public void Flush()
    {
        string json;
        lock (_gate)
        {
            _saveTimer?.Dispose();
            _saveTimer = null;
            if (!_dirty) return;
            _dirty = false;
            var file = new YouTubeHistoryFile
            {
                Revision = _revision,
                Entries = _entries.Values.OrderByDescending(e => e.LastWatched).ToList(),
            };
            json = JsonSerializer.Serialize(file, YouTubeHistoryJsonContext.File.YouTubeHistoryFile);
        }
        try
        {
            _files.WriteAllTextAtomic(Path, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Writing {Path} failed", Path);
        }
    }

    private static HistoryItem ToItem(YouTubeHistoryEntry e) => new()
    {
        Uri = UriOf(e.VideoId),
        Title = e.Title,
        Position = e.Position,
        Length = e.Length,
        Progress = e.Finished ? 1 : e.Length > 0 ? Math.Clamp((double)e.Position / e.Length, 0, 1) : 0,
        Finished = e.Finished,
        LastWatched = e.LastWatched,
        PlayCount = e.PlayCount,
        ArtUrl = $"https://i.ytimg.com/vi/{e.VideoId}/hqdefault.jpg",
        Kind = Kind,
        Channel = e.Channel,
    };

    private void TrimLocked()
    {
        if (_entries.Count <= MaxEntries) return;
        foreach (var old in _entries.Values.OrderBy(e => e.LastWatched).Take(_entries.Count - MaxEntries).ToList())
            _entries.Remove(old.VideoId);
    }

    private void Load()
    {
        string? text;
        try
        {
            text = _files.ReadAllText(Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Reading {Path} failed; starting with an empty YouTube history", Path);
            return;
        }
        if (text is null) return;
        try
        {
            var file = JsonSerializer.Deserialize(text, YouTubeHistoryJsonContext.File.YouTubeHistoryFile)
                ?? throw new JsonException("empty document");
            foreach (var e in file.Entries ?? [])
            {
                if (string.IsNullOrEmpty(e.VideoId)) continue;
                e.Position = Math.Max(0, e.Position);
                _entries[e.VideoId] = e;
            }
            _revision = file.Revision;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            _log.LogWarning("{Path} is corrupt ({Error}); keeping it aside, the YouTube history starts empty", Path, ex.Message);
            var aside = Path + ".corrupt-" + Clock.GetUtcNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            try { _files.Move(Path, aside); }
            catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException) { }
        }
    }

    private void MarkChangedLocked(bool notify)
    {
        if (notify) _revision++;
        _dirty = true;
        if (_saveTimer is null && !_disposed)
            _saveTimer = Clock.CreateTimer(_ => SafeFlush(), null, _saveDelay, Timeout.InfiniteTimeSpan);
    }

    private void SafeFlush()
    {
        try { Flush(); }
        catch (Exception ex) { _log.LogWarning(ex, "Saving the YouTube history failed"); }
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { _log.LogWarning(ex, "YouTube history Changed handler failed"); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Flush();
    }
}
