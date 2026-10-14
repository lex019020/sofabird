using Sofabird.Core.Library;
using Xunit;

namespace Sofabird.Core.Tests;

public class ReleaseNameParserTests
{
    [Theory]
    [InlineData("The.Lord.of.the.Rings.The.Fellowship.of.the.Ring.2001.US.Warner.Bros.UHD.BDRemux.2160p-rutracker.mkv",
        "The Lord of the Rings The Fellowship of the Ring", 2001)]
    [InlineData("The.Matrix.1999.2160p.UHD.BluRay.x265-GROUP.mkv", "The Matrix", 1999)]
    [InlineData("Blade Runner 2049 (2017) [1080p].mkv", "Blade Runner 2049", 2017)]
    [InlineData("1917.2019.1080p.BluRay.mkv", "1917", 2019)]
    [InlineData("1917.mkv", "1917", null)]
    [InlineData("2001 A Space Odyssey 1968.mkv", "2001 A Space Odyssey", 1968)]
    [InlineData("Inception.mkv", "Inception", null)]
    [InlineData("Dune Part Two 2024 WEB-DL 1080p.mkv", "Dune Part Two", 2024)]
    [InlineData("Властелин колец Братство кольца 2001 BDRip.avi", "Властелин колец Братство кольца", 2001)]
    [InlineData("Star.Wars.Episode.4.A.New.Hope.1977.1080p.mkv", "Star Wars Episode 4 A New Hope", 1977)]
    [InlineData("Mr. Robot (2015) 720p.mkv", "Mr. Robot", 2015)]
    [InlineData("[Group] Movie Name (2010) [BDRip 1080p].mkv", "Movie Name", 2010)]
    [InlineData("Movie_Name_2012_DVDRip.avi", "Movie Name", 2012)]
    public void Movies(string file, string title, int? year)
    {
        var p = ReleaseNameParser.Parse(file);
        Assert.False(p.IsEpisode);
        Assert.Equal(title, p.Title);
        Assert.Equal(year, p.Year);
    }

    [Theory]
    [InlineData("Show.Name.S01E02.1080p.WEB-DL.mkv", "Show Name", 1, 2)]
    [InlineData("show.name.s2e10.720p.mkv", "show name", 2, 10)]
    [InlineData("Show Name - S03E04 - The Title.mkv", "Show Name", 3, 4)]
    [InlineData("Show Name 1x05 HDTV.avi", "Show Name", 1, 5)]
    [InlineData("Show.Name.2021.S01E01.mkv", "Show Name", 1, 1)]
    [InlineData("Show.Name.S01.E03.mkv", "Show Name", 1, 3)]
    [InlineData("Сериал.S01E07.WEB-DL.mkv", "Сериал", 1, 7)]
    public void Episodes(string file, string title, int season, int episode)
    {
        var p = ReleaseNameParser.Parse(file);
        Assert.True(p.IsEpisode);
        Assert.Equal(title, p.Title);
        Assert.Equal(season, p.Season);
        Assert.Equal(episode, p.Episode);
    }

    [Fact]
    public void Episode_TakesTheTitleAndSeasonFromTheFolders()
    {
        var p = ReleaseNameParser.Parse("E05 - Pilot.mkv", ["Season 2", "The Expanse (2015)"]);
        Assert.Equal(("The Expanse", 2, 5), (p.Title, p.Season, p.Episode));

        var bare = ReleaseNameParser.Parse("S01E03.mkv", ["Severance"]);
        Assert.Equal(("Severance", 1, 3), (bare.Title, bare.Season, bare.Episode));

        var inPack = ReleaseNameParser.Parse("03.mkv", ["Show.Name.S01.1080p.WEB-DL"]);
        Assert.False(inPack.IsEpisode); // a bare number is not an episode without an "E"
        var episodeInPack = ReleaseNameParser.Parse("E03.mkv", ["Show.Name.S01.1080p.WEB-DL"]);
        Assert.Equal(("Show Name", 1, 3), (episodeInPack.Title, episodeInPack.Season, episodeInPack.Episode));
    }

    [Theory]
    [InlineData("Season 2", 2)]
    [InlineData("S03", 3)]
    [InlineData("Сезон 1", 1)]
    [InlineData("4 сезон", 4)]
    [InlineData("Show.Name.S02.1080p.WEB-DL", 2)]
    [InlineData("Movies", null)]
    [InlineData("Seasons of Love", null)]
    public void SeasonFolders(string folder, int? season) =>
        Assert.Equal(season, ReleaseNameParser.SeasonOfFolder(folder));

    [Fact]
    public void Key_IgnoresCaseAndPunctuation() =>
        Assert.Equal(ReleaseNameParser.Key("The Show: Name"), ReleaseNameParser.Key("the.show name"));
}
