using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sofabird.Core.Protocol;

namespace Sofabird.Core.History;

/// <summary>Result of <see cref="HistoryStore.UpdatePosition"/>.</summary>
public readonly record struct PositionUpdate(bool Found, bool BecameFinished, bool BecameUnfinished);

/// <summary>Result of <see cref="HistoryStore.BeginPlayback"/>: the entry before this playback (null — new) and after.</summary>
public sealed record PlaybackStart(HistoryEntry? Before, HistoryEntry After);

/// <summary>
/// The watch history (<c>history.json</c> in the data dir): the companion is its source of truth. Thread-safe.
/// Changes are written atomically (temp file + replace), at most once per <see cref="SaveDelay"/>, and on
/// <see cref="Flush"/>/<see cref="Dispose"/>. A corrupt file is moved aside and the history starts empty.
/// </summary>
public sealed class HistoryStore : IDisposable
{
    public const int SchemaVersion = 1;
    public static readonly TimeSpan DefaultSaveDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PurgeInterval = TimeSpan.FromDays(1);

    private readonly object _gate = new();
    private readonly object _writeGate = new();
    private readonly ILogger _log;
    private readonly Dictionary<string, HistoryEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FolderPreferences> _folders = new(StringComparer.OrdinalIgnoreCase);
    private long _revision;
    private long _changeSeq;
    private long _writtenSeq;
    private ITimer? _saveTimer;
    private ITimer? _purgeTimer;
    private bool _disposed;

    public HistoryStore(string path, TimeProvider? clock = null, IHistoryFileSystem? fileSystem = null, ILogger? logger = null, TimeSpan? saveDelay = null)
    {
        Path = path;
        Clock = clock ?? TimeProvider.System;
        FileSystem = fileSystem ?? PhysicalHistoryFileSystem.Instance;
        _log = logger ?? NullLogger.Instance;
        SaveDelay = saveDelay ?? DefaultSaveDelay;
        Load();
        Purge();
    }

    public string Path { get; }
    public TimeProvider Clock { get; }
    public IHistoryFileSystem FileSystem { get; }
    public TimeSpan SaveDelay { get; }

    /// <summary>Changes whenever the history changes visibly (not on every periodic position save).</summary>
    public long Revision { get { lock (_gate) return _revision; } }

    public int Count { get { lock (_gate) return _entries.Count; } }

    /// <summary>The revision changed. Raised outside the lock, on the thread that made the change.</summary>
    public event Action? Changed;

    private long NowMs => Clock.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>Delete old entries once a day (they are also deleted at startup).</summary>
    public void StartMaintenance()
    {
        lock (_gate)
        {
            if (_disposed || _purgeTimer is not null) return;
            _purgeTimer = Clock.CreateTimer(_ => SafePurge(), null, PurgeInterval, PurgeInterval);
        }
    }

    // ------------------------------------------------------------------ queries

    /// <summary>Entry of a file by URI, else by name + size (the file was reached by another path); a copy.</summary>
    public HistoryEntry? Find(string uri, long? size = null)
    {
        lock (_gate) return FindLocked(uri, MediaUri.FileName(uri), size)?.Clone();
    }

    public IReadOnlyList<HistoryEntry> Snapshot()
    {
        lock (_gate) return _entries.Values.Select(e => e.Clone()).ToList();
    }

    public FolderPreferences? GetFolderPreferences(string? folderUri)
    {
        var key = MediaUri.Normalize(folderUri);
        if (key is null) return null;
        lock (_gate) return _folders.TryGetValue(key, out var p) ? p.Clone() : null;
    }

    /// <summary>
    /// <c>getHistory</c>: <paramref name="filter"/> <c>continue</c> or <c>all</c>, newest first, paged.
    /// Lists series folders for <c>continue</c>, so call it off the UI thread.
    /// </summary>
    public (int Total, List<HistoryItem> Items) Query(string filter, int limit, int offset)
    {
        var entries = Snapshot();
        List<HistoryItem> all = filter == "continue"
            ? HistoryRules.ContinueWatching(entries, folder => MediaUri.ToLocalPath(folder) is { } dir ? FileSystem.TryListFiles(dir) : null)
            : entries.OrderByDescending(e => e.LastWatched).Select(HistoryRules.ToItem).ToList();
        return (all.Count, all.Skip(Math.Max(0, offset)).Take(Math.Max(0, limit)).ToList());
    }

