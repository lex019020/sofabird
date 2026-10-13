namespace VlcRemote.Core.Vlc;

/// <summary>Parsing the command line of a running <c>vlc.exe</c> (to restart it with the same file).</summary>
public static class VlcCommandLine
{
    /// <summary>
    /// The file/URI VLC was opened with: the last argument that is not an option and looks like media —
    /// an absolute path or a URI with a scheme (<c>file:///…</c>, <c>http://…</c>). Option values
    /// (<c>8080</c>, <c>http:rc</c>, <c>127.0.0.1:4212</c>) and flags such as <c>--started-from-file</c>
    /// are skipped. <paramref name="argv"/>[0] is the exe path.
    /// </summary>
    public static string? ExtractMedia(IReadOnlyList<string> argv)
    {
        for (var i = argv.Count - 1; i >= 1; i--)
        {
            var a = argv[i].Trim();
            if (a.Length == 0 || a.StartsWith('-')) continue;
            if (IsMediaLike(a)) return a;
        }
        return null;
    }

    private static bool IsMediaLike(string a)
    {
        var scheme = a.IndexOf("://", StringComparison.Ordinal);
        if (scheme > 1)
        {
            // vlc://quit and similar service items are not restored.
            return !a.StartsWith("vlc://", StringComparison.OrdinalIgnoreCase);
        }
        // C:\… , C:/… , \\server\share\…
        if (a.Length >= 3 && char.IsAsciiLetter(a[0]) && a[1] == ':' && (a[2] == '\\' || a[2] == '/')) return true;
        if (a.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        return false;
    }
}
