using Microsoft.Extensions.Time.Testing;
using Sofabird.Core.Library;
using Xunit;

namespace Sofabird.Core.Tests;

public sealed class LibraryCatalogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sofabird-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-13T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    private string CachePath => Path.Combine(_dir, "library-metadata.json");

    private static LibraryScan Sample() => LibraryScanner.Group(
    [
        new(@"D:\Movies", @"D:\Movies\The.Matrix.1999.mkv", 10, DateTimeOffset.UnixEpoch),
        new(@"D:\Movies", @"D:\Movies\Unknown.Film.2020.mkv", 10, DateTimeOffset.UnixEpoch),
        new(@"D:\Series", @"D:\Series\Severance\Severance.S01E01.mkv", 10, DateTimeOffset.UnixEpoch),
        new(@"D:\Series", @"D:\Series\Severance\Severance.S01E02.mkv", 10, DateTimeOffset.UnixEpoch),
    ]);

    [Fact]
    public async Task Enrich_FillsTheLibrary_AndTheCacheSavesLaterLookups()
    {
        var source = new FakeSource();
        var catalog = new LibraryCatalog(new MetadataCache(CachePath), "ru-RU", _time) { Scan = Sample() };
        var found = 0;
        await catalog.EnrichAsync(source, () => found++, CancellationToken.None);

        var library = catalog.BuildLibrary(3, scanning: false, uri => uri.EndsWith("S01E01.mkv", StringComparison.Ordinal) ? (1.0, true) : null);
        var matrix = library.Movies.Single(m => m.Year == 1999);
        Assert.Equal("Матрица", matrix.Title);
        Assert.Equal("https://image.tmdb.org/t/p/w342/matrix.jpg", matrix.Poster);
        Assert.Null(library.Movies.Single(m => m.Year == 2020).Poster); // not on TMDB: the parsed title stays
        var show = Assert.Single(library.Shows);
        Assert.Equal((2, 1), (show.Episodes, show.Unwatched));

        var detail = catalog.BuildShow(show.Key, _ => null)!;
        Assert.Equal(["Хорошие новости", null], detail.Episodes.Select(e => e.Title));
        Assert.Equal(3, found); // the movie, the show, its season

        // A new start reads the cache: nothing is looked up again, including the movie TMDB did not know.
        var again = new FakeSource();
        var catalog2 = new LibraryCatalog(new MetadataCache(CachePath), "ru-RU", _time) { Scan = Sample() };
        await catalog2.EnrichAsync(again, () => { }, CancellationToken.None);
        Assert.Equal(0, again.Calls);

        // A week later the unknown movie is asked again; the known ones stay for six months.
        _time.Advance(TimeSpan.FromDays(8));
        await new LibraryCatalog(new MetadataCache(CachePath), "ru-RU", _time) { Scan = Sample() }
            .EnrichAsync(again, () => { }, CancellationToken.None);
        Assert.Equal(1, again.Calls);
    }

    [Fact]
    public async Task ArtOf_GivesHistoryEntriesTheirPicture()
    {
        var catalog = new LibraryCatalog(new MetadataCache(CachePath), "ru-RU", _time) { Scan = Sample() };
        await catalog.EnrichAsync(new FakeSource(), () => { }, CancellationToken.None);

        var art = catalog.ArtOf(
        [
            "file:///D:/Movies/The.Matrix.1999.mkv",
            "file:///d:/series/severance/Severance.S01E01.mkv", // as the history writes it
            "file:///D:/Series/Severance/Severance.S01E02.mkv",
            "file:///D:/Movies/Unknown.Film.2020.mkv",
            "file:///E:/Elsewhere/clip.mkv",
        ]);

        Assert.Equal("https://image.tmdb.org/t/p/w300/m-back.jpg", art[LibraryCatalog.UriKey("file:///D:/Movies/The.Matrix.1999.mkv")]);
        Assert.Equal("https://image.tmdb.org/t/p/w300/e1.jpg", art[LibraryCatalog.UriKey("file:///D:/Series/Severance/Severance.S01E01.mkv")]);
        // No still for the second episode and no backdrop for the series: its poster.
        Assert.Equal("https://image.tmdb.org/t/p/w342/sev.jpg", art[LibraryCatalog.UriKey("file:///D:/Series/Severance/Severance.S01E02.mkv")]);
        Assert.Equal(3, art.Count); // the unknown movie and the file outside the library have none
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private sealed class FakeSource : IMetadataSource
    {
        public int Calls { get; private set; }

        public Task<TmdbMatch?> FindMovieAsync(string title, int? year, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(title == "The Matrix"
                ? new TmdbMatch(603, "movie", "Матрица", 1999, "…", "/matrix.jpg", "/m-back.jpg", 8.2)
                : null);
        }

        public Task<TmdbMatch?> FindShowAsync(string title, int? year, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<TmdbMatch?>(new TmdbMatch(95396, "tv", "Разделение", 2022, null, "/sev.jpg", null, null));
        }

        public Task<IReadOnlyList<TmdbEpisode>> GetSeasonAsync(int showId, int season, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<TmdbEpisode>>([new TmdbEpisode(1, 1, "Хорошие новости", null, "/e1.jpg")]);
        }
    }
}
