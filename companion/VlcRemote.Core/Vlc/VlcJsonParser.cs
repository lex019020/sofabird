using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VlcRemote.Core.Vlc;

/// <summary>
/// Parser of the JSON responses of the VLC 3.0.x HTTP interface, accounting for the quirks from protocol/vlc-http.md.
/// Parsed by hand via <see cref="JsonDocument"/>: field types vary (bool/0, []/{}),
/// category keys are localized.
/// </summary>
public static partial class VlcJsonParser
{
    [GeneratedRegex(@"(\d+)\s*$")]
    private static partial Regex TrailingNumber();

    public static VlcStatus ParseStatus(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ParseStatus(doc.RootElement);
    }

    public static VlcStatus ParseStatus(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new VlcException("status.json: root is not an object");

        var s = new VlcStatus
        {
            State = GetString(root, "state") ?? "stopped",
            Time = GetInt64(root, "time") ?? 0,
            Length = GetInt64(root, "length") ?? 0,
            Position = GetDouble(root, "position") ?? 0,
            Rate = GetDouble(root, "rate") ?? 1.0,
            Volume = (int)(GetInt64(root, "volume") ?? 0),
            Fullscreen = GetBoolLoose(root, "fullscreen"),
            CurrentPlId = (int)(GetInt64(root, "currentplid") ?? -1),
            AudioDelay = GetDouble(root, "audiodelay") ?? 0,
            SubtitleDelay = GetDouble(root, "subtitledelay") ?? 0,
            AspectRatio = GetString(root, "aspectratio") ?? "default",
            Random = GetBoolLoose(root, "random"),
            Loop = GetBoolLoose(root, "loop"),
            Repeat = GetBoolLoose(root, "repeat"),
            ApiVersion = (int)(GetInt64(root, "apiversion") ?? 0),
            Version = GetString(root, "version"),
            SeekSec = (int)(GetInt64(root, "seek_sec") ?? 10),
        };

        if (root.TryGetProperty("information", out var info) && info.ValueKind == JsonValueKind.Object)
            s.Information = ParseInformation(info);

        return s;
    }

