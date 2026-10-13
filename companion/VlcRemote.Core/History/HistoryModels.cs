using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VlcRemote.Core.History;

/// <summary>One watched file (protocol/README.md, "Watch history and resume").</summary>
public sealed class HistoryEntry
{
    /// <summary>Normalized file URI (<see cref="MediaUri.Normalize"/>): the key.</summary>
    public string Uri { get; set; } = "";
    /// <summary>File name; with <see cref="Size"/> a secondary key that survives a path change.</summary>
    public string Name { get; set; } = "";
    public long? Size { get; set; }
    /// <summary>VLC's title (metadata or file name).</summary>
    public string Title { get; set; } = "";
    public string FolderUri { get; set; } = "";
    /// <summary>Resume point, seconds (0 when finished).</summary>
    public long Position { get; set; }
    /// <summary>Duration, seconds (0 = unknown).</summary>
    public long Length { get; set; }
    public bool Finished { get; set; }
    /// <summary>Unix ms of the last playback.</summary>
    public long LastWatched { get; set; }
    public int PlayCount { get; set; }

    public HistoryEntry Clone() => (HistoryEntry)MemberwiseClone();
}

/// <summary>A remembered audio or subtitle track; track ids differ from file to file and are not stored.</summary>
public sealed class TrackPreference
{
    public string? Lang { get; set; }
    public string? Name { get; set; }
    public string? Codec { get; set; }

    public TrackPreference Clone() => (TrackPreference)MemberwiseClone();
}

/// <summary>The subtitle choice of a folder: a track, or <c>"off"</c> in the file.</summary>
[JsonConverter(typeof(SubtitleChoiceConverter))]
public sealed class SubtitleChoice
{
    public static SubtitleChoice Disabled { get; } = new() { Off = true };

    public bool Off { get; init; }
    public TrackPreference? Track { get; init; }

    public static SubtitleChoice Of(TrackPreference track) => new() { Track = track };
}

/// <summary>Track choices remembered for every file of one folder.</summary>
public sealed class FolderPreferences
{
    public TrackPreference? Audio { get; set; }
    public SubtitleChoice? Subtitle { get; set; }

    public FolderPreferences Clone() => new() { Audio = Audio?.Clone(), Subtitle = Subtitle };
}

/// <summary>Contents of <c>history.json</c>.</summary>
public sealed class HistoryFile
{
    public int Version { get; set; } = HistoryStore.SchemaVersion;
    public long Revision { get; set; }
    public List<HistoryEntry> Entries { get; set; } = [];
    public Dictionary<string, FolderPreferences> Folders { get; set; } = [];
}

/// <summary>Reads a subtitle choice written either as <c>"off"</c> or as a track object.</summary>
public sealed class SubtitleChoiceConverter : JsonConverter<SubtitleChoice>
{
    public override SubtitleChoice? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.String:
                return string.Equals(reader.GetString(), "off", StringComparison.OrdinalIgnoreCase)
                    ? SubtitleChoice.Disabled
                    : throw new JsonException("subtitle preference must be \"off\" or an object");
            case JsonTokenType.StartObject:
                var track = JsonSerializer.Deserialize(ref reader, HistoryJsonContext.Default.TrackPreference);
                return track is null ? null : SubtitleChoice.Of(track);
            default:
                throw new JsonException("subtitle preference must be \"off\" or an object");
        }
    }

    public override void Write(Utf8JsonWriter writer, SubtitleChoice value, JsonSerializerOptions options)
    {
        if (value.Off || value.Track is null) writer.WriteStringValue("off");
        else JsonSerializer.Serialize(writer, value.Track, HistoryJsonContext.Default.TrackPreference);
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(HistoryFile))]
[JsonSerializable(typeof(TrackPreference))]
public partial class HistoryJsonContext : JsonSerializerContext
{
    /// <summary>Same options plus relaxed escaping, so Cyrillic titles stay readable in the file.</summary>
    public static HistoryJsonContext File { get; } = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
}
