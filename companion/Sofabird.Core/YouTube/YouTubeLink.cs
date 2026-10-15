using System.Globalization;
using System.Text.RegularExpressions;

namespace Sofabird.Core.YouTube;

/// <summary>A YouTube video to show on the TV: its id, an optional playlist and start time.</summary>
public sealed record YouTubeVideo(string VideoId, string? PlaylistId = null, int? StartSeconds = null)
{
    /// <summary>The canonical watch page, built from the parsed parts only (never the phone's raw text).</summary>
    public string WatchUrl
    {
        get
        {
            var url = "https://www.youtube.com/watch?v=" + VideoId;
            if (PlaylistId is not null) url += "&list=" + PlaylistId;
            if (StartSeconds is > 0) url += "&t=" + StartSeconds.Value.ToString(CultureInfo.InvariantCulture) + "s";
            return url;
        }
    }
}

/// <summary>
/// Finds a YouTube video link in what a phone shares or pastes (the YouTube app shares "Title https://youtu.be/ID?si=…")
/// and keeps only the video id, playlist id and start time: the companion opens nothing but a YouTube watch page.
/// </summary>
public static partial class YouTubeLink
{
    private static readonly string[] Hosts = ["youtube.com", "www.youtube.com", "m.youtube.com", "music.youtube.com", "youtu.be", "www.youtu.be"];

    public static YouTubeVideo? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (Match m in UrlPattern().Matches(text))
        {
            var raw = m.Value;
            if (!raw.Contains("://", StringComparison.Ordinal)) raw = "https://" + raw;
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) continue;
            if (!Hosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)) continue;
            var query = ParseQuery(uri.Query);
            var id = VideoIdFrom(uri, query);
            if (id is null) continue;
            var list = query.TryGetValue("list", out var l) && ListPattern().IsMatch(l) ? l : null;
            var start = query.TryGetValue("t", out var t) ? ParseTime(t)
                : query.TryGetValue("start", out var s) ? ParseTime(s) : null;
            return new YouTubeVideo(id, list, start);
        }
        return null;
    }

    private static string? VideoIdFrom(Uri uri, Dictionary<string, string> query)
    {
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? id;
        if (uri.Host.EndsWith("youtu.be", StringComparison.OrdinalIgnoreCase))
            id = segments.Length > 0 ? segments[0] : null;
        else if (segments.Length >= 2 && segments[0] is "shorts" or "live" or "embed" or "v")
            id = segments[1];
        else
            id = query.TryGetValue("v", out var v) ? v : null;
        return id is not null && IdPattern().IsMatch(id) ? id : null;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0) continue;
            result.TryAdd(Uri.UnescapeDataString(pair[..eq]), Uri.UnescapeDataString(pair[(eq + 1)..]));
        }
        return result;
    }

    /// <summary><c>90</c>, <c>90s</c>, <c>1m30s</c>, <c>1h2m3s</c>.</summary>
    internal static int? ParseTime(string value)
    {
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var plain)) return plain;
        var m = TimePattern().Match(value);
        if (!m.Success || m.Length == 0) return null;
        int Part(string name) => m.Groups[name].Success ? int.Parse(m.Groups[name].Value, CultureInfo.InvariantCulture) : 0;
        return Part("h") * 3600 + Part("m") * 60 + Part("s");
    }

    [GeneratedRegex(@"(?:https?://)?(?:www\.|m\.|music\.)?(?:youtube\.com|youtu\.be)/[^\s<>""']+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[A-Za-z0-9_-]{2,64}$")]
    private static partial Regex ListPattern();

    [GeneratedRegex(@"^(?:(?<h>\d+)h)?(?:(?<m>\d+)m)?(?:(?<s>\d+)s)?$")]
    private static partial Regex TimePattern();
}
