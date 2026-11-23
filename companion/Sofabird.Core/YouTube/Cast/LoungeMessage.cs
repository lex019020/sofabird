using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Sofabird.Core.YouTube.Cast;

/// <summary>
/// A message of YouTube's lounge channel: its array id (<see cref="Aid"/>, acknowledged with the next request), its name
/// (<c>setPlaylist</c>, <c>pause</c>, <c>c</c>, <c>S</c>, <c>noop</c>, …) and its arguments. Commands carry one object
/// whose values are strings (<c>{"newTime":"60"}</c>); the channel's own <c>c</c> and <c>S</c> carry plain values.
/// </summary>
public sealed record LoungeMessage(long Aid, string Name, IReadOnlyList<JsonElement> Args)
{
    /// <summary>A field of the command's object as a string (numbers too); <c>null</c> — absent.</summary>
    public string? Get(string key)
    {
        if (Args.Count == 0 || Args[0].ValueKind != JsonValueKind.Object || !Args[0].TryGetProperty(key, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    /// <summary>A field as a number of seconds or a volume (<c>"12.5"</c>); <c>null</c> — absent or not a number.</summary>
    public double? GetNumber(string key) =>
        double.TryParse(Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : null;

    /// <summary>The <paramref name="index"/>-th argument as a string (<c>c</c>: the session id; <c>S</c>: the gsessionid).</summary>
    public string? Arg(int index) =>
        index < Args.Count && Args[index].ValueKind == JsonValueKind.String ? Args[index].GetString() : null;
}

/// <summary>
/// Reads the lounge channel's responses: chunks of a length line and a JSON array of <c>[aid, [name, args…]]</c>
/// items, which may arrive split anywhere or several at once. The arrays are found by matching brackets (outside
/// strings) rather than by the length, which counts characters a decoder may see differently.
/// </summary>
public sealed class LoungeChunkReader
{
    private readonly StringBuilder _buffer = new();

    /// <summary>Adds received text; returns the messages completed by it, in order.</summary>
    public IReadOnlyList<LoungeMessage> Feed(string text)
    {
        _buffer.Append(text);
        var messages = new List<LoungeMessage>();
        while (TryTakeArray(out var json))
        {
            if (json is not null) Parse(json, messages);
        }
        return messages;
    }

    /// <summary>Parses a complete response (the session's first request answers in one piece).</summary>
    public static IReadOnlyList<LoungeMessage> ParseAll(string text) => new LoungeChunkReader().Feed(text);

    /// <summary>
    /// Takes the next complete top-level array off the buffer (<c>json</c> = <c>null</c> for skipped garbage);
    /// <c>false</c> — nothing complete yet.
    /// </summary>
    private bool TryTakeArray(out string? json)
    {
        json = null;
        var i = 0;
        while (i < _buffer.Length && char.IsWhiteSpace(_buffer[i])) i++;
        if (i == _buffer.Length)
        {
            _buffer.Clear();
            return false;
        }
        if (char.IsDigit(_buffer[i]))
        {
            // The length line: wait for its end, then drop it.
            var end = i;
            while (end < _buffer.Length && char.IsDigit(_buffer[end])) end++;
            if (end == _buffer.Length) return false;
            _buffer.Remove(0, end);
            return true;
        }
        if (_buffer[i] != '[')
        {
            _buffer.Remove(0, i + 1); // not ours: skip a character and look again
            return true;
        }
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var j = i; j < _buffer.Length; j++)
        {
            var c = _buffer[j];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c == '[') depth++;
            else if (c == ']' && --depth == 0)
            {
                json = _buffer.ToString(i, j - i + 1);
                _buffer.Remove(0, j + 1);
                return true;
            }
        }
        return false;
    }

    private static void Parse(string json, List<LoungeMessage> into)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                // [aid, ["name", args…]]
                if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() < 2) continue;
                var aidElement = item[0];
                var body = item[1];
                if (aidElement.ValueKind != JsonValueKind.Number || !aidElement.TryGetInt64(out var aid)) continue;
                if (body.ValueKind != JsonValueKind.Array || body.GetArrayLength() == 0 || body[0].ValueKind != JsonValueKind.String) continue;
                var args = body.EnumerateArray().Skip(1).Select(a => a.Clone()).ToList();
                into.Add(new LoungeMessage(aid, body[0].GetString()!, args));
            }
        }
        catch (JsonException)
        {
            // a chunk that is not a message list (the reply to a sent message: [1,7,0]) or broken: nothing in it
        }
    }
}
