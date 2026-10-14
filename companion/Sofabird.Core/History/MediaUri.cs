using System.Globalization;
using System.Text;

namespace Sofabird.Core.History;

/// <summary>
/// File URIs of the watch history. VLC, .NET and the phone spell the same file differently
/// (<c>file:///D:/a%20b.mkv</c>, <c>file:///d:/a b.mkv</c>, different sets of escaped characters), so every URI is
/// normalized before it is used as a key: percent-decoded, then re-encoded with one fixed rule (UTF-8, upper-case hex),
/// drive letter and host in lower case. Only <c>file:</c> URIs are recorded (local disks and UNC shares).
/// </summary>
public static class MediaUri
{
    private const string Scheme = "file:";

    /// <summary>Normalized file URI, or <c>null</c> when <paramref name="uri"/> is not a file URI (a stream, a discâ€¦).</summary>
    public static string? Normalize(string? uri)
    {
        if (!TryParse(uri, out var host, out var path)) return null;
        var sb = new StringBuilder("file://");
        if (host.Length > 0) sb.Append(host.ToLowerInvariant());
        sb.Append('/');
        var segments = path.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            if (i > 0) sb.Append('/');
            var seg = segments[i];
            if (i == 0 && host.Length == 0 && IsDrive(seg))
            {
                sb.Append(char.ToLowerInvariant(seg[0])).Append(':');
                continue;
            }
            Escape(sb, seg);
        }
        return sb.ToString();
    }

    /// <summary>Windows path of a file URI (<c>D:\dir\x.mkv</c>, <c>\\host\share\x.mkv</c>); <c>null</c> for other URIs.</summary>
    public static string? ToLocalPath(string? uri)
    {
        if (!TryParse(uri, out var host, out var path)) return null;
        var local = path.Replace('/', '\\');
        if (host.Length > 0) return @"\\" + host + @"\" + local;
        if (local.Length >= 2 && IsDrive(local[..2])) local = char.ToUpperInvariant(local[0]) + local[1..];
        if (local.Length == 2) local += @"\";
        return local;
    }

    /// <summary>Normalized URI of the folder that holds the file (with a trailing slash).</summary>
    public static string? FolderOf(string? uri)
    {
        var n = Normalize(uri);
        if (n is null) return null;
        var slash = n.LastIndexOf('/');
        return slash < "file:///".Length - 1 ? null : n[..(slash + 1)];
    }

    /// <summary>Decoded file name (last path segment).</summary>
    public static string? FileName(string? uri)
    {
        if (!TryParse(uri, out _, out var path)) return null;
        var slash = path.LastIndexOf('/');
        var name = slash >= 0 ? path[(slash + 1)..] : path;
        return name.Length == 0 ? null : name;
    }

    /// <summary>Normalized URI of <paramref name="fileName"/> inside the folder <paramref name="folderUri"/>.</summary>
    public static string Combine(string folderUri, string fileName)
    {
        var sb = new StringBuilder(folderUri.EndsWith('/') ? folderUri : folderUri + "/");
        Escape(sb, fileName);
        return Normalize(sb.ToString()) ?? sb.ToString();
    }

    /// <summary>Same file? Case-insensitive like the Windows file system.</summary>
    public static bool Same(string? a, string? b)
    {
        var na = Normalize(a);
        return na is not null && string.Equals(na, Normalize(b), StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParse(string? uri, out string host, out string path)
    {
        host = "";
        path = "";
        if (string.IsNullOrWhiteSpace(uri)) return false;
        var s = uri.Trim();
        if (!s.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)) return false;
        s = s[Scheme.Length..];
        // A fragment or query never belongs to a file path (VLC escapes '#' and '?').
        var cut = s.IndexOfAny(['#', '?']);
        if (cut >= 0) s = s[..cut];
        s = s.Replace('\\', '/');
        if (s.StartsWith("//", StringComparison.Ordinal))
        {
            s = s[2..];
            if (s.StartsWith('/'))
            {
                s = s[1..]; // file:///D:/x â†’ D:/x
            }
            else
            {
                var slash = s.IndexOf('/');
                var h = slash < 0 ? s : s[..slash];
                s = slash < 0 ? "" : s[(slash + 1)..];
                if (!h.Equals("localhost", StringComparison.OrdinalIgnoreCase)) host = Unescape(h);
            }
        }
        else if (s.StartsWith('/'))
        {
            s = s.TrimStart('/'); // file:/D:/x
        }
        path = Unescape(s);
        if (host.Length == 0 && (path.Length < 2 || !IsDrive(path[..2]))) return false;
        if (host.Length > 0 && path.Length == 0) return false;
        return true;
    }

    private static bool IsDrive(string s) => s.Length == 2 && char.IsAsciiLetter(s[0]) && s[1] == ':';

    private static string Unescape(string s)
    {
        try { return Uri.UnescapeDataString(s); }
        catch (UriFormatException) { return s; }
    }

    /// <summary>Unreserved characters and sub-delimiters stay as they are, everything else is UTF-8 %XX.</summary>
    private static void Escape(StringBuilder sb, string segment)
    {
        Span<byte> buf = stackalloc byte[4];
        foreach (var rune in segment.EnumerateRunes())
        {
            if (rune.IsAscii && IsSafe((char)rune.Value))
            {
                sb.Append((char)rune.Value);
                continue;
            }
            var n = rune.EncodeToUtf8(buf);
            for (var i = 0; i < n; i++)
                sb.Append('%').Append(buf[i].ToString("X2", CultureInfo.InvariantCulture));
        }
    }

    private static bool IsSafe(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~' or '!' or '$' or '&' or '\'' or '(' or ')' or '*' or '+' or ',' or ';' or '=' or '@';
}
