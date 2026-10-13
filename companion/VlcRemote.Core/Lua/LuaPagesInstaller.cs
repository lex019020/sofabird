using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace VlcRemote.Core.Lua;

/// <summary>
/// Устанавливает Lua-страницы уровня 1 (см. companion/lua/install.ps1): VLC берёт HTTP-страницы из
/// ПЕРВОЙ существующей папки (<c>%APPDATA%\vlc\lua\http</c>, затем Program Files), поэтому
/// пользовательская папка должна содержать копию штатных страниц плюс наши
/// <c>requests/tracks.json</c> и <c>requests/setvar.json</c>. Пути и содержимое страниц
/// инжектируются (компаньон берёт их из встроенных ресурсов, тесты — из файлов).
/// Запускать только при остановленном VLC — страницы регистрируются при старте интерфейса.
/// </summary>
public sealed class LuaPagesInstaller
{
    public const string VersionFileName = "vlcremote.version";
    private readonly IReadOnlyDictionary<string, byte[]> _pages;
    private readonly ILogger _log;

    /// <param name="stockHttpDir">Штатная папка VLC, например <c>C:\Program Files\VideoLAN\VLC\lua\http</c>.</param>
    /// <param name="targetHttpDir">Пользовательская папка, например <c>%APPDATA%\vlc\lua\http</c>.</param>
    /// <param name="pages">Наши страницы: имя файла в <c>requests/</c> → содержимое.</param>
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
    /// <summary>Хэш содержимого наших страниц — чтобы переустанавливать при их обновлении.</summary>
    public string PagesVersion { get; }

    /// <summary>Папка пользователя по умолчанию: <c>%APPDATA%\vlc\lua\http</c>.</summary>
    public static string DefaultTargetDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "vlc", "lua", "http");

    /// <summary>Установлены ли наши страницы для данной версии VLC и актуальной версии самих страниц.</summary>
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

    /// <summary>Скопировать штатную папку + наши страницы + файл версии. Идемпотентно.</summary>
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

    /// <summary>Удалить пользовательскую папку (VLC вернётся к штатным страницам).</summary>
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
