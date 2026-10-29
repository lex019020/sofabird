using Sofabird.Core.Library;
using Xunit;

namespace Sofabird.Core.Tests;

public class LibraryScannerTests
{
    private static LibraryFile F(string root, string relative, long size = 1_000) =>
        new(root, Path.Combine(root, relative), size, DateTimeOffset.UnixEpoch);

    [Fact]
    public void A_folder_that_cannot_be_reached_is_reported()
    {
        var present = Directory.CreateTempSubdirectory("sofabird-lib-");
        try
        {
            File.WriteAllBytes(Path.Combine(present.FullName, "The.Matrix.1999.mkv"), [1]);
            var gone = Path.Combine(present.FullName, "not-there");
            var scan = LibraryScanner.Scan([present.FullName, gone]);
            Assert.Single(scan.Movies);
            Assert.Equal([gone], scan.MissingRoots);
        }
        finally
        {
            present.Delete(recursive: true);
        }
    }

    [Fact]
    public void Groups_MoviesAndSeries_AcrossFolders()
    {
        var scan = LibraryScanner.Group(
        [
            F(@"D:\Movies", "The.Matrix.1999.1080p.mkv"),
            F(@"D:\Movies", @"Inception (2010)\movie.mkv"),
            F(@"D:\Series", @"Severance\Season 1\Severance.S01E02.mkv"),
            F(@"D:\Series", @"Severance\Season 1\Severance.S01E01.mkv"),
            F(@"E:\More", "severance.s02e01.720p.mkv"),
            F(@"D:\Series", @"The Expanse (2015)\Season 2\E05 - Pilot.mkv"),
        ]);

        Assert.Equal(["Inception", "The Matrix"], scan.Movies.Select(m => m.Title));
        Assert.Equal(2010, scan.Movies[0].Year);
        Assert.Equal(["Severance", "The Expanse"], scan.Shows.Select(s => s.Title));
        Assert.Equal([(1, 1), (1, 2), (2, 1)], scan.Shows[0].Episodes.Select(e => (e.Season, e.Episode)));
        Assert.Equal((2, 5), (scan.Shows[1].Episodes[0].Season, scan.Shows[1].Episodes[0].Episode));
    }

    [Fact]
    public void SameMovieTwice_IsOneMovieWithVersions_BiggestFirst()
    {
        var scan = LibraryScanner.Group(
        [
            F(@"D:\Movies", "The.Matrix.1999.1080p.mkv", size: 10),
            F(@"D:\Movies", "The.Matrix.1999.2160p.mkv", size: 50),
            F(@"D:\Movies", "The.Matrix.2021.mkv"),
        ]);
        Assert.Equal(2, scan.Movies.Count);
        var classic = scan.Movies.Single(m => m.Year == 1999);
        Assert.Equal(["The.Matrix.1999.2160p.mkv", "The.Matrix.1999.1080p.mkv"], classic.Files.Select(f => f.FileName));
    }

    [Fact]
    public void Skips_SamplesAndExtras()
    {
        var scan = LibraryScanner.Group(
        [
            F(@"D:\Movies", @"Movie (2010)\Movie.2010.mkv"),
            F(@"D:\Movies", @"Movie (2010)\Movie.2010.sample.mkv"),
            F(@"D:\Movies", @"Movie (2010)\Extras\Making of.mkv"),
        ]);
        var movie = Assert.Single(scan.Movies);
        Assert.Single(movie.Files);
    }

    [Fact]
    public void Scan_ReadsVideoFilesFromDisk()
    {
        var root = Path.Combine(Path.GetTempPath(), "sofabird-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Show", "Season 1"));
            File.WriteAllText(Path.Combine(root, "Show", "Season 1", "Show.S01E01.mkv"), "x");
            File.WriteAllText(Path.Combine(root, "Movie.2020.mp4"), "x");
            File.WriteAllText(Path.Combine(root, "notes.txt"), "x");

            var scan = LibraryScanner.Scan([root, Path.Combine(root, "missing")]);

            Assert.Equal("Movie", Assert.Single(scan.Movies).Title);
            var show = Assert.Single(scan.Shows);
            Assert.Equal("Show", show.Title);
            Assert.StartsWith("file:///", show.Episodes[0].File.Uri, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
