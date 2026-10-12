namespace VlcRemote.Core.Vlc;

/// <summary>Разобранный <c>status.json</c> (apiversion 3), см. protocol/vlc-http.md.</summary>
public sealed class VlcStatus
{
    public string State { get; set; } = "stopped";
    public long Time { get; set; }
    public long Length { get; set; }
    public double Position { get; set; }
    public double Rate { get; set; } = 1.0;
    public int Volume { get; set; }
    public bool Fullscreen { get; set; }
    public int CurrentPlId { get; set; } = -1;
    public double AudioDelay { get; set; }
    public double SubtitleDelay { get; set; }
    public string AspectRatio { get; set; } = "default";
    public bool Random { get; set; }
    public bool Loop { get; set; }
    public bool Repeat { get; set; }
    public int ApiVersion { get; set; }
    public string? Version { get; set; }
    public int SeekSec { get; set; }
    /// <summary>Отсутствует в состоянии <c>stopped</c>.</summary>
    public VlcInformation? Information { get; set; }

    public bool IsPlaying => State == "playing";
    public bool IsStopped => State == "stopped";
}

public sealed class VlcInformation
{
    public int Chapter { get; set; } = -1;
    public int ChapterCount { get; set; }
    public int Title { get; set; } = -1;
    public int TitleCount { get; set; }
    public IReadOnlyDictionary<string, string> Meta { get; set; } = new Dictionary<string, string>();
    public IReadOnlyList<VlcStream> Streams { get; set; } = [];
}

/// <summary>Элемент <c>information.category["Stream N"]</c>.</summary>
public sealed class VlcStream
{
    /// <summary>ID для <c>audio_track</c>/<c>subtitle_track</c>/<c>video_track</c> = <c>Original_ID</c> ?? N.</summary>
    public int Id { get; set; }
    /// <summary>"Video" | "Audio" | "Subtitle" (как отдаёт VLC; сравнивать без учёта регистра).</summary>
    public string Type { get; set; } = "";
    public string? Language { get; set; }
    public string? Description { get; set; }
    public string? Codec { get; set; }

    public bool IsAudio => Type.Equals("Audio", StringComparison.OrdinalIgnoreCase);
    public bool IsVideo => Type.Equals("Video", StringComparison.OrdinalIgnoreCase);
    public bool IsSubtitle => Type.Equals("Subtitle", StringComparison.OrdinalIgnoreCase);
}

public sealed class VlcPlaylistItem
{
    public int PlId { get; set; }
    public string Name { get; set; } = "";
    public string Uri { get; set; } = "";
    public long Duration { get; set; }
    public bool Current { get; set; }
}

public sealed class VlcBrowseEntry
{
    public string Type { get; set; } = "file";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Uri { get; set; } = "";
    public long? Size { get; set; }
    public long? ModificationTime { get; set; }
}

/// <summary>Элемент списка переменной VLC (<c>tracks.json</c>, уровень 1).</summary>
public sealed class VlcVarItem
{
    public string Id { get; set; } = "";
    public int? IntId { get; set; }
    public string Name { get; set; } = "";
}

public sealed class VlcVarList
{
    public string? Current { get; set; }
    public int? CurrentInt { get; set; }
    public IReadOnlyList<VlcVarItem> Items { get; set; } = [];
}

/// <summary>Ответ нашей Lua-страницы <c>requests/tracks.json</c>.</summary>
public sealed class VlcTracksPage
{
    public int VlcRemote { get; set; }
    public VlcVarList? Audio { get; set; }
    public VlcVarList? Spu { get; set; }
    public VlcVarList? Video { get; set; }
    public VlcVarList? Chapter { get; set; }
    public VlcVarList? Title { get; set; }
    public VlcVarList? StereoMode { get; set; }
    public VlcVarList? Aspect { get; set; }
    public VlcVarList? Crop { get; set; }
    public VlcVarList? Zoom { get; set; }
    public VlcVarList? Deinterlace { get; set; }
}

public sealed class VlcSetVarResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? Current { get; set; }
}

public sealed class VlcArt
{
    public byte[] Data { get; set; } = [];
    public string ContentType { get; set; } = "application/octet-stream";
}

public enum VlcTrackKind
{
    Audio,
    Subtitle,
    Video,
}
