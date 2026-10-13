using System.Text.Json;
using System.Text.Json.Serialization;

namespace VlcRemote.Core.Protocol;

/// <summary>
/// Базовый класс всех сообщений протокола (protocol/README.md). Тип кодируется полем <c>t</c>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "t",
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization,
    IgnoreUnrecognizedTypeDiscriminators = false)]
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(WelcomeMessage), "welcome")]
[JsonDerivedType(typeof(ErrMessage), "err")]
[JsonDerivedType(typeof(OkMessage), "ok")]
[JsonDerivedType(typeof(CmdMessage), "cmd")]
[JsonDerivedType(typeof(SysMessage), "sys")]
[JsonDerivedType(typeof(BrowseMessage), "browse")]
[JsonDerivedType(typeof(EntriesMessage), "entries")]
[JsonDerivedType(typeof(GetStateMessage), "getState")]
[JsonDerivedType(typeof(GetPlaylistMessage), "getPlaylist")]
[JsonDerivedType(typeof(PlaylistMessage), "playlist")]
[JsonDerivedType(typeof(GetTracksMessage), "getTracks")]
[JsonDerivedType(typeof(TracksMessage), "tracks")]
[JsonDerivedType(typeof(GetAudioDevicesMessage), "getAudioDevices")]
[JsonDerivedType(typeof(AudioDevicesMessage), "audioDevices")]
[JsonDerivedType(typeof(DisplaysMessage), "displays")]
[JsonDerivedType(typeof(StateMessage), "state")]
[JsonDerivedType(typeof(EventMessage), "event")]
[JsonDerivedType(typeof(PingMessage), "ping")]
[JsonDerivedType(typeof(PongMessage), "pong")]
[JsonDerivedType(typeof(BeaconMessage), "beacon")]
public abstract class Message
{
    /// <summary>Идентификатор запроса; ответ несёт тот же <c>id</c>.</summary>
    [JsonPropertyOrder(-10)]
    public string? Id { get; set; }
}

public sealed class DeviceInfo
{
    public string? Name { get; set; }
    public string? Id { get; set; }
    /// <summary>Phone UI language as a BCP 47 tag (e.g. <c>ru-RU</c>); optional, used for <c>err.msg</c>.</summary>
    public string? Locale { get; set; }
}

public sealed class HelloMessage : Message
{
    public int V { get; set; }
    public DeviceInfo? Device { get; set; }
    public string? Token { get; set; }
    public string? PairCode { get; set; }
}

/// <summary>
/// Ответ на <c>hello</c>. Поле <c>id</c> здесь — стабильный GUID установки компаньона
/// (у <c>welcome</c> нет request-id), т.е. базовое <see cref="Message.Id"/> = GUID ПК.
/// </summary>
public sealed class WelcomeMessage : Message
{
    public int V { get; set; } = ProtocolConstants.Version;
    public string Name { get; set; } = "";
    public IReadOnlyList<string> Caps { get; set; } = [];
    public string? Token { get; set; }
    public VlcInfo Vlc { get; set; } = new();
}

public sealed class ErrMessage : Message
{
    public string Code { get; set; } = ErrorCodes.Internal;
    public string Msg { get; set; } = "";
}

public sealed class OkMessage : Message;

public sealed class CmdMessage : Message
{
    public string Name { get; set; } = "";
    public JsonElement? Args { get; set; }
}

public sealed class SysMessage : Message
{
    public string Name { get; set; } = "";
    public JsonElement? Args { get; set; }
}

public sealed class BrowseMessage : Message
{
    public string Uri { get; set; } = "";
}

public sealed class BrowseEntry
{
    public string Name { get; set; } = "";
    public string Uri { get; set; } = "";
    /// <summary>"dir" или "file".</summary>
    public string Type { get; set; } = "file";
    public long? Size { get; set; }
    public long? Modified { get; set; }
}

public sealed class EntriesMessage : Message
{
    public string Uri { get; set; } = "";
    public IReadOnlyList<BrowseEntry> Items { get; set; } = [];
}

public sealed class GetStateMessage : Message;

public sealed class GetPlaylistMessage : Message;