    /// <summary>Adds <c>progress</c>/<c>finished</c> to the files of a <c>browse</c> listing that are in the history.</summary>
    public void Annotate(IEnumerable<BrowseEntry> items)
    {
        lock (_gate)
        {
            if (_entries.Count == 0) return;
            foreach (var item in items)
            {
                if (item.Type != "file") continue;
                var e = FindLocked(item.Uri, item.Name, item.Size);
                if (e is null) continue;
                item.Progress = HistoryRules.Progress(e);
                item.Finished = e.Finished;
            }
        }
    }

    /// <summary>The next unfinished episode after <paramref name="uri"/> in its folder (natural order); null — none.</summary>
    public NextInfo? FindNextEpisode(string uri)
    {
        var folder = MediaUri.FolderOf(uri);
        var name = MediaUri.FileName(uri);
        if (folder is null || name is null || MediaUri.ToLocalPath(folder) is not { } dir) return null;
        var files = FileSystem.TryListFiles(dir);
        if (files is null) return null;
        string? next;
        lock (_gate)
        {
            next = HistoryRules.FindNextEpisode(name, files,
                f => _entries.TryGetValue(MediaUri.Combine(folder, f), out var e) && e.Finished);
        }
        if (next is null) return null;
        var nextUri = MediaUri.Combine(folder, next);
        string title;
        lock (_gate) title = _entries.TryGetValue(nextUri, out var known) && !string.IsNullOrEmpty(known.Title) ? known.Title : HistoryRules.DisplayName(next);
        return new NextInfo { Uri = nextUri, Title = title };
    }

    // ------------------------------------------------------------------ recording

    /// <summary>
    /// A file starts playing: create or update its entry (title, length, play count, last watched). An entry found by
    /// name + size under another path moves to the new one. <c>null</c> for URIs that are not files.
    /// </summary>
    public PlaybackStart? BeginPlayback(string uri, string? title, long length, long? size)
    {
        var key = MediaUri.Normalize(uri);
        var name = MediaUri.FileName(uri);
        if (key is null || name is null) return null;
        PlaybackStart result;
        lock (_gate)
        {
            var entry = FindLocked(key, name, size);
            var before = entry?.Clone();
            if (entry is null)
            {
                entry = new HistoryEntry { Uri = key };
                _entries[key] = entry;
            }
            else if (!string.Equals(entry.Uri, key, StringComparison.OrdinalIgnoreCase))
            {
                _log.LogInformation("History: {Old} is now at {New}", entry.Uri, key);
                _entries.Remove(entry.Uri);
                entry.Uri = key;
                _entries[key] = entry;
            }
            entry.Uri = key;
            entry.Name = name;
            entry.FolderUri = MediaUri.FolderOf(key) ?? "";
            if (size is long s) entry.Size = s;
            if (!string.IsNullOrWhiteSpace(title)) entry.Title = title.Trim();
            if (string.IsNullOrEmpty(entry.Title)) entry.Title = HistoryRules.DisplayName(name);
            if (length > 0) entry.Length = length;
            entry.LastWatched = NowMs;
            entry.PlayCount++;
            result = new PlaybackStart(before, entry.Clone());
            MarkChangedLocked(notify: true);
        }
        RaiseChanged();
        return result;
    }

    /// <summary>Title or length became known after the start.</summary>
    public void UpdateMeta(string uri, string? title, long length)
    {
        var changed = false;
        lock (_gate)
        {
            if (FindExactLocked(uri) is not { } e) return;
            if (!string.IsNullOrWhiteSpace(title) && e.Title != title.Trim()) { e.Title = title.Trim(); changed = true; }
            if (length > 0 && e.Length != length) { e.Length = length; changed = true; }
            if (changed) MarkChangedLocked(notify: true);
        }
        if (changed) RaiseChanged();
    }