    private static VlcInformation ParseInformation(JsonElement info)
    {
        var result = new VlcInformation
        {
            Chapter = (int)(GetInt64(info, "chapter") ?? -1),
            Title = (int)(GetInt64(info, "title") ?? -1),
            ChapterCount = CountArray(info, "chapters"),
            TitleCount = CountArray(info, "titles"),
        };

        if (info.TryGetProperty("category", out var category) && category.ValueKind == JsonValueKind.Object)
        {
            var meta = new Dictionary<string, string>(StringComparer.Ordinal);
            var streams = new List<VlcStreamInfo>();
            foreach (var prop in category.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                if (prop.Name.Equals("meta", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var m in prop.Value.EnumerateObject())
                        meta[m.Name] = ElementToString(m.Value);
                    continue;
                }

                // A stream category is recognized by the presence of the Type field, not by the word "Stream"
                // (the keys are localized).
                if (!prop.Value.TryGetProperty("Type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
                    continue;

                var stream = new VlcStreamInfo
                {
                    Type = typeEl.GetString() ?? "",
                    Language = GetString(prop.Value, "Language"),
                    Description = GetString(prop.Value, "Description"),
                    Codec = GetString(prop.Value, "Codec"),
                };

                int? id = null;
                if (prop.Value.TryGetProperty("Original_ID", out var orig))
                    id = ElementToInt(orig);
                if (id is null)
                {
                    var m = TrailingNumber().Match(prop.Name);
                    if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                        id = n;
                }
                if (id is null) continue; // a track without an ID cannot be selected
                stream.Id = id.Value;
                streams.Add(stream);
            }

            streams.Sort((a, b) => a.Id.CompareTo(b.Id));
            result.Meta = meta;
            result.Streams = streams;
        }

        return result;
    }

    public static IReadOnlyList<VlcPlaylistItem> ParsePlaylist(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var items = new List<VlcPlaylistItem>();
        CollectLeaves(doc.RootElement, items, isRoot: true);
        return items;
    }

    private static void CollectLeaves(JsonElement node, List<VlcPlaylistItem> items, bool isRoot)
    {
        if (node.ValueKind != JsonValueKind.Object) return;
        var type = GetString(node, "type");
        var name = GetString(node, "name") ?? "";
        if (type == "node")
        {
            // Ignore the Media Library node.
            if (!isRoot && name.Equals("Media Library", StringComparison.OrdinalIgnoreCase)) return;
            if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
                foreach (var child in children.EnumerateArray())
                    CollectLeaves(child, items, isRoot: false);
            return;
        }

        if (type == "leaf")
        {
            var idStr = GetString(node, "id") ?? ElementToString(node.TryGetProperty("id", out var idEl) ? idEl : default);
            if (!int.TryParse(idStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var plId)) return;
            items.Add(new VlcPlaylistItem
            {
                PlId = plId,
                Name = name,
                Uri = GetString(node, "uri") ?? "",
                Duration = GetInt64(node, "duration") ?? 0,
                Current = node.TryGetProperty("current", out var cur) && cur.ValueKind == JsonValueKind.String,
            });
        }
    }

    public static IReadOnlyList<VlcBrowseEntry> ParseBrowse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<VlcBrowseEntry>();
        if (!doc.RootElement.TryGetProperty("element", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var name = GetString(el, "name") ?? "";
            if (name == "..") continue; // the phone builds "up" itself
            list.Add(new VlcBrowseEntry
            {
                Type = GetString(el, "type") ?? "file",
                Name = name,
                Path = GetString(el, "path") ?? "",
                Uri = GetString(el, "uri") ?? "",
                Size = GetInt64(el, "size"),
                ModificationTime = GetInt64(el, "modification_time"),
            });
        }
        return list;
    }

    public static VlcTracksPage ParseTracksPage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new VlcException("tracks.json: root is not an object");
        return new VlcTracksPage
        {
            VlcRemote = (int)(GetInt64(root, "vlcremote") ?? 0),
            Audio = ParseVarList(root, "audio"),
            Spu = ParseVarList(root, "spu"),
            Video = ParseVarList(root, "video"),
            Chapter = ParseVarList(root, "chapter"),
            Title = ParseVarList(root, "title"),
            StereoMode = ParseVarList(root, "stereo_mode"),
            Aspect = ParseVarList(root, "aspect"),
            Crop = ParseVarList(root, "crop"),
            Zoom = ParseVarList(root, "zoom"),
            Deinterlace = ParseVarList(root, "deinterlace"),
        };
    }

    public static VlcSetVarResult ParseSetVar(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new VlcSetVarResult
        {
            Ok = GetBoolLoose(root, "ok"),
            Error = GetString(root, "err"),
            Current = root.TryGetProperty("current", out var c) ? ElementToString(c) : null,
        };
    }

    private static VlcVarList? ParseVarList(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Object) return null;
        var list = new VlcVarList();
        if (el.TryGetProperty("current", out var cur) && cur.ValueKind != JsonValueKind.Null)
        {
            list.Current = ElementToString(cur);
            list.CurrentInt = ElementToInt(cur);
        }
        var items = new List<VlcVarItem>();
        if (el.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in arr.EnumerateArray())
            {
                if (it.ValueKind != JsonValueKind.Object) continue;
                var idEl = it.TryGetProperty("id", out var i) ? i : default;
                items.Add(new VlcVarItem
                {
                    Id = ElementToString(idEl),
                    IntId = ElementToInt(idEl),
                    Name = GetString(it, "name") ?? "",
                });
            }
        }
        list.Items = items;
        return list;
    }

    // --- helpers ---

    private static string? GetString(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => ElementToString(el),
            _ => null,
        };
    }

    private static long? GetInt64(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.TryGetInt64(out var l) ? l : (long)Math.Round(el.GetDouble()),
            JsonValueKind.String => long.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : null,
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            _ => null,
        };
    }

    private static double? GetDouble(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.GetDouble(),
            JsonValueKind.String => double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null,
            _ => null,
        };
    }

    /// <summary><c>fullscreen</c> comes as a bool during playback and as 0 when stopped.</summary>
    private static bool GetBoolLoose(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var el)) return false;
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => el.TryGetDouble(out var d) && d != 0,
            JsonValueKind.String => el.GetString() is "true" or "1",
            _ => false,
        };
    }

    private static int CountArray(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el)) return 0;
        return el.ValueKind switch
        {
            JsonValueKind.Array => el.GetArrayLength(),
            JsonValueKind.Number => (int)(el.TryGetInt64(out var l) ? l : 0), // VLC 4: chapters_count
            _ => 0,
        };
    }

    internal static string ElementToString(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString() ?? "",
        JsonValueKind.Number => el.TryGetInt64(out var l) ? l.ToString(CultureInfo.InvariantCulture) : el.GetDouble().ToString(CultureInfo.InvariantCulture),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Undefined or JsonValueKind.Null => "",
        _ => el.GetRawText(),
    };

    internal static int? ElementToInt(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Number => el.TryGetInt32(out var i) ? i : (el.TryGetDouble(out var d) && Math.Abs(d - Math.Round(d)) < 1e-9 ? (int)Math.Round(d) : null),
        JsonValueKind.String => int.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : null,
        _ => null,
    };
}
