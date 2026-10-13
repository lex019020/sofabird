using System.Globalization;
using System.Text.RegularExpressions;

namespace VlcRemote.Core.Library;

/// <summary>What a video file's name says about it.</summary>
/// <param name="Title">The movie or the series, with the release noise removed ("The Lord of the Rings The Fellowship of the Ring").</param>
/// <param name="Year">The release year, when the name has one.</param>
/// <param name="Season">A series episode: the season (1 when the name has only an episode number).</param>
/// <param name="Episode">A series episode: its number in the season.</param>
public sealed record ParsedName(string Title, int? Year, int? Season, int? Episode)
{
    public bool IsEpisode => Episode is not null;
}

/// <summary>
/// Parses release-style file names ("The.Matrix.1999.2160p.UHD.BluRay.x265-GROUP.mkv",
/// "Show.Name.S01E02.1080p.WEB-DL.mkv") the way the PTN / guessit tools do: find the anchors (SxxEyy, the year, the
/// first quality tag), the title is what comes before the first one. Pure; the folder names fill in what the file name
/// lacks ("Show/Season 1/E02.mkv").
/// </summary>
public static partial class ReleaseNameParser
{
    /// <param name="fileName">The file name with its extension.</param>
    /// <param name="folders">The folders above the file, nearest first ("Season 1", "Show Name", …).</param>
    public static ParsedName Parse(string fileName, IReadOnlyList<string>? folders = null)
    {
        folders ??= [];
        var name = Normalize(Path.GetFileNameWithoutExtension(fileName));

        int? season = null, episode = null;
        var anchor = name.Length;
        if (SeasonEpisode().Match(name) is { Success: true } se)
        {
            season = Int(se.Groups["s"].Value);
            episode = Int(se.Groups["e"].Value);
            anchor = se.Index;
        }
        else if (CrossEpisode().Match(name) is { Success: true } x)
        {
            season = Int(x.Groups["s"].Value);
            episode = Int(x.Groups["e"].Value);
            anchor = x.Index;
        }
        else if (EpisodeOnly().Match(name) is { Success: true } e
                 && (e.Index == 0 || (folders.Count > 0 && SeasonOfFolder(folders[0]) is not null)))
        {
            // Only "E02 - Title" or a file in a season folder: "Star Wars Episode 4 1977" is a movie.
            episode = Int(e.Groups["e"].Value);
            season = folders.Count > 0 ? SeasonOfFolder(folders[0]) ?? 1 : 1;
            anchor = e.Index;
        }
        if (episode is null && SeasonToken().Match(name) is { Success: true } st && st.Index > 0)
            anchor = st.Index; // a season pack folder: "Show.Name.S01.1080p"

        // The year is the last one that does not start the name ("1917.2019.1080p" is "1917" from 2019, "2001 A Space
        // Odyssey 1968" is from 1968, "1917.mkv" has no year); an episode's year comes before SxxEyy.
        int? year = null;
        var years = Year().Matches(name).Where(y => y.Index > 0 && y.Index < anchor).ToList();
        if (years.Count > 0)
        {
            year = Int(years[^1].Value);
            anchor = years[^1].Index;
        }
        if (Tag().Match(name) is { Success: true } tag && tag.Index > 0) anchor = Math.Min(anchor, tag.Index);

        var title = CleanTitle(name[..anchor]);
        if (title.Length == 0) title = TitleFromFolders(folders, episode is not null);
        if (title.Length == 0) title = CleanTitle(name);
        return new ParsedName(title, year, season, episode);
    }

    /// <summary>
    /// The season a folder is for: "Season 2", "S02", "Сезон 2", "2 сезон", or a season pack "Show.Name.S02.1080p" → 2;
    /// any other folder → <c>null</c>.
    /// </summary>
    public static int? SeasonOfFolder(string folder)
    {
        var name = Normalize(folder);
        if (SeasonFolder().Match(name) is { Success: true } m) return Int(m.Groups["n"].Value);
        return SeasonToken().Match(name) is { Success: true } t ? Int(t.Groups["n"].Value) : null;
    }

