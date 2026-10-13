using System.Globalization;
using System.Text.RegularExpressions;

namespace VlcRemote.Core.Vlc;

public enum RcStatusKind
{
    Unknown,
    NewInput,
    PlayState,
    PauseState,
    StopState,
    AudioVolume,
    NewRate,
    Time,
    Quit,
}

/// <summary>Asynchronous RC line <c>status change: ( … )</c>.</summary>
public sealed record RcStatusChange(RcStatusKind Kind, string Raw, string? Text, long? Number);

/// <summary>A block line <c>| id - text [*]</c>.</summary>
public sealed record RcListEntry(string Id, string Text, bool Current)
{
    public int? IntId => int.TryParse(Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;
}

public sealed record RcTrackEntry(int Id, string Name, string? Language, bool Current);

/// <summary>Parser of the text protocol of VLC's RC interface (modules/control/oldrc.c).</summary>
public static partial class RcParser
{
    [GeneratedRegex(@"^status change:\s*\(\s*(?<inner>.*?)\s*\)\s*(?::\s*(?<suffix>.*))?$")]
    private static partial Regex StatusChangeRegex();

    [GeneratedRegex(@"^(?<cmd>\S+): returned (?<code>-?\d+) \((?<msg>.*)\)\s*$")]
    private static partial Regex ReturnedRegex();

    [GeneratedRegex(@"^\+----\[\s*(?<name>.*?)\s*\]\s*$")]
    private static partial Regex BlockStartRegex();

    [GeneratedRegex(@"^\+----\[\s*end of\s+(?<name>.*?)\s*\]\s*$")]
    private static partial Regex BlockEndRegex();

    [GeneratedRegex(@"^\|\s?(?<id>\S*)\s*-\s(?<text>.*?)(?<cur>\s\*)?\s*$")]
    private static partial Regex ListEntryRegex();

    [GeneratedRegex(@"^(?<name>.*?)\s+-\s+\[(?<lang>[^\]]+)\]$")]
    private static partial Regex TrackLangRegex();

    public static bool IsStatusChange(string line) => line.StartsWith("status change:", StringComparison.Ordinal);

    public static bool TryParseStatusChange(string line, out RcStatusChange change)
    {
        change = null!;
        var m = StatusChangeRegex().Match(line);
        if (!m.Success) return false;
        var inner = m.Groups["inner"].Value;
        var kind = RcStatusKind.Unknown;
        string? text = null;
        long? number = null;

        var colon = inner.IndexOf(':');
        var key = colon >= 0 ? inner[..colon].Trim() : inner.Trim();
        var value = colon >= 0 ? inner[(colon + 1)..].Trim() : "";
        switch (key)
        {
            case "new input":
                kind = RcStatusKind.NewInput; text = value; break;
            case "play state":
                kind = RcStatusKind.PlayState; number = ParseLong(value); break;
            case "pause state":
                kind = RcStatusKind.PauseState; number = ParseLong(value); break;
            case "stop state":
                kind = RcStatusKind.StopState; number = ParseLong(value); break;
            case "audio volume":
                kind = RcStatusKind.AudioVolume; number = ParseLong(value); break;
            case "new rate":
                kind = RcStatusKind.NewRate; text = value; break;
            case "time":
                kind = RcStatusKind.Time; number = ParseLong(value.TrimEnd('s')); break;
            case "quit":
                kind = RcStatusKind.Quit; break;
        }
        change = new RcStatusChange(kind, line, text, number);
        return true;
    }

    /// <summary>A <c>cmd: returned N (msg)</c> line.</summary>
    public static bool TryParseReturned(string line, out string command, out int code, out string message)
    {
        var m = ReturnedRegex().Match(line);
        if (!m.Success) { command = ""; code = 0; message = ""; return false; }
        command = m.Groups["cmd"].Value;
        code = int.Parse(m.Groups["code"].Value, CultureInfo.InvariantCulture);
        message = m.Groups["msg"].Value;
        return true;
    }

    public static bool TryParseBlockStart(string line, out string name)
    {
        if (BlockEndRegex().IsMatch(line)) { name = ""; return false; }
        var m = BlockStartRegex().Match(line);
        name = m.Success ? m.Groups["name"].Value : "";
        return m.Success;
    }

    public static bool TryParseBlockEnd(string line, out string name)
    {
        var m = BlockEndRegex().Match(line);
        name = m.Success ? m.Groups["name"].Value : "";
        return m.Success;
    }

    /// <summary>Contents of the first <c>+----[ … ]</c> … <c>+----[ end of … ]</c> block (lines without the frame).</summary>
    public static IReadOnlyList<string> ExtractBlockLines(IEnumerable<string> lines)
    {
        var result = new List<string>();
        var inBlock = false;
        foreach (var line in lines)
        {
            if (!inBlock)
            {
                if (TryParseBlockStart(line, out _)) inBlock = true;
                continue;
            }
            if (TryParseBlockEnd(line, out _)) break;
            result.Add(line);
        }
        return result;
    }

    public static IReadOnlyList<RcListEntry> ParseListEntries(IEnumerable<string> lines)
    {
        var list = new List<RcListEntry>();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (!line.StartsWith('|')) continue;
            var m = ListEntryRegex().Match(line);
            if (!m.Success) continue;
            list.Add(new RcListEntry(m.Groups["id"].Value, m.Groups["text"].Value.Trim(), m.Groups["cur"].Success));
        }
        return list;
    }

    /// <summary>Parse the <c>adev</c> response: output devices (id "" = default).</summary>
    public static IReadOnlyList<RcListEntry> ParseAudioDevices(IEnumerable<string> responseLines) =>
        ParseListEntries(ExtractBlockLines(responseLines));

    /// <summary>Parse the <c>atrack</c>/<c>strack</c>/<c>vtrack</c> response: tracks with the language and the current one.</summary>
    public static IReadOnlyList<RcTrackEntry> ParseTracks(IEnumerable<string> responseLines)
    {
        var result = new List<RcTrackEntry>();
        foreach (var e in ParseListEntries(ExtractBlockLines(responseLines)))
        {
            if (e.IntId is not int id) continue;
            var name = e.Text;
            string? lang = null;
            var m = TrackLangRegex().Match(name);
            if (m.Success)
            {
                name = m.Groups["name"].Value.Trim();
                lang = m.Groups["lang"].Value.Trim();
            }
            result.Add(new RcTrackEntry(id, name, lang, e.Current));
        }
        return result;
    }

    private static long? ParseLong(string s) =>
        long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
}
