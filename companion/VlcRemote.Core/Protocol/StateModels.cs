namespace VlcRemote.Core.Protocol;

public sealed class VlcInfo
{
    /// <summary>Можно управлять (полный контроль). Подробности — <see cref="Status"/>.</summary>
    public bool Running { get; set; }
    /// <summary>Состояние VLC на ПК: одно из <see cref="VlcStatusNames"/>.</summary>
    public string? Status { get; set; }
    public string? Version { get; set; }
    public int? ApiVersion { get; set; }
    /// <summary>Уровень возможностей: 2 = компаньон.</summary>
    public int Level { get; set; } = 2;

    public VlcInfo Clone() => (VlcInfo)MemberwiseClone();
}

/// <summary>Значения <c>vlc.status</c> (protocol/README.md, «Состояние: state»).</summary>
public static class VlcStatusNames
{
    /// <summary>VLC не найден на ПК.</summary>
    public const string NotInstalled = "notInstalled";
    /// <summary>Процесса нет.</summary>
    public const string NotRunning = "notRunning";
    /// <summary>Запускается компаньоном.</summary>
    public const string Starting = "starting";
    /// <summary>Процесс есть, но веб-интерфейс не отвечает с паролем компаньона.</summary>
    public const string NoControl = "noControl";
    /// <summary>Полный контроль.</summary>
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
    /// <summary>Unix-время (мс) момента снятия состояния.</summary>
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
    /// <summary>Системная громкость 0..100.</summary>
    public int? Volume { get; set; }
    public bool? Muted { get; set; }
    /// <summary>ID устройства вывода VLC (<c>{0.0.0.00000000}.{guid}</c>), "" = по умолчанию.</summary>
    public string? AudioDevice { get; set; }
    public int? Display { get; set; }
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
}