    /// <summary>Only a season ("Season 1", "S01"), no title in it: the title is in the folder above.</summary>
    private static bool IsBareSeasonFolder(string folder) => SeasonFolder().IsMatch(Normalize(folder));

    /// <summary>Case- and punctuation-insensitive key: the same series in two folders, "The.Show" and "the show".</summary>
    public static string Key(string title) =>
        Spaces().Replace(NonWord().Replace(title.ToLowerInvariant(), " "), " ").Trim();

    private static string TitleFromFolders(IReadOnlyList<string> folders, bool episode)
    {
        foreach (var folder in folders)
        {
            if (IsBareSeasonFolder(folder)) continue; // "Season 1" is not the title; the folder above is
            var parsed = Parse(folder + ".x", []);
            if (parsed.Title.Length > 0) return parsed.Title;
            if (!episode) break;
        }
        return "";
    }

    private static string Normalize(string s)
    {
        // Dots and underscores separate words in release names; keep the dot inside "5.1", "H.264", "Mr. Robot" alike —
        // the tags are cut off anyway, and a title keeps reading naturally.
        s = s.Replace('_', ' ');
        if (!s.Contains(' ')) s = s.Replace('.', ' ');
        else s = DotsBetweenWords().Replace(s, " ");
        return Spaces().Replace(s, " ").Trim();
    }

    private static string CleanTitle(string s)
    {
        s = Brackets().Replace(s, " ");          // [Group], (Extended), {…}
        s = s.Replace('(', ' ').Replace(')', ' ');
        s = Spaces().Replace(s, " ").Trim(' ', '-', '.', ',', '–', '—', '[', ']');
        return s;
    }

    private static int Int(string s) => int.Parse(s, NumberStyles.None, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"(?i)\bS(?<s>\d{1,2})\s?[-.]?\s?E(?<e>\d{1,3})(?!\d)")]
    private static partial Regex SeasonEpisode();

    [GeneratedRegex(@"(?i)(?<![\dx])(?<s>\d{1,2})x(?<e>\d{2,3})(?![\dp])")]
    private static partial Regex CrossEpisode();

    [GeneratedRegex(@"(?i)(?:^|[\s\-\[(])(?:E|Ep|Episode|Серия)\s?(?<e>\d{1,3})(?!\d)")]
    private static partial Regex EpisodeOnly();

    [GeneratedRegex(@"(?<![\d])(?:19\d{2}|20\d{2})(?![\dp])")]
    private static partial Regex Year();

    [GeneratedRegex(@"(?i)\b(?:2160p|1080p|1080i|720p|576p|480p|4k|uhd|hdr10\+?|hdr|dv|dolby vision|blu-?ray|bdrip|bdremux|remux|brrip|web-?dl|web-?rip|webrip|hdtv|hdrip|dvdrip|dvd5|dvd9|x264|x265|h ?264|h ?265|hevc|avc|av1|xvid|divx|10bit|aac|ac3|dts|truehd|atmos|eac3|ddp?5 ?1|proper|repack|extended|unrated|remastered|director'?s cut|imax|multi|dub|mvo|avo)\b")]
    private static partial Regex Tag();

    [GeneratedRegex(@"(?i)^(?:(?:season|сезон|s)\s?(?<n>\d{1,2})|(?<n>\d{1,2})\s?(?:season|сезон))$")]
    private static partial Regex SeasonFolder();

    [GeneratedRegex(@"(?i)\b(?:S(?<n>\d{1,2})|(?:season|сезон)\s?(?<n>\d{1,2}))\b")]
    private static partial Regex SeasonToken();

    [GeneratedRegex(@"\[[^\]]*\]|\{[^}]*\}")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"(?<=\w)\.(?=\w)")]
    private static partial Regex DotsBetweenWords();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonWord();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
