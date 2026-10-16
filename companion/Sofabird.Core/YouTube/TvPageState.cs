using System.Text.Json;

namespace Sofabird.Core.YouTube;

/// <summary>
/// What the TV extension reports about the YouTube page (<c>{"t":"state", …}</c> from <c>content.js</c>): the video,
/// the position at <see cref="ReceivedAt"/>, whether an ad plays. <see cref="Watching"/> is <c>false</c> off a watch page.
/// </summary>
public sealed record TvPageState
{
    public bool Watching { get; init; }
    public string? VideoId { get; init; }
    public string? Title { get; init; }
    public string? Channel { get; init; }
    /// <summary>Seconds, of the ad while one plays.</summary>
    public double Time { get; init; }
    public double Duration { get; init; }
    public bool Playing { get; init; }
    public bool Ended { get; init; }
    public bool Ad { get; init; }
    public bool AdSkippable { get; init; }
    public bool CanNext { get; init; }
    public bool CanPrevious { get; init; }
    public bool Fullscreen { get; init; }
    public DateTime ReceivedAt { get; init; }

    /// <summary>A line from the extension; <c>null</c> for anything that is not a state message.</summary>
    public static TvPageState? Parse(string json, DateTime receivedAt)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Str(root, "t") != "state") return null;
            return new TvPageState
            {
                Watching = Bool(root, "watching"),
                VideoId = Str(root, "videoId"),
                Title = Str(root, "title"),
                Channel = Str(root, "channel"),
                Time = Num(root, "time"),
                Duration = Num(root, "duration"),
                Playing = Bool(root, "playing"),
                Ended = Bool(root, "ended"),
                Ad = Bool(root, "ad"),
                AdSkippable = Bool(root, "adSkippable"),
                CanNext = Bool(root, "canNext"),
                CanPrevious = Bool(root, "canPrevious"),
                Fullscreen = Bool(root, "fullscreen"),
                ReceivedAt = receivedAt,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The position at <paramref name="now"/>: moved on since it was reported while playing, never past the end.</summary>
    public double TimeAt(DateTime now)
    {
        var t = Time;
        if (Playing && now > ReceivedAt) t += (now - ReceivedAt).TotalSeconds;
        return Duration > 0 ? Math.Min(t, Duration) : t;
    }

    /// <summary>
    /// Whether phones need a new state: anything but the position changed, or the position jumped (a seek) more than
    /// <paramref name="tolerance"/> seconds from where it would be by now.
    /// </summary>
    public bool DiffersFrom(TvPageState? previous, double tolerance = 2)
    {
        if (previous is null) return true;
        if (this with { Time = 0, ReceivedAt = default } != previous with { Time = 0, ReceivedAt = default }) return true;
        return Math.Abs(Time - previous.TimeAt(ReceivedAt)) > tolerance;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : 0;
}
