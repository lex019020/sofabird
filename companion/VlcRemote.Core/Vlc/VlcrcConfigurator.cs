using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace VlcRemote.Core.Vlc;

/// <summary>What the companion writes into <c>vlcrc</c> so that VLC, however it is opened, can be controlled.</summary>
public sealed record VlcrcSettings(int HttpPort, string HttpPassword, int RcPort);

public enum VlcrcResult
{
    /// <summary>The file is already configured — nothing was written.</summary>
    Unchanged,
    /// <summary>The file was changed (with a backup copy before the first change).</summary>
    Updated,
    /// <summary>There was no file (VLC has never been started) — a minimal one was created.</summary>
    Created,
    /// <summary>The file could not be parsed (not UTF-8) — left untouched.</summary>
    Skipped,
}

/// <summary>
/// Edits the text of <c>vlcrc</c> (VLC 3.0.x): sections like <c>[core] # core program</c>, keys as
/// commented-out defaults <c>#extraintf=</c>. A key is looked up only within its own section
/// (<c>[lua]</c> also has <c>#rc-host=</c>), the first <c>^#?key=</c> line is replaced (uncommented);
/// no such line — it is appended to the end of the section; no section — a section with the key is appended to the end of the file.
/// Line endings are preserved.
/// </summary>
public static class VlcrcEditor
{
    public const string CoreSection = "core";
    public const string LuaSection = "lua";
    public const string OldRcSection = "oldrc";
    public const string QtSection = "qt";

