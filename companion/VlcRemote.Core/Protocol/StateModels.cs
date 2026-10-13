namespace VlcRemote.Core.Protocol;

public sealed class VlcInfo
{
    /// <summary>Can be controlled (full control). Details in <see cref="Status"/>.</summary>
    public bool Running { get; set; }
    /// <summary>State of VLC on the PC: one of <see cref="VlcStatusNames"/>.</summary>
    public string? Status { get; set; }
    public string? Version { get; set; }
    public int? ApiVersion { get; set; }
    /// <summary>Capability level: 2 = companion.</summary>
    public int Level { get; set; } = 2;

    public VlcInfo Clone() => (VlcInfo)MemberwiseClone();
}

/// <summary>Values of <c>vlc.status</c> (protocol/README.md, "State: <c>state</c>").</summary>
public static class VlcStatusNames
{
    /// <summary>VLC was not found on the PC.</summary>
    public const string NotInstalled = "notInstalled";
    /// <summary>There is no process.</summary>
    public const string NotRunning = "notRunning";
    /// <summary>Being started by the companion.</summary>
    public const string Starting = "starting";
    /// <summary>The process exists, but the web interface does not answer with the companion's password.</summary>
    public const string NoControl = "noControl";
    /// <summary>Full control.</summary>
    public const string Running = "running";
}

public sealed class PlaybackInfo
{
    /// <summary>"playing" | "paused" | "stopped".</summary>
    public string State { get; set; } = "stopped";
    public long Time { get; set; }
    public long Length { get; set; }
    public double Position { get; set; }
    public double Rate { get; set; } = 1.0;
    /// <summary>Unix time (ms) when the state was captured.</summary>
    public long UpdatedAt { get; set; }
}

public sealed class ItemInfo
{
    public int PlId { get; set; } = -1;
    public string? Uri { get; set; }
    public string? Title { get; set; }
    public string? ArtUrl { get; set; }
}

public sealed class TrackInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Lang { get; set; }
    public string? Codec { get; set; }
}

public sealed class TracksInfo
{
    public IReadOnlyList<TrackInfo> Audio { get; set; } = [];
    public IReadOnlyList<TrackInfo> Subtitle { get; set; } = [];
    public IReadOnlyList<TrackInfo> Video { get; set; } = [];
    public int? CurrentAudio { get; set; }
    public int? CurrentSubtitle { get; set; }
    public int? CurrentVideo { get; set; }
}

public sealed class ChaptersInfo
{
    public int Index { get; set; } = -1;
    public int Count { get; set; }
    public IReadOnlyList<string>? Names { get; set; }
}

public sealed class TitlesInfo
{
    public int Index { get; set; } = -1;
    public int Count { get; set; }
}

public sealed class VolumeInfo
{
    /// <summary>0..512, 256 = 100 %.</summary>
    public int Value { get; set; }
    public bool Muted { get; set; }
}

public sealed class VideoInfo
{
    public bool Fullscreen { get; set; }
    public string Aspect { get; set; } = "default";
}

public sealed class DelaysInfo
{
    public double Audio { get; set; }
    public double Subtitle { get; set; }
}

public sealed class FlagsInfo
{
    public bool Random { get; set; }
    public bool Loop { get; set; }
    public bool Repeat { get; set; }
}

public sealed class SystemInfo
{
    /// <summary>System volume 0..100.</summary>
    public int? Volume { get; set; }
    public bool? Muted { get; set; }
    /// <summary>VLC output device ID (<c>{0.0.0.00000000}.{guid}</c>), "" = default.</summary>
    public string? AudioDevice { get; set; }
    /// <summary>Index (in <c>listDisplays</c>) of the monitor with the VLC window; <c>null</c> — no window.</summary>
    public int? Display { get; set; }
    /// <summary>Stable id of the monitor with the VLC window; <c>null</c> — no window.</summary>
    public string? DisplayId { get; set; }
    /// <summary>Stable id of the preferred monitor; <c>null</c> — the companion does not manage the VLC window.</summary>
    public string? PreferredDisplayId { get; set; }
    public bool? KeepAwake { get; set; }
}

public sealed class StateMessage : Message
{
    public VlcInfo Vlc { get; set; } = new();
    public PlaybackInfo Playback { get; set; } = new();
    public ItemInfo? Item { get; set; }
    public TracksInfo? Tracks { get; set; }
    public ChaptersInfo? Chapters { get; set; }
    public TitlesInfo? Titles { get; set; }
    public VolumeInfo Volume { get; set; } = new();
    public VideoInfo Video { get; set; } = new();
    public DelaysInfo Delays { get; set; } = new();
    public FlagsInfo Flags { get; set; } = new();
    public SystemInfo? System { get; set; }
    /// <summary>The resume question waiting for an answer (<c>cmd resume</c>); absent when there is none.</summary>
    public ResumeInfo? Resume { get; set; }
    /// <summary>The offered next episode (<c>cmd open</c> / <c>cmd dismissNext</c>); absent when there is none.</summary>
    public NextInfo? Next { get; set; }
    /// <summary>Watch history revision (capability <c>history</c>).</summary>
    public HistoryInfo? History { get; set; }
}

public sealed class ResumeInfo
{
    public string Uri { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>Resume point, seconds.</summary>
    public long Position { get; set; }
    public long Length { get; set; }
    public bool Pending { get; set; } = true;
}

public sealed class NextInfo
{
    public string Uri { get; set; } = "";
    public string Title { get; set; } = "";
}

public sealed class HistoryInfo
{
    /// <summary>Changes whenever the history changes, so phones can refresh their lists.</summary>
    public long Revision { get; set; }
}