    /// <summary>
    /// Save the playback position. Less than 5% left → finished (position 0). A finished entry stays finished unless
    /// <paramref name="allowUnfinish"/> (the file is being watched again) and at least the resume threshold is reached.
    /// <paramref name="notify"/> = false for the periodic saves during playback (no new revision).
    /// </summary>
    public PositionUpdate UpdatePosition(string uri, long position, long length, bool allowUnfinish, bool notify)
    {
        PositionUpdate result;
        bool raised;
        lock (_gate)
        {
            if (FindExactLocked(uri) is not { } e) return default;
            position = Math.Max(0, position);
            if (length > 0) e.Length = length;
            e.LastWatched = NowMs;
            bool became = false, unbecame = false;
            if (HistoryRules.IsFinishedAt(position, e.Length))
            {
                became = !e.Finished;
                e.Finished = true;
                e.Position = 0;
            }
            else if (e.Finished)
            {
                if (allowUnfinish && position >= HistoryRules.ResumeThresholdSeconds)
                {
                    e.Finished = false;
                    e.Position = position;
                    unbecame = true;
                }
                else
                {
                    e.Position = 0;
                }
            }
            else
            {
                e.Position = position;
            }
            raised = notify || became || unbecame;
            MarkChangedLocked(raised);
            result = new PositionUpdate(true, became, unbecame);
        }
        if (raised) RaiseChanged();
        return result;
    }

    /// <summary>Mark watched / unwatched (<c>historyUpdate</c>, end of file). Unwatched resets the position to 0.
    /// Marking a file that is not in the history as watched adds it. Returns whether anything changed.</summary>
    public bool SetFinished(string uri, bool finished)
    {
        var key = MediaUri.Normalize(uri) ?? throw new ArgumentException("not a file URI: " + uri);
        lock (_gate)
        {
            var e = FindExactLocked(key);
            if (e is null)
            {
                if (!finished) return false;
                var name = MediaUri.FileName(key) ?? key;
                e = new HistoryEntry
                {
                    Uri = key,
                    Name = name,
                    Title = HistoryRules.DisplayName(name),
                    FolderUri = MediaUri.FolderOf(key) ?? "",
                    Size = MediaUri.ToLocalPath(key) is { } p ? FileSystem.TryGetFileSize(p) : null,
                    LastWatched = NowMs,
                };
                _entries[key] = e;
            }
            else if (e.Finished == finished && (finished || e.Position == 0))
            {
                return false;
            }
            e.Finished = finished;
            e.Position = 0;
            MarkChangedLocked(notify: true);
        }
        RaiseChanged();
        return true;
    }

    public bool Remove(string uri)
    {
        lock (_gate)
        {
            var e = FindExactLocked(uri);
            if (e is null) return false;
            _entries.Remove(e.Uri);
            MarkChangedLocked(notify: true);
        }
        RaiseChanged();
        return true;
    }

