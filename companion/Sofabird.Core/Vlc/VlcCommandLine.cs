using System.Globalization;
using System.Text;

namespace Sofabird.Core.Vlc;

/// <summary>How the companion starts VLC: the interfaces with the companion's ports and password, plus the user's options.</summary>
public sealed record VlcLaunchOptions(
    string HttpHost,
    int HttpPort,
    string HttpPassword,
    int RcPort,
    bool Fullscreen,
    string? ExtraArgs);

/// <summary>
/// The <c>vlc.exe</c> command line: building it for a launch by the companion, writing it to the log without the
/// password, and parsing the one of a running VLC (to restart it with the same file).
/// </summary>
public static class VlcCommandLine
{
    /// <summary>Arguments for <c>vlc.exe</c> (without the exe itself).</summary>
    /// <param name="fullscreenFollowsWindow">
    /// The companion places the window itself: <c>--qt-fullscreen-screennumber=-1</c> overrides a screen number from
    /// <c>vlcrc</c>, so fullscreen video opens on the monitor with the main window (Qt's screen order differs from ours).
    /// </param>
    public static IEnumerable<string> BuildLaunchArgs(VlcLaunchOptions options, string? uri, bool fullscreenFollowsWindow)
    {
        yield return "--extraintf";
        yield return "http:rc";
        yield return "--http-host";
        yield return options.HttpHost;
        yield return "--http-port";
        yield return options.HttpPort.ToString(CultureInfo.InvariantCulture);
        yield return "--http-password";
        yield return options.HttpPassword;
        yield return "--rc-host";
        yield return $"127.0.0.1:{options.RcPort.ToString(CultureInfo.InvariantCulture)}";
        yield return "--rc-quiet";
        if (fullscreenFollowsWindow)
            yield return "--qt-fullscreen-screennumber=-1";
        if (options.Fullscreen)
            yield return "--fullscreen";
        if (!string.IsNullOrWhiteSpace(options.ExtraArgs))
            foreach (var extra in SplitArgs(options.ExtraArgs))
                yield return extra;
        if (!string.IsNullOrEmpty(uri))
            yield return uri;
    }

    /// <summary>Command line for the log: the <c>--http-password</c> value is hidden.</summary>
    public static string RedactArgs(IEnumerable<string> args)
    {
        var sb = new StringBuilder();
        var redactNext = false;
        foreach (var a in args)
        {
            if (sb.Length > 0) sb.Append(' ');
            if (redactNext) { sb.Append("***"); redactNext = false; continue; }
            if (a == "--http-password") redactNext = true;
            sb.Append(a.Contains(' ', StringComparison.Ordinal) ? $"\"{a}\"" : a);
        }
        return sb.ToString();
    }

    /// <summary>Splits the user's extra options on spaces; double quotes group words (and are dropped).</summary>
    public static IEnumerable<string> SplitArgs(string s)
    {
        var sb = new StringBuilder();
        var quoted = false;
        foreach (var ch in s)
        {
            if (ch == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(ch) && !quoted)
            {
                if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
                continue;
            }
            sb.Append(ch);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

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
