using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Sofabird.Core.History;
using Sofabird.Core.Library;
using Sofabird.Core.Protocol;

namespace Sofabird.Companion.Services;

/// <summary>
/// The library of the media folders (<see cref="Settings.MediaRoots"/>): scans them at the start, when the folders
/// change and every 30 minutes, then looks up on TMDB what the cache does not have. Network folders are left out of the
/// timer (reading one wakes the computer it is on): they are read again when a phone asks for the library and their
/// last scan is old (<see cref="LibraryRescanPolicy"/>). Phones see <c>state.library</c> change and ask for
/// <c>getLibrary</c> / <c>getShow</c>.
/// </summary>
public sealed class LibraryService : IDisposable
{
    private static readonly TimeSpan RescanEvery = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RevisionEvery = TimeSpan.FromSeconds(3);

    private readonly Settings _settings;
    private readonly HistoryStore? _history;
    private readonly BackgroundWork _background;
    private readonly ILogger _log;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly SemaphoreSlim _rescan = new(0, 1);
    /// <summary>The last scan of each media folder: its files (<c>null</c> — not reachable) and when it was read.</summary>
    private readonly Dictionary<string, (IReadOnlyList<LibraryFile>? Files, DateTimeOffset At)> _roots = new(StringComparer.OrdinalIgnoreCase);
    private long _revision;
    private volatile bool _scanning;
    private volatile bool _foldersChanged = true; // the first scan reads everything

    public LibraryService(Settings settings, AppPaths paths, HistoryStore? history, BackgroundWork background, ILogger<LibraryService> log)
    {
        _settings = settings;
        _history = history;
        _background = background;
        _log = log;
        Language = CultureInfo.CurrentUICulture.Name is { Length: > 0 } ui ? ui : "en-US";
        Catalog = new LibraryCatalog(new MetadataCache(Path.Combine(paths.DataDir, "library-metadata.json")), Language);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Sofabird/" + AppVersion.Informational);
    }

    /// <summary>The language of titles and descriptions: Windows' UI language.</summary>
    public string Language { get; }

    public LibraryCatalog Catalog { get; }

    /// <summary><c>state.library</c>.</summary>
    public LibraryInfo Info => new() { Revision = Interlocked.Read(ref _revision), Scanning = _scanning };

    /// <summary>The library changed (a scan, posters found): the state is to be published again.</summary>
    public event Action? Changed;

    public void Start() => _background.Run("Library", LoopAsync);

    /// <summary>The media folders changed: scan now.</summary>
    public void Rescan()
    {
        _foldersChanged = true;
        Wake();
    }

    private void Wake()
    {
        if (_rescan.CurrentCount == 0) _rescan.Release();
    }

    /// <summary>A phone asks for the library: network folders whose last scan is old are read again (in the background).</summary>
    private void PhoneAsked()
    {
        var now = DateTimeOffset.UtcNow;
        bool stale;
        lock (_roots)
        {
            stale = _roots.Any(r => LibraryRescanPolicy.IsNetwork(r.Key)
                && LibraryRescanPolicy.ShouldScan(true, LibraryScanReason.PhoneAsked, r.Value.At, now));
        }
        if (stale) Wake();
    }

    public LibraryMessage BuildLibrary()
    {
        PhoneAsked();
        return Catalog.BuildLibrary(Info.Revision, _scanning, Watched);
    }

    public ShowMessage? BuildShow(string key) => Catalog.BuildShow(key, Watched);

    private (double Progress, bool Finished)? Watched(string uri) =>
        _history?.Find(uri) is { } e ? (HistoryRules.Progress(e), e.Finished) : null;

    private async Task LoopAsync(CancellationToken ct)
    {
        var woken = true;
        while (true)
        {
            var reason = _foldersChanged ? LibraryScanReason.FoldersChanged
                : woken ? LibraryScanReason.PhoneAsked
                : LibraryScanReason.Periodic;
            _foldersChanged = false;
            await RefreshAsync(reason, ct).ConfigureAwait(false);
            woken = await _rescan.WaitAsync(RescanEvery, ct).ConfigureAwait(false);
        }
    }

