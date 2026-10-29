using Sofabird.Core.History;
using Sofabird.Core.Protocol;

namespace Sofabird.Core.Library;

/// <summary>Where metadata comes from (<see cref="TmdbClient"/>; a fake in tests).</summary>
public interface IMetadataSource
{
    Task<TmdbMatch?> FindMovieAsync(string title, int? year, CancellationToken ct);
    Task<TmdbMatch?> FindShowAsync(string title, int? year, CancellationToken ct);
    Task<IReadOnlyList<TmdbEpisode>> GetSeasonAsync(int showId, int season, CancellationToken ct);
}

/// <summary>The history's view of a file: watched fraction and whether it is finished; <c>null</c> = never played.</summary>
public delegate (double Progress, bool Finished)? WatchLookup(string uri);

/// <summary>
/// The library the phones see: the last scan of the media folders plus the TMDB metadata in the cache. Builds the
/// <c>library</c> and <c>show</c> replies and looks up what the cache does not have yet. Thread-safe: the scan and the
/// lookups run in the background while sessions read.
/// </summary>
public sealed class LibraryCatalog
{
    private readonly MetadataCache _cache;
    private readonly string _language;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private LibraryScan _scan = LibraryScan.Empty;

    public LibraryCatalog(MetadataCache cache, string language, TimeProvider? time = null)
    {
        _cache = cache;
        _language = language;
        _time = time ?? TimeProvider.System;
    }

    public LibraryScan Scan
    {
        get { lock (_lock) return _scan; }
        set { lock (_lock) _scan = value; }
    }

    /// <summary>
    /// Looks up the movies, series and seasons of the current scan that the cache does not have (or has too old),
    /// one at a time. <paramref name="onFound"/> is called after each one, so the phones see posters appear.
    /// </summary>
    public async Task EnrichAsync(IMetadataSource source, Action onFound, CancellationToken ct)
    {
        var scan = Scan;
        foreach (var movie in scan.Movies)
        {
            var key = MetadataCache.TitleKey("movie", ReleaseNameParser.Key(movie.Title), movie.Year);
            if (Has(key)) continue;
            var match = await source.FindMovieAsync(movie.Title, movie.Year, ct).ConfigureAwait(false);
            Remember(key, match);
            if (match is not null) onFound();
        }
        foreach (var show in scan.Shows)
        {
            var key = MetadataCache.TitleKey("tv", show.Key, null);
            TmdbMatch? match;
            if (!TryGet(key, out match))
            {
                match = await source.FindShowAsync(show.Title, show.Year, ct).ConfigureAwait(false);
                Remember(key, match);
                if (match is not null) onFound();
            }
            if (match is null) continue;
            foreach (var season in show.Episodes.Select(e => e.Season).Distinct())
            {
                var seasonKey = MetadataCache.SeasonKey(match.Id, season);
                if (HasSeason(seasonKey)) continue;
                var episodes = await source.GetSeasonAsync(match.Id, season, ct).ConfigureAwait(false);
                lock (_lock) _cache.SetSeason(seasonKey, _language, episodes, _time.GetUtcNow());
                if (episodes.Count > 0) onFound();
            }
        }
        lock (_lock) _cache.Save(_time.GetUtcNow());
    }

    public LibraryMessage BuildLibrary(long revision, bool scanning, WatchLookup watched)
    {
        lock (_lock)
        {
            return new LibraryMessage
            {
                Revision = revision,
                Scanning = scanning,
                MissingFolders = _scan.MissingRoots,
                Movies = _scan.Movies.Select(m =>
                {
                    var item = Fill(new MovieItem { Key = m.Key }, m.Title, m.Year,
                        Match(MetadataCache.TitleKey("movie", ReleaseNameParser.Key(m.Title), m.Year)));
                    item.Files = m.Files.Select(f => File(f, watched)).ToList();
                    return item;
                }).ToList(),
                Shows = _scan.Shows.Select(s => BuildShowItem(s, watched)).ToList(),
            };
        }
    }

    /// <returns><c>null</c> when the library has no series with this key (any more).</returns>
    public ShowMessage? BuildShow(string key, WatchLookup watched)
    {
        lock (_lock)
        {
            var show = _scan.Shows.FirstOrDefault(s => s.Key == key);
            if (show is null) return null;
            var match = Match(MetadataCache.TitleKey("tv", show.Key, null));
            var names = new Dictionary<(int, int), TmdbEpisode>();
            if (match is not null)
            {
                foreach (var season in show.Episodes.Select(e => e.Season).Distinct())
                {
                    if (!_cache.TryGetSeason(MetadataCache.SeasonKey(match.Id, season), _language, _time.GetUtcNow(), out var episodes)) continue;
                    foreach (var e in episodes) names[(e.Season, e.Episode)] = e;
                }
            }
            return new ShowMessage
            {
                Show = BuildShowItem(show, watched),
                Episodes = show.Episodes.Select(e =>
                {
                    names.TryGetValue((e.Season, e.Episode), out var tmdb);
                    return new EpisodeItem
                    {
                        Season = e.Season,
                        Episode = e.Episode,
                        Title = tmdb?.Title,
                        Overview = tmdb?.Overview,
                        Still = TmdbClient.ImageUrl(tmdb?.Still, "w300"),
                        File = File(e.File, watched),
                    };
                }).ToList(),
            };
        }
    }