    /// <summary>Required values: (section, key, "current value → new value" function).</summary>
    private static IEnumerable<(string Section, string Key, Func<string?, string> Value)> Wanted(VlcrcSettings s) =>
    [
        (CoreSection, "extraintf", MergeExtraIntf),
        (CoreSection, "http-port", _ => s.HttpPort.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        (LuaSection, "http-password", _ => s.HttpPassword),
        (OldRcSection, "rc-host", _ => $"127.0.0.1:{s.RcPort}"),
        (OldRcSection, "rc-quiet", _ => "1"),
        // VLC's own "continue playback?" (0 = never, 1 = ask, 2 = always): the companion asks on the phone instead.
        (QtSection, "qt-continue", _ => "0"),
    ];

    /// <summary>
    /// Add <c>http</c> and <c>rc</c> to the list of extra interfaces, keeping the others
    /// (the separator is a colon, as in VLC).
    /// </summary>
    public static string MergeExtraIntf(string? existing)
    {
        var items = (existing ?? "")
            .Split([':', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        bool Has(params string[] names) => items.Any(i => names.Contains(i, StringComparer.OrdinalIgnoreCase));
        if (!Has("http", "luahttp")) items.Add("http");
        if (!Has("rc", "oldrc")) items.Add("rc");
        return string.Join(':', items);
    }

    /// <summary>Minimal <c>vlcrc</c> for a PC where VLC has never been started.</summary>
    public static string CreateMinimal(VlcrcSettings s, string newline = "\r\n")
    {
        var sb = new StringBuilder();
        foreach (var group in Wanted(s).GroupBy(w => w.Section))
        {
            if (sb.Length > 0) sb.Append(newline);
            sb.Append('[').Append(group.Key).Append(']').Append(newline);
            foreach (var w in group)
                sb.Append(w.Key).Append('=').Append(w.Value(null)).Append(newline);
        }
        return sb.ToString();
    }

    /// <summary>Apply the settings to the text (without BOM). Returns the new text; <paramref name="changed"/> — whether it differs.</summary>
    public static string Apply(string content, VlcrcSettings s, out bool changed)
    {
        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        // Lines without endings; a trailing empty line means the file ended with a line break.
        var lines = content.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var endsWithNewline = lines.Count > 0 && lines[^1].Length == 0;
        if (endsWithNewline) lines.RemoveAt(lines.Count - 1);

        changed = false;
        foreach (var (section, key, value) in Wanted(s))
        {
            var (start, end) = FindSection(lines, section);
            if (start < 0)
            {
                // No section — append it to the end of the file.
                if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
                lines.Add($"[{section}]");
                lines.Add($"{key}={value(null)}");
                changed = true;
                continue;
            }

            int commented = -1, active = -1;
            for (var i = start + 1; i < end; i++)
            {
                var t = lines[i].TrimStart();
                if (t.StartsWith(key + "=", StringComparison.Ordinal)) { active = i; break; }
                if (commented < 0 && t.StartsWith('#')
                    && t[1..].TrimStart().StartsWith(key + "=", StringComparison.Ordinal))
                    commented = i;
            }

            if (active >= 0)
            {
                var current = lines[active].TrimStart()[(key.Length + 1)..];
                var desired = value(current);
                if (current != desired)
                {
                    lines[active] = $"{key}={desired}";
                    changed = true;
                }
            }
            else if (commented >= 0)
            {
                lines[commented] = $"{key}={value(null)}";
                changed = true;
            }
            else
            {
                // Insert after the last non-empty line of the section.
                var insertAt = end;
                while (insertAt - 1 > start && lines[insertAt - 1].Trim().Length == 0) insertAt--;
                lines.Insert(insertAt, $"{key}={value(null)}");
                changed = true;
            }
        }

        var result = string.Join(newline, lines);
        if (endsWithNewline || lines.Count == 0) result += newline;
        return result;
    }

    /// <summary>Index of the section header and index of its end (the next header or the end of the file); -1 if absent.</summary>
    private static (int Start, int End) FindSection(List<string> lines, string name)
    {
        var start = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            var header = SectionName(lines[i]);
            if (header is null) continue;
            if (start >= 0) return (start, i);
            if (string.Equals(header, name, StringComparison.OrdinalIgnoreCase)) start = i;
        }
        return (start, start < 0 ? -1 : lines.Count);
    }

    private static string? SectionName(string line)
    {
        var t = line.TrimStart();
        if (!t.StartsWith('[')) return null;
        var close = t.IndexOf(']');
        return close > 1 ? t[1..close].Trim() : null;
    }
}

/// <summary>
/// The <c>vlcrc</c> file: reads it (UTF-8, the BOM is preserved), edits it via <see cref="VlcrcEditor"/>,
/// makes a <c>vlcrc.vlcremote-backup</c> copy before the first change, writes atomically (temp file + replace).
/// Call only when VLC is not running: VLC overwrites vlcrc from memory.
/// </summary>
public sealed class VlcrcConfigurator
{
    public const string BackupSuffix = ".vlcremote-backup";
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    private readonly ILogger _log;

    public VlcrcConfigurator(string path, ILogger? logger = null)
    {
        Path = path;
        _log = logger ?? NullLogger.Instance;
    }

    public string Path { get; }
    public string BackupPath => Path + BackupSuffix;

    /// <summary><c>%APPDATA%\vlc\vlcrc</c>.</summary>
    public static string DefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "vlc", "vlcrc");

    /// <summary>Read-only check: the file exists and already has the values <see cref="Ensure"/> would write.</summary>
    public bool IsConfigured(VlcrcSettings settings)
    {
        try
        {
            if (!File.Exists(Path)) return false;
            var bytes = File.ReadAllBytes(Path);
            var skip = bytes.AsSpan().StartsWith(Bom) ? 3 : 0;
            var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, skip, bytes.Length - skip);
            VlcrcEditor.Apply(text, settings, out var changed);
            return !changed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return false;
        }
    }

    public VlcrcResult Ensure(VlcrcSettings settings)
    {
        if (!File.Exists(Path))
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
            WriteAtomic(Encoding.UTF8.GetBytes(VlcrcEditor.CreateMinimal(settings)), bom: true);
            _log.LogInformation("Created {Path} with HTTP/RC interfaces for the companion", Path);
            return VlcrcResult.Created;
        }

        var bytes = File.ReadAllBytes(Path);
        var hasBom = bytes.AsSpan().StartsWith(Bom);
        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
        }
        catch (DecoderFallbackException)
        {
            _log.LogWarning("{Path} is not valid UTF-8; leaving it untouched", Path);
            return VlcrcResult.Skipped;
        }

        var updated = VlcrcEditor.Apply(text, settings, out var changed);
        if (!changed) return VlcrcResult.Unchanged;

        if (!File.Exists(BackupPath))
            File.Copy(Path, BackupPath);
        WriteAtomic(new UTF8Encoding(false).GetBytes(updated), hasBom);
        _log.LogInformation("Updated {Path}: VLC will start with HTTP/RC interfaces for the companion (backup: {Backup})", Path, BackupPath);
        return VlcrcResult.Updated;
    }

    private void WriteAtomic(byte[] content, bool bom)
    {
        var tmp = Path + ".vlcremote-tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            if (bom) fs.Write(Bom);
            fs.Write(content);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, Path, overwrite: true);
    }
}