    private async Task RefreshAsync(LibraryScanReason reason, CancellationToken ct)
    {
        SetScanning(true);
        try
        {
            var roots = _settings.MediaRoots.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var scan = await Task.Run(() => ScanRoots(roots, reason, ct), ct).ConfigureAwait(false);
            if (!SameFiles(Catalog.Scan, scan) || !Catalog.Scan.MissingRoots.SequenceEqual(scan.MissingRoots, StringComparer.OrdinalIgnoreCase))
            {
                Catalog.Scan = scan;
                _log.LogInformation("Library: {Movies} movies, {Shows} series in {Roots} folders", scan.Movies.Count, scan.Shows.Count, roots.Count);
                if (scan.MissingRoots.Count > 0)
                    _log.LogWarning("Library: not available now: {Folders}", string.Join(", ", scan.MissingRoots));
                Bump();
            }
            if (TmdbToken() is { } token) await EnrichAsync(token, ct).ConfigureAwait(false);
            else if (scan.Movies.Count + scan.Shows.Count > 0) _log.LogInformation("Library: no TMDB token, titles from the file names only");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Library refresh failed");
        }
        finally
        {
            SetScanning(false);
        }
    }

    /// <summary>Reads the folders the policy allows, keeps the last result of the others, and groups all of them.</summary>
    private LibraryScan ScanRoots(IReadOnlyList<string> roots, LibraryScanReason reason, CancellationToken ct)
    {
        foreach (var root in roots)
        {
            DateTimeOffset? last;
            lock (_roots) last = _roots.TryGetValue(root, out var previous) ? previous.At : null;
            var network = LibraryRescanPolicy.IsNetwork(root);
            if (!LibraryRescanPolicy.ShouldScan(network, reason, last, DateTimeOffset.UtcNow))
            {
                _log.LogDebug("Library: {Root} not read ({Reason}): a network folder, its computer may sleep", root, reason);
                continue;
            }
            var files = LibraryScanner.ListFiles(root, ct);
            lock (_roots) _roots[root] = (files, DateTimeOffset.UtcNow);
        }
        lock (_roots)
        {
            foreach (var gone in _roots.Keys.Except(roots, StringComparer.OrdinalIgnoreCase).ToList()) _roots.Remove(gone);
            var files = roots.SelectMany(r => _roots.TryGetValue(r, out var s) ? s.Files ?? [] : []);
            var missing = roots.Where(r => _roots.TryGetValue(r, out var s) && s.Files is null).ToList();
            return LibraryScanner.Group(files) with { MissingRoots = missing };
        }
    }

    private async Task EnrichAsync(string token, CancellationToken ct)
    {
        var found = 0;
        var enrich = Catalog.EnrichAsync(new TmdbClient(_http, token, Language), () => Interlocked.Increment(ref found), ct);
        // Posters arrive one by one; the phones re-read the library at most every few seconds.
        while (await Task.WhenAny(enrich, Task.Delay(RevisionEvery, ct)).ConfigureAwait(false) != enrich)
        {
            if (Interlocked.Exchange(ref found, 0) > 0) Bump();
        }
        await enrich.ConfigureAwait(false);
        if (Interlocked.Exchange(ref found, 0) > 0) Bump();
    }

    /// <summary>The embedded token (CI secret <c>TMDB_API_TOKEN</c>) or the user's own (<c>tmdbToken</c>).</summary>
    private string? TmdbToken() =>
        _settings.TmdbToken is { Length: > 0 } own ? own
        : typeof(LibraryService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "TmdbToken")?.Value is { Length: > 0 } embedded ? embedded : null;

    private static bool SameFiles(LibraryScan a, LibraryScan b) =>
        a.Movies.SelectMany(m => m.Files).Select(f => f.Path)
            .Concat(a.Shows.SelectMany(s => s.Episodes).Select(e => e.File.Path))
            .SequenceEqual(b.Movies.SelectMany(m => m.Files).Select(f => f.Path)
                .Concat(b.Shows.SelectMany(s => s.Episodes).Select(e => e.File.Path)), StringComparer.OrdinalIgnoreCase);

    private void SetScanning(bool on)
    {
        if (_scanning == on) return;
        _scanning = on;
        Changed?.Invoke();
    }

    private void Bump()
    {
        Interlocked.Increment(ref _revision);
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _http.Dispose();
        _rescan.Dispose();
    }
}
