using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace VlcRemote.Core.Lua;

/// <summary>
/// Installs the level 1 Lua pages (see companion/lua/install.ps1): VLC takes the HTTP pages from
/// the FIRST existing folder (<c>%APPDATA%\vlc\lua\http</c>, then Program Files), so
/// the user folder must contain a copy of the stock pages plus our
/// <c>requests/tracks.json</c> and <c>requests/setvar.json</c>. The paths and the page contents
/// are injected (the companion takes them from embedded resources, the tests from files).
/// Run only while VLC is stopped — the pages are registered when the interface starts.
/// </summary>
public sealed class LuaPagesInstaller
{
    public const string VersionFileName = "vlcremote.version";
    private readonly IReadOnlyDictionary<string, byte[]> _pages;
    private readonly ILogger _log;

    /// <param name="stockHttpDir">VLC's stock folder, for example <c>C:\Program Files\VideoLAN\VLC\lua\http</c>.</param>
    /// <param name="targetHttpDir">The user folder, for example <c>%APPDATA%\vlc\lua\http</c>.</param>
    /// <param name="pages">Our pages: file name in <c>requests/</c> → contents.</param>
    public LuaPagesInstaller(string stockHttpDir, string targetHttpDir, IReadOnlyDictionary<string, byte[]> pages, ILogger? log = null)
    {
        if (pages.Count == 0) throw new ArgumentException("no pages to install", nameof(pages));
        StockHttpDir = stockHttpDir;
        TargetHttpDir = targetHttpDir;
        _pages = pages;
        _log = log ?? NullLogger.Instance;
        PagesVersion = ComputeVersion(pages);
    }

    public string StockHttpDir { get; }
    public string TargetHttpDir { get; }
    /// <summary>Hash of our pages' contents — to reinstall them when they are updated.</summary>
    public string PagesVersion { get; }

    /// <summary>Default user folder: <c>%APPDATA%\vlc\lua\http</c>.</summary>
    public static string DefaultTargetDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "vlc", "lua", "http");

    /// <summary>Whether our pages are installed for this VLC version and the current version of the pages themselves.</summary>
    public bool IsInstalled(string? vlcVersion)
    {
        var versionFile = Path.Combine(TargetHttpDir, VersionFileName);
        if (!File.Exists(versionFile)) return false;
        foreach (var page in _pages.Keys)
            if (!File.Exists(Path.Combine(TargetHttpDir, "requests", page))) return false;
        if (!File.Exists(Path.Combine(TargetHttpDir, "requests", "status.json"))) return false;

        var info = ReadVersionFile(versionFile);
        if (!info.TryGetValue("vlc", out var installedVlc) || !string.Equals(installedVlc, vlcVersion ?? "", StringComparison.Ordinal))
            return false;
        if (!info.TryGetValue("pages", out var pages) || pages != PagesVersion)
            return false;
        return true;
    }

    public bool NeedsInstall(string? vlcVersion) => Directory.Exists(StockHttpDir) && !IsInstalled(vlcVersion);

    /// <summary>Copy the stock folder + our pages + the version file. Idempotent.</summary>
    public void Install(string? vlcVersion)
    {
        if (!Directory.Exists(StockHttpDir))
            throw new DirectoryNotFoundException($"VLC stock Lua HTTP folder not found: {StockHttpDir}");

        _log.LogInformation("Installing Lua pages from {Stock} to {Target} (VLC {Version})", StockHttpDir, TargetHttpDir, vlcVersion);
        Directory.CreateDirectory(TargetHttpDir);
        CopyDirectory(StockHttpDir, TargetHttpDir);

        var requests = Path.Combine(TargetHttpDir, "requests");
        Directory.CreateDirectory(requests);
        foreach (var (name, bytes) in _pages)
            File.WriteAllBytes(Path.Combine(requests, name), bytes);

        File.WriteAllText(Path.Combine(TargetHttpDir, VersionFileName),
            $"vlc={vlcVersion}\npages={PagesVersion}\ninstalled={DateTimeOffset.Now:O}\n");
    }

    /// <summary>Delete the user folder (VLC falls back to the stock pages).</summary>
    public void Uninstall()
    {
        if (Directory.Exists(TargetHttpDir))
            Directory.Delete(TargetHttpDir, recursive: true);
    }

    private static void CopyDirectory(string source, string target)
    {
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(target, Path.GetRelativePath(source, file));
            File.Copy(file, dest, overwrite: true);
            File.SetAttributes(dest, FileAttributes.Normal);
        }
    }

    private static string ComputeVersion(IReadOnlyDictionary<string, byte[]> pages)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var name in pages.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            sha.AppendData(System.Text.Encoding.UTF8.GetBytes(name));
            sha.AppendData(pages[name]);
        }
        return Convert.ToHexStringLower(sha.GetHashAndReset())[..16];
    }

    private static Dictionary<string, string> ReadVersionFile(string path)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(path))
        {
            var idx = line.IndexOf('=');
            if (idx <= 0) continue;
            dict[line[..idx].Trim()] = line[(idx + 1)..].Trim();
        }
        return dict;
    }
}