    /// <summary>Forget everything: entries and the per-folder track choices.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _folders.Clear();
            MarkChangedLocked(notify: true);
        }
        RaiseChanged();
    }

    public void SetAudioPreference(string folderUri, TrackPreference pref) =>
        UpdateFolder(folderUri, p => p.Audio = pref.Clone());

    public void SetSubtitlePreference(string folderUri, SubtitleChoice choice) =>
        UpdateFolder(folderUri, p => p.Subtitle = choice.Off ? SubtitleChoice.Disabled : SubtitleChoice.Of(choice.Track!.Clone()));

    private void UpdateFolder(string folderUri, Action<FolderPreferences> change)
    {
        var key = MediaUri.Normalize(folderUri);
        if (key is null) return;
        lock (_gate)
        {
            if (!_folders.TryGetValue(key, out var p)) _folders[key] = p = new FolderPreferences();
            change(p);
            MarkChangedLocked(notify: false); // not visible in any list
        }
    }

    /// <summary>Delete entries not watched for <see cref="HistoryRules.Retention"/> (and track choices of folders left empty).</summary>
    public int Purge()
    {
        int removed;
        lock (_gate)
        {
            var cutoff = Clock.GetUtcNow().Subtract(HistoryRules.Retention).ToUnixTimeMilliseconds();
            var old = _entries.Values.Where(e => e.LastWatched < cutoff).Select(e => e.Uri).ToList();
            foreach (var uri in old) _entries.Remove(uri);
            removed = old.Count;
            if (removed == 0) return 0;
            var live = new HashSet<string>(_entries.Values.Select(e => e.FolderUri), StringComparer.OrdinalIgnoreCase);
            foreach (var folder in _folders.Keys.Where(f => !live.Contains(f)).ToList()) _folders.Remove(folder);
            MarkChangedLocked(notify: true);
        }
        _log.LogInformation("History: removed {Count} entries not watched for {Days} days", removed, (int)HistoryRules.Retention.TotalDays);
        RaiseChanged();
        return removed;
    }

    private void SafePurge()
    {
        try { Purge(); }
        catch (Exception ex) { _log.LogWarning(ex, "History purge failed"); }
    }

    // ------------------------------------------------------------------ persistence

    /// <summary>Write pending changes now.</summary>
    public void Flush()
    {
        string json;
        long seq;
        lock (_gate)
        {
            _saveTimer?.Dispose();
            _saveTimer = null;
            if (_changeSeq == _writtenSeq) return;
            seq = _changeSeq;
            var file = new HistoryFile
            {
                Version = SchemaVersion,
                Revision = _revision,
                Entries = _entries.Values.OrderBy(e => e.Uri, StringComparer.OrdinalIgnoreCase).ToList(),
                Folders = new Dictionary<string, FolderPreferences>(_folders, StringComparer.OrdinalIgnoreCase),
            };
            json = JsonSerializer.Serialize(file, HistoryJsonContext.File.HistoryFile);
        }
        lock (_writeGate)
        {
            if (seq <= Interlocked.Read(ref _writtenSeq)) return;
            try
            {
                FileSystem.WriteAllTextAtomic(Path, json);
                Interlocked.Exchange(ref _writtenSeq, seq);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(ex, "Writing {Path} failed", Path);
            }
        }
    }

    private void Load()
    {
        string? text;
        try
        {
            text = FileSystem.ReadAllText(Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Reading {Path} failed; starting with an empty history", Path);
            MoveAside();
            return;
        }
        if (text is null) return;

        HistoryFile? file;
        try
        {
            file = JsonSerializer.Deserialize(text, HistoryJsonContext.File.HistoryFile);
            if (file is null) throw new JsonException("empty document");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            _log.LogWarning("{Path} is corrupt ({Error}); keeping it aside and starting with an empty history", Path, ex.Message);
            MoveAside();
            return;
        }
        if (file.Version > SchemaVersion)
            _log.LogWarning("{Path} was written by a newer companion (schema {Version}); reading what is known", Path, file.Version);

        foreach (var e in file.Entries ?? [])
        {
            var key = MediaUri.Normalize(e.Uri);
            if (key is null) continue;
            e.Uri = key;
            if (string.IsNullOrEmpty(e.Name)) e.Name = MediaUri.FileName(key) ?? "";
            if (string.IsNullOrEmpty(e.FolderUri)) e.FolderUri = MediaUri.FolderOf(key) ?? "";
            if (e.Position < 0) e.Position = 0;
            if (_entries.TryGetValue(key, out var existing) && existing.LastWatched >= e.LastWatched) continue;
            _entries[key] = e;
        }
        foreach (var (folder, prefs) in file.Folders ?? [])
        {
            var key = MediaUri.Normalize(folder);
            if (key is not null && prefs is not null) _folders[key] = prefs;
        }
        _revision = file.Revision;
        _log.LogInformation("History: {Count} entries, {Folders} folders with track choices", _entries.Count, _folders.Count);
    }

    private void MoveAside()
    {
        var aside = Path + ".corrupt-" + Clock.GetUtcNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        try { FileSystem.Move(Path, aside); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.LogWarning(ex, "Moving {Path} aside failed", Path); }
    }

    private void MarkChangedLocked(bool notify)
    {
        if (notify) _revision++;
        _changeSeq++;
        if (_saveTimer is null && !_disposed)
            _saveTimer = Clock.CreateTimer(_ => SafeFlush(), null, SaveDelay, Timeout.InfiniteTimeSpan);
    }

    private void SafeFlush()
    {
        try { Flush(); }
        catch (Exception ex) { _log.LogWarning(ex, "Saving the history failed"); }
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { _log.LogWarning(ex, "History Changed handler failed"); }
    }

    private HistoryEntry? FindExactLocked(string uri)
    {
        var key = MediaUri.Normalize(uri);
        return key is not null && _entries.TryGetValue(key, out var e) ? e : null;
    }

    private HistoryEntry? FindLocked(string uri, string? name, long? size)
    {
        if (FindExactLocked(uri) is { } e) return e;
        if (size is not long s || string.IsNullOrEmpty(name)) return null;
        return _entries.Values
            .Where(x => x.Size == s && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
            .MaxBy(x => x.LastWatched);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _purgeTimer?.Dispose();
            _purgeTimer = null;
        }
        Flush();
    }
}