    /// <summary>
    /// Pictures for history entries ("Continue watching" on the phone), by normalized URI: an episode's still, else the
    /// movie's or series' backdrop, else its poster — 16:9 where TMDB has one. Files outside the library or without a
    /// TMDB match get none.
    /// </summary>
    public IReadOnlyDictionary<string, string> ArtOf(IEnumerable<string> uris)
    {
        var wanted = new HashSet<string>(uris.Select(UriKey), StringComparer.OrdinalIgnoreCase);
        var art = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0) return art;
        lock (_lock)
        {
            foreach (var m in _scan.Movies)
            {
                var hits = m.Files.Select(f => UriKey(f.Uri)).Where(wanted.Contains).ToList();
                if (hits.Count == 0) continue;
                var match = Match(MetadataCache.TitleKey("movie", ReleaseNameParser.Key(m.Title), m.Year));
                if (Picture(match) is not { } url) continue;
                foreach (var hit in hits) art[hit] = url;
            }
            foreach (var s in _scan.Shows)
            {
                var hits = s.Episodes.Where(e => wanted.Contains(UriKey(e.File.Uri))).ToList();
                if (hits.Count == 0) continue;
                var match = Match(MetadataCache.TitleKey("tv", s.Key, null));
                foreach (var e in hits)
                {
                    var still = match is not null
                        && _cache.TryGetSeason(MetadataCache.SeasonKey(match.Id, e.Season), _language, _time.GetUtcNow(), out var episodes)
                            ? episodes.FirstOrDefault(x => x.Episode == e.Episode)?.Still
                            : null;
                    if ((TmdbClient.ImageUrl(still, "w300") ?? Picture(match)) is { } url) art[UriKey(e.File.Uri)] = url;
                }
            }
        }
        return art;
    }

    /// <summary>The key <see cref="ArtOf"/> answers by: the history's normalized form of a file URI.</summary>
    public static string UriKey(string uri) => MediaUri.Normalize(uri) ?? uri;

    private static string? Picture(TmdbMatch? match) =>
        TmdbClient.ImageUrl(match?.Backdrop, "w300") ?? TmdbClient.ImageUrl(match?.Poster, "w342");

    private ShowItem BuildShowItem(LibraryShow s, WatchLookup watched)
    {
        var item = Fill(new ShowItem { Key = s.Key }, s.Title, s.Year, Match(MetadataCache.TitleKey("tv", s.Key, null)));
        item.Episodes = s.Episodes.Count;
        item.Unwatched = s.Episodes.Count(e => watched(e.File.Uri) is not { Finished: true });
        return item;
    }

    private static T Fill<T>(T item, string title, int? year, TmdbMatch? match) where T : LibraryTitle
    {
        item.Title = match?.Title ?? title;
        item.Year = match?.Year ?? year;
        item.Overview = match?.Overview;
        item.Poster = TmdbClient.ImageUrl(match?.Poster, "w342");
        item.Backdrop = TmdbClient.ImageUrl(match?.Backdrop, "w1280");
        item.Rating = match?.Rating;
        return item;
    }

    private static MediaFile File(LibraryFile f, WatchLookup watched)
    {
        var w = watched(f.Uri);
        return new MediaFile { Uri = f.Uri, Name = f.FileName, Size = f.Size, Progress = w?.Progress, Finished = w?.Finished };
    }

    private TmdbMatch? Match(string key) =>
        _cache.TryGet(key, _language, _time.GetUtcNow(), out var match) ? match : null;

    private bool Has(string key) => TryGet(key, out _);

    private bool TryGet(string key, out TmdbMatch? match)
    {
        lock (_lock) return _cache.TryGet(key, _language, _time.GetUtcNow(), out match);
    }

    private bool HasSeason(string key)
    {
        lock (_lock) return _cache.TryGetSeason(key, _language, _time.GetUtcNow(), out _);
    }

    private void Remember(string key, TmdbMatch? match)
    {
        lock (_lock) _cache.Set(key, _language, match, _time.GetUtcNow());
    }
}
