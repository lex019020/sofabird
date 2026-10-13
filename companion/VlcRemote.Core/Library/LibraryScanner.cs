using VlcRemote.Core.History;
using VlcRemote.Core.Vlc;

namespace VlcRemote.Core.Library;

/// <summary>A video file found under a media folder.</summary>
/// <param name="Root">The media folder it was found under.</param>
/// <param name="Path">Its full path.</param>
public sealed record LibraryFile(string Root, string Path, long Size, DateTimeOffset Modified)
{
    public string Uri => LocalFileBrowser.ToUri(Path, isDir: false);
    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>The folders between the media folder and the file, nearest first.</summary>
    public IReadOnlyList<string> Folders
    {
        get
        {
            var relative = System.IO.Path.GetRelativePath(Root, System.IO.Path.GetDirectoryName(Path) ?? Root);
            if (relative == ".") return [];
            var parts = relative.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            Array.Reverse(parts);
            return parts;
        }
    }
}

/// <param name="Key">Stable id: the normalized title and the year.</param>
/// <param name="Files">Usually one; several are versions of the same movie (biggest first).</param>
public sealed record LibraryMovie(string Key, string Title, int? Year, IReadOnlyList<LibraryFile> Files);

public sealed record LibraryEpisode(int Season, int Episode, LibraryFile File);

/// <param name="Key">Stable id: the normalized title.</param>
/// <param name="Year">The earliest year the episodes' names carry, if any.</param>
public sealed record LibraryShow(string Key, string Title, int? Year, IReadOnlyList<LibraryEpisode> Episodes);

public sealed record LibraryScan(IReadOnlyList<LibraryMovie> Movies, IReadOnlyList<LibraryShow> Shows)
{
    public static LibraryScan Empty { get; } = new([], []);
}

/// <summary>
/// Finds the video files under the media folders and groups them into movies and series by their names
/// (<see cref="ReleaseNameParser"/>): a series is every episode with the same title, whatever folders they are in.
/// </summary>
public static class LibraryScanner
{
    /// <summary>Folders that hold bonus material, not the movie itself.</summary>
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "extras", "featurettes", "behind the scenes", "deleted scenes", "trailers", "samples", "sample", "bonus",
    };

    /// <summary>Walks the media folders (skips hidden, system and inaccessible entries). Blocking: call it off the UI thread.</summary>
    public static LibraryScan Scan(IEnumerable<string> roots, CancellationToken ct = default)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };
        var files = new List<LibraryFile>();
        foreach (var root in roots.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            foreach (var path in Directory.EnumerateFiles(root, "*", options))
            {
                ct.ThrowIfCancellationRequested();
                if (!HistoryRules.IsVideoFile(path)) continue;
                try
                {
                    var info = new FileInfo(path);
                    files.Add(new LibraryFile(root, path, info.Length, info.LastWriteTimeUtc));
                }
                catch (IOException) { } // gone or unreadable since it was listed
                catch (UnauthorizedAccessException) { }
            }
        }
        return Group(files);
    }

    /// <summary>Movies and series from a list of files (pure).</summary>
    public static LibraryScan Group(IEnumerable<LibraryFile> files)
    {
        var movies = new Dictionary<string, (string Title, int? Year, List<LibraryFile> Files)>();
        var shows = new Dictionary<string, (string Title, List<int> Years, List<LibraryEpisode> Episodes)>();
        foreach (var file in files)
        {
            var folders = file.Folders;
            if (folders.Any(SkippedFolders.Contains) || IsSample(file.FileName)) continue;
            var name = ReleaseNameParser.Parse(file.FileName, folders);
            if (name.IsEpisode)
            {
                var key = ReleaseNameParser.Key(name.Title);
                if (!shows.TryGetValue(key, out var show)) shows[key] = show = (name.Title, [], []);
                if (name.Year is int y) show.Years.Add(y);
                show.Episodes.Add(new LibraryEpisode(name.Season ?? 1, name.Episode!.Value, file));
            }
            else
            {
                name = WithFolderName(name, folders);
                var key = ReleaseNameParser.Key(name.Title) + "|" + name.Year;
                if (!movies.TryGetValue(key, out var movie)) movies[key] = movie = (name.Title, name.Year, []);
                movie.Files.Add(file);
            }
        }
        var byTitle = NaturalStringComparer.Instance;
        return new LibraryScan(
            movies.Select(m => new LibraryMovie(m.Key, m.Value.Title, m.Value.Year,
                    m.Value.Files.OrderByDescending(f => f.Size).ToList()))
                .OrderBy(m => m.Title, byTitle).ThenBy(m => m.Year).ToList(),
            shows.Select(s => new LibraryShow(s.Key, s.Value.Title, s.Value.Years.Count > 0 ? s.Value.Years.Min() : null,
                    s.Value.Episodes.OrderBy(e => e.Season).ThenBy(e => e.Episode)
                        .ThenBy(e => e.File.FileName, byTitle).ToList()))
                .OrderBy(s => s.Title, byTitle).ToList());
    }

    /// <summary>
    /// "Inception (2010)/movie.mkv": a file name without a year in a folder whose name has one — the folder names the
    /// movie.
    /// </summary>
    private static ParsedName WithFolderName(ParsedName name, IReadOnlyList<string> folders)
    {
        if (name.Year is not null || folders.Count == 0) return name;
        var folder = ReleaseNameParser.Parse(folders[0] + ".x");
        return folder.Year is not null && !folder.IsEpisode ? folder : name;
    }

    private static bool IsSample(string fileName) =>
        System.IO.Path.GetFileNameWithoutExtension(fileName)
            .Split([' ', '.', '-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Any(w => w.Equals("sample", StringComparison.OrdinalIgnoreCase));
}
