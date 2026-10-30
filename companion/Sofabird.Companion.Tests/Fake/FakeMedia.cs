namespace Sofabird.Companion.Tests.Fake;

/// <summary>An elementary stream of a file: <c>Stream N</c> of <c>status.json</c>, an item of <c>tracks.json</c> and of RC <c>atrack</c>.</summary>
/// <param name="Id">The ES id VLC selects it by (<c>audio_track&amp;val=</c>).</param>
/// <param name="Type"><c>Video</c> | <c>Audio</c> | <c>Subtitle</c>, as VLC spells it.</param>
internal sealed record FakeTrack(int Id, string Type, string? Language, string Description, string Codec)
{
    public static FakeTrack Video(int id) => new(id, "Video", null, "Main / MPEG-H HEVC @ 8000 kbps", "MPEG-H Part2/HEVC (H.265) (hevc)");
    public static FakeTrack Audio(int id, string language, string description) => new(id, "Audio", language, description, "A52 Audio (aka AC3) (a52 )");
    public static FakeTrack Subtitle(int id, string language, string description) => new(id, "Subtitle", language, description, "Text subtitles with various tags (subt)");

    /// <summary>The name VLC gives it in variable lists: <c>Description - [Language]</c>.</summary>
    public string ListName => Language is null ? Description : $"{Description} - [{Language}]";
}

/// <summary>A media file on the fake PC.</summary>
/// <param name="Uri">The URI as VLC reports it (<c>file:///D:/…</c>).</param>
/// <param name="Title">The playlist name (VLC takes the title tag, else the file name).</param>
/// <param name="Length">Duration in seconds.</param>
/// <param name="EndsAt">Where playback really ends (a truncated file ends before <paramref name="Length"/>); <c>null</c> — at the end.</param>
/// <param name="DefaultSubtitle">Subtitle track VLC selects by itself; <c>-1</c> — none.</param>
internal sealed record FakeMedia(string Uri, string Title, long Length, IReadOnlyList<FakeTrack> Tracks, long? EndsAt = null, int DefaultSubtitle = -1)
{
    /// <summary>A movie with one video, two audio and two subtitle tracks.</summary>
    public static FakeMedia Movie(string uri, string title, long length) => new(uri, title, length,
    [
        FakeTrack.Video(0),
        FakeTrack.Audio(1, "English", "Original / AC3 5.1 @ 448 kbps"),
        FakeTrack.Audio(2, "Russian", "Dub / AC3 5.1 @ 448 kbps"),
        FakeTrack.Subtitle(3, "English", "Full / SRT"),
        FakeTrack.Subtitle(4, "Russian", "Full / SRT"),
    ]);

    public long End => EndsAt ?? Length;

    /// <summary>The decoded file name (<c>meta.filename</c>).</summary>
    public string FileName => global::System.Uri.UnescapeDataString(Uri[(Uri.LastIndexOf('/') + 1)..]);
}
