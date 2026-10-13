namespace VlcRemote.Core.Vlc;

/// <summary>Разбор командной строки запущенного <c>vlc.exe</c> (для перезапуска с тем же файлом).</summary>
public static class VlcCommandLine
{
    /// <summary>
    /// Файл/URI, с которым открыт VLC: последний аргумент, не являющийся опцией и похожий на медиа —
    /// абсолютный путь или URI со схемой (<c>file:///…</c>, <c>http://…</c>). Значения опций
    /// (<c>8080</c>, <c>http:rc</c>, <c>127.0.0.1:4212</c>) и флаги вроде <c>--started-from-file</c>
    /// пропускаются. <paramref name="argv"/>[0] — путь к exe.
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
            // vlc://quit и подобные служебные элементы не восстанавливаем.
            return !a.StartsWith("vlc://", StringComparison.OrdinalIgnoreCase);
        }
        // C:\… , C:/… , \\server\share\…
        if (a.Length >= 3 && char.IsAsciiLetter(a[0]) && a[1] == ':' && (a[2] == '\\' || a[2] == '/')) return true;
        if (a.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        return false;
    }
}
