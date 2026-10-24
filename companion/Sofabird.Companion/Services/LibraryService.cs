using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Sofabird.Core.History;
using Sofabird.Core.Library;
using Sofabird.Core.Protocol;

namespace Sofabird.Companion.Services;

/// <summary>
/// The library of the media folders (<see cref="Settings.MediaRoots"/>): scans them at the start, every 30 minutes and
/// when the folders change, then looks up on TMDB what the cache does not have. Phones see <c>state.library</c> change
/// and ask for <c>getLibrary</c> / <c>getShow</c>.
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
    private long _revision;
    private volatile bool _scanning;

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
        if (_rescan.CurrentCount == 0) _rescan.Release();
    }

    public LibraryMessage BuildLibrary() => Catalog.BuildLibrary(Info.Revision, _scanning, Watched);

    public ShowMessage? BuildShow(string key) => Catalog.BuildShow(key, Watched);

    private (double Progress, bool Finished)? Watched(string uri) =>
        _history?.Find(uri) is { } e ? (HistoryRules.Progress(e), e.Finished) : null;

    private async Task LoopAsync(CancellationToken ct)
    {
        while (true)
        {
            await RefreshAsync(ct).ConfigureAwait(false);
            await _rescan.WaitAsync(RescanEvery, ct).ConfigureAwait(false);
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        SetScanning(true);
        try
        {
            var roots = _settings.MediaRoots.ToList();
            var scan = await Task.Run(() => LibraryScanner.Scan(roots, ct), ct).ConfigureAwait(false);
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
