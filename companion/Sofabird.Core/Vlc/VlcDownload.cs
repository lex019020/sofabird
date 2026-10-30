using System.Text.RegularExpressions;

namespace Sofabird.Core.Vlc;

/// <summary>
/// Where the companion downloads VLC media player from: VideoLAN's own server — the newest 3.0.x (the series the
/// companion drives), 64-bit installer, with the SHA-256 VideoLAN publishes next to it.
/// </summary>
public static partial class VlcDownload
{
    /// <summary>The publisher VLC's binaries and installers are signed by (Authenticode <c>O=</c>).</summary>
    public const string Publisher = "VideoLAN";

    /// <summary>The list of VLC releases, one folder per version.</summary>
    public static readonly Uri Index = new("https://download.videolan.org/pub/videolan/vlc/");

    [GeneratedRegex(@"href=""(?<v>3\.0\.\d+(?:\.\d+)?)/""")]
    private static partial Regex ReleaseFolder();

    [GeneratedRegex(@"^\s*(?<hash>[0-9a-fA-F]{64})\s+\*?(?<name>\S+)")]
    private static partial Regex ChecksumLine();

    /// <summary>The newest 3.0.x in <see cref="Index"/>'s listing (<c>3.0.24</c> over <c>3.0.9.2</c>); <c>null</c> — none.</summary>
    public static string? LatestVersion(string indexHtml)
    {
        string? best = null;
        Version? bestVersion = null;
        foreach (Match m in ReleaseFolder().Matches(indexHtml))
        {
            var text = m.Groups["v"].Value;
            if (!Version.TryParse(text, out var version) || (bestVersion is not null && version <= bestVersion)) continue;
            (best, bestVersion) = (text, version);
        }
        return best;
    }

    public static string InstallerName(string version) => $"vlc-{version}-win64.exe";

    public static Uri InstallerUrl(string version) => new(Index, $"{version}/win64/{InstallerName(version)}");

    /// <summary>VideoLAN's <c>.sha256</c> of the installer: served by VideoLAN itself, while the installer may come from a mirror.</summary>
    public static Uri ChecksumUrl(string version) => new(Index, $"{version}/win64/{InstallerName(version)}.sha256");

    /// <summary>The hash of a <c>.sha256</c> file (<c>hex  name</c>) when it is for <paramref name="fileName"/>; lower case.</summary>
    public static string? ParseChecksum(string text, string fileName)
    {
        var m = ChecksumLine().Match(text);
        return m.Success && string.Equals(m.Groups["name"].Value, fileName, StringComparison.OrdinalIgnoreCase)
            ? m.Groups["hash"].Value.ToLowerInvariant()
            : null;
    }
}