public sealed class PlaylistEntry
{
    public int PlId { get; set; }
    public string Name { get; set; } = "";
    public string Uri { get; set; } = "";
    public long DurationSec { get; set; }
    public bool Current { get; set; }
}

public sealed class PlaylistMessage : Message
{
    public IReadOnlyList<PlaylistEntry> Items { get; set; } = [];
}

public sealed class GetTracksMessage : Message;

public sealed class TracksMessage : Message
{
    public IReadOnlyList<TrackInfo> Audio { get; set; } = [];
    public IReadOnlyList<TrackInfo> Subtitle { get; set; } = [];
    public IReadOnlyList<TrackInfo> Video { get; set; } = [];
    public int? CurrentAudio { get; set; }
    public int? CurrentSubtitle { get; set; }
    public int? CurrentVideo { get; set; }

    public static TracksMessage From(TracksInfo tracks, string? id) => new()
    {
        Id = id,
        Audio = tracks.Audio,
        Subtitle = tracks.Subtitle,
        Video = tracks.Video,
        CurrentAudio = tracks.CurrentAudio,
        CurrentSubtitle = tracks.CurrentSubtitle,
        CurrentVideo = tracks.CurrentVideo,
    };
}

public sealed class GetAudioDevicesMessage : Message;

public sealed class AudioDeviceEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Current { get; set; }
}

public sealed class AudioDevicesMessage : Message
{
    public IReadOnlyList<AudioDeviceEntry> Items { get; set; } = [];
}

public sealed class DisplayBounds
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

public sealed class DisplayEntry
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public bool Primary { get; set; }
    public DisplayBounds Bounds { get; set; } = new();
}

public sealed class DisplaysMessage : Message
{
    public IReadOnlyList<DisplayEntry> Items { get; set; } = [];
}

public sealed class EventMessage : Message
{
    public string Name { get; set; } = "";
    /// <summary>Дополнительно для <c>inputChanged</c>: URI нового элемента.</summary>
    public string? Uri { get; set; }
    /// <summary>Дополнительно для <c>paired</c>: имя устройства.</summary>
    public string? Device { get; set; }
}

public sealed class PingMessage : Message;

public sealed class PongMessage : Message;

/// <summary>Ответ на UDP-маяк. Поле <c>id</c> (базовое <see cref="Message.Id"/>) — GUID ПК.</summary>
public sealed class BeaconMessage : Message
{
    public string Name { get; set; } = "";
    public int WsPort { get; set; }
    public int Protocol { get; set; } = ProtocolConstants.Version;
    public bool NeedsPairing { get; set; }
}

public static class ProtocolConstants
{
    public const int Version = 1;
    public const int DefaultWsPort = 47555;
    public const int DefaultBeaconPort = 47556;
    public const string DnsSdServiceType = "_vlcremote._tcp";
    public const string BeaconRequest = "VLCREMOTE_DISCOVER 1";
}

public static class ErrorCodes
{
    /// <summary>Телефон не сопряжён и кода в <c>hello</c> нет; компаньон показывает окно с кодом.</summary>
    public const string PairingRequired = "pairing_required";
    public const string Unauthorized = "unauthorized";
    public const string BadRequest = "bad_request";
    public const string VlcNotRunning = "vlc_not_running";
    public const string VlcError = "vlc_error";
    public const string Unsupported = "unsupported";
    public const string Internal = "internal";
}

public static class Caps
{
    public const string CurrentTrack = "currentTrack";
    public const string ChapterNames = "chapterNames";
    public const string AudioDevices = "audioDevices";
    public const string PushEvents = "pushEvents";
    public const string LaunchVlc = "launchVlc";
    public const string WindowControl = "windowControl";
    public const string Power = "power";
    public const string SystemVolume = "systemVolume";
    public const string Metadata = "metadata";
    public const string Bluetooth = "bluetooth";
}

public static class EventNames
{
    public const string VlcStarted = "vlcStarted";
    public const string VlcExited = "vlcExited";
    public const string InputChanged = "inputChanged";
    public const string EndReached = "endReached";
    public const string Paired = "paired";
}
