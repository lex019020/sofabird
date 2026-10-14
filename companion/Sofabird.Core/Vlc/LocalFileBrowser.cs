using Sofabird.Core.Protocol;

namespace Sofabird.Core.Vlc;

/// <summary>
/// File browsing by the companion itself (System.IO), without VLC: the library works even when VLC is closed
/// or open without control. The URIs have the same format as VLC's <c>browse.json</c> (<c>file:///D:/dir/</c>,
/// <c>file://host/share/dir/</c>), so they can be passed to <c>in_play</c>/the VLC command line.
/// </summary>
public sealed class LocalFileBrowser
{
    private readonly Func<IEnumerable<string>> _roots;

    /// <param name="extraRoots">Extra roots (the MediaRoots setting), for example <c>\\nas\video</c>.</param>
    public LocalFileBrowser(Func<IEnumerable<string>>? extraRoots = null)
    {
        _roots = extraRoots ?? (() => Array.Empty<string>());
    }

    public const string RootUri = "file:///";

    public IReadOnlyList<BrowseEntry> Browse(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri) || uri == RootUri || uri == "file://" || uri == "file:")
            return ListRoots();

        var path = ToLocalPath(uri);
        var dir = new DirectoryInfo(path);
        if (!dir.Exists)
            throw new DirectoryNotFoundException($"Folder not found: {path}");

        var result = new List<BrowseEntry>();
        foreach (var d in Safe(() => dir.EnumerateDirectories()))
        {
            if (IsHidden(d)) continue;
            result.Add(new BrowseEntry { Name = d.Name, Uri = ToUri(d.FullName, isDir: true), Type = "dir", Modified = Unix(d) });
        }
        foreach (var f in Safe(() => dir.EnumerateFiles()))
        {
            if (IsHidden(f)) continue;
            long? size = null;
            try { size = f.Length; } catch (IOException) { }
            result.Add(new BrowseEntry { Name = f.Name, Uri = ToUri(f.FullName, isDir: false), Type = "file", Size = size, Modified = Unix(f) });
        }
        return result
            .OrderBy(e => e.Type == "dir" ? 0 : 1)
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private List<BrowseEntry> ListRoots()
    {
        var result = new List<BrowseEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in _roots())
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var full = root.Trim();
            if (!Directory.Exists(full) || !seen.Add(full.TrimEnd('\\', '/'))) continue;
            var name = new DirectoryInfo(full).Name;
            result.Add(new BrowseEntry { Name = string.IsNullOrEmpty(name) ? full : $"{name} ({full.TrimEnd('\\')})", Uri = ToUri(full, isDir: true), Type = "dir" });
        }
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is DriveType.CDRom or DriveType.NoRootDirectory or DriveType.Unknown) continue;
            bool ready;
            try { ready = drive.IsReady; } catch (IOException) { ready = false; }
            if (!ready) continue;
            string label = "";
            try { label = drive.VolumeLabel; } catch (IOException) { } catch (UnauthorizedAccessException) { }
            var letter = drive.Name.TrimEnd('\\');
            result.Add(new BrowseEntry
            {
                Name = string.IsNullOrEmpty(label) ? drive.Name : $"{label} ({letter})",
                Uri = ToUri(drive.RootDirectory.FullName, isDir: true),
                Type = "dir",
            });
        }
        return result;
    }

    /// <summary><c>file:///D:/a%20b/</c> → <c>D:\a b\</c>; <c>file://host/share/x/</c> → <c>\\host\share\x\</c>.</summary>
    public static string ToLocalPath(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || !u.IsFile)
        {
            // Not a URI — assume a plain path was passed.
            return uri;
        }
        return u.LocalPath;
    }

    /// <summary>Path → file URI in VLC's format (folders with a trailing slash).</summary>
    public static string ToUri(string path, bool isDir)
    {
        var p = path;
        if (isDir && !p.EndsWith('\\') && !p.EndsWith('/')) p += Path.DirectorySeparatorChar;
        return new Uri(p).AbsoluteUri;
    }

    private static bool IsHidden(FileSystemInfo info)
    {
        try { return (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 || info.Name.StartsWith('.') || info.Name.StartsWith('$'); }
        catch (IOException) { return true; }
    }

    private static long? Unix(FileSystemInfo info)
    {
        try { return new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds(); } catch (IOException) { return null; }
    }

    private static IEnumerable<T> Safe<T>(Func<IEnumerable<T>> enumerate)
    {
        // Folders without access are skipped instead of failing the whole list.
        IEnumerator<T> e;
        try { e = enumerate().GetEnumerator(); }
        catch (UnauthorizedAccessException) { yield break; }
        using (e)
        {
            while (true)
            {
                T current;
                try
                {
                    if (!e.MoveNext()) yield break;
                    current = e.Current;
                }
                catch (UnauthorizedAccessException) { continue; }
                catch (IOException) { yield break; }
                yield return current;
            }
        }
    }
}
