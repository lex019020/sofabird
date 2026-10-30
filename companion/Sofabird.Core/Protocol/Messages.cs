using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sofabird.Core.Protocol;

/// <summary>
/// Base class of all protocol messages (protocol/README.md). The type is encoded in the <c>t</c> field.
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
[JsonDerivedType(typeof(GetHistoryMessage), "getHistory")]
[JsonDerivedType(typeof(HistoryMessage), "history")]
[JsonDerivedType(typeof(HistoryUpdateMessage), "historyUpdate")]
[JsonDerivedType(typeof(HistoryClearMessage), "historyClear")]
[JsonDerivedType(typeof(GetLibraryMessage), "getLibrary")]
[JsonDerivedType(typeof(LibraryMessage), "library")]
[JsonDerivedType(typeof(GetShowMessage), "getShow")]
[JsonDerivedType(typeof(ShowMessage), "show")]
public abstract class Message
{
    /// <summary>Request identifier; the response carries the same <c>id</c>.</summary>
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
/// Response to <c>hello</c>. The <c>id</c> field here is the stable installation GUID of the companion
/// (<c>welcome</c> has no request id), i.e. the base <see cref="Message.Id"/> = the PC GUID.
/// </summary>
public sealed class WelcomeMessage : Message
{
    public int V { get; set; } = ProtocolConstants.Version;
    public string Name { get; set; } = "";
    public IReadOnlyList<string> Caps { get; set; } = [];
    public string? Token { get; set; }
    public VlcInfo Vlc { get; set; } = new();
    /// <summary>Bluetooth adapter address (<c>AA:BB:CC:DD:EE:FF</c>); absent when there is no adapter or the radio is off.</summary>
    public string? Bt { get; set; }
    /// <summary>
    /// MAC addresses (<c>AA:BB:CC:DD:EE:FF</c>, wired first) of the connected adapters that will wake the PC from sleep:
    /// a phone wakes it with them (Wake-on-LAN). Empty — it cannot be woken; absent — not known yet.
    /// </summary>
    public IReadOnlyList<string>? Macs { get; set; }
    /// <summary>The encrypted WebSocket (<c>wss</c>): its port and the certificate's SHA-256 a phone pins; absent without one.</summary>
    public TlsInfo? Tls { get; set; }
}

/// <summary>
/// The encrypted WebSocket: <c>wss://host:Port/ws</c> with a self-signed certificate whose SHA-256 (DER, hex) is
/// <see cref="Sha256"/>; a phone pins it at pairing and accepts no other.
/// </summary>
public sealed class TlsInfo
{
    public int Port { get; set; }
    public string Sha256 { get; set; } = "";
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
    /// <summary>"dir" or "file".</summary>
    public string Type { get; set; } = "file";
    public long? Size { get; set; }
    public long? Modified { get; set; }
    /// <summary>Watched part 0..1 (1 when finished); only for files in the watch history.</summary>
    public double? Progress { get; set; }
    /// <summary>Watched to the end (less than 5% left) or marked as watched; only for files in the watch history.</summary>
    public bool? Finished { get; set; }
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
    /// <summary>Stable monitor id (<c>monitorDevicePath</c>, or the GDI device name when it is unknown).</summary>
    public string Id { get; set; } = "";
    /// <summary>Friendly name from EDID, or a fallback ("Display 2"). See <see cref="NameText"/>.</summary>
    public string Name { get; set; } = "";
    /// <summary><c>internal</c> (laptop panel), <c>external</c> (HDMI/DP/…) or <c>unknown</c>.</summary>
    public string? Kind { get; set; }
    public bool Primary { get; set; }
    /// <summary>The VLC window is on this monitor now.</summary>
    public bool Current { get; set; }
    /// <summary>VLC opens on this monitor (the preferred one).</summary>
    public bool Preferred { get; set; }
    public DisplayBounds Bounds { get; set; } = new();

    /// <summary>Localizable fallback name; the session renders it into <see cref="Name"/> in the phone's language.</summary>
    [JsonIgnore]
    public Localization.LocalizableText? NameText { get; set; }
}

public sealed class DisplaysMessage : Message
{
    public IReadOnlyList<DisplayEntry> Items { get; set; } = [];
    /// <summary>Id of the preferred monitor (may be not connected right now); <c>null</c> — the companion does not manage the VLC window.</summary>
    public string? PreferredId { get; set; }

    /// <summary>Render localizable names (<see cref="DisplayEntry.NameText"/>) in <paramref name="culture"/>.</summary>
    public void Localize(System.Globalization.CultureInfo? culture)
    {
        foreach (var item in Items)
            if (item.NameText is { } text) item.Name = text.Render(culture);
    }
}

public sealed class EventMessage : Message
{
    public string Name { get; set; } = "";
    /// <summary>Additionally for <c>inputChanged</c>: the URI of the new item.</summary>
    public string? Uri { get; set; }
    /// <summary>Additionally for <c>paired</c>: the device name.</summary>
    public string? Device { get; set; }
    /// <summary><c>resumePrompt</c> / <c>nextEpisode</c>: title of the file.</summary>
    public string? Title { get; set; }
    /// <summary><c>resumePrompt</c>: resume point, seconds.</summary>
    public long? Position { get; set; }
    /// <summary><c>resumePrompt</c>: duration, seconds.</summary>
    public long? Length { get; set; }
    /// <summary><c>resumePrompt</c>: the question is waiting for an answer.</summary>
    public bool? Pending { get; set; }
}

/// <summary><c>getHistory</c>: <c>filter</c> = <c>continue</c> | <c>all</c>.</summary>
public sealed class GetHistoryMessage : Message
{
    public string? Filter { get; set; }
    public int? Limit { get; set; }
    public int? Offset { get; set; }
}

public sealed class HistoryItem
{
    public string Uri { get; set; } = "";
    public string Title { get; set; } = "";
    public string Name { get; set; } = "";
    public string FolderUri { get; set; } = "";
    public long Position { get; set; }
    public long Length { get; set; }
    public double Progress { get; set; }
    public bool Finished { get; set; }
    public long LastWatched { get; set; }
    public int PlayCount { get; set; }
    public string? ArtUrl { get; set; }
    /// <summary>The next episode of a series (in the <c>continue</c> list); absent otherwise.</summary>
    public bool? Next { get; set; }
    /// <summary><c>youtube</c> — a video played in the TV browser (<see cref="Uri"/> is its watch page); absent — a file.</summary>
    public string? Kind { get; set; }
    /// <summary>The YouTube channel of a <c>youtube</c> item.</summary>
    public string? Channel { get; set; }
}

public sealed class HistoryMessage : Message
{
    /// <summary>Number of items matching the filter (before paging).</summary>
    public int Total { get; set; }
    public IReadOnlyList<HistoryItem> Items { get; set; } = [];
}

/// <summary><c>historyUpdate</c>: mark watched / unwatched (<c>finished</c>) or <c>remove</c> an entry.</summary>
public sealed class HistoryUpdateMessage : Message
{
    public string Uri { get; set; } = "";
    public bool? Finished { get; set; }
    public bool? Remove { get; set; }
}

public sealed class HistoryClearMessage : Message;

/// <summary><c>getLibrary</c>: the movies and series of the media folders (<c>library</c> capability).</summary>
public sealed class GetLibraryMessage : Message;

/// <summary>A video file of the library.</summary>
public sealed class MediaFile
{
    public string Uri { get; set; } = "";
    /// <summary>File name.</summary>
    public string Name { get; set; } = "";
    public long Size { get; set; }
    /// <summary>Watched fraction 0..1 from the history; absent when never played.</summary>
    public double? Progress { get; set; }
    public bool? Finished { get; set; }
}

/// <summary>Fields a movie and a series share.</summary>
public abstract class LibraryTitle
{
    /// <summary>Stable id within the PC's library.</summary>
    public string Key { get; set; } = "";
    /// <summary>TMDB's title in the PC's language, or the one parsed from the file name.</summary>
    public string Title { get; set; } = "";
    public int? Year { get; set; }
    public string? Overview { get; set; }
    /// <summary>Full image URL (TMDB, w342); absent when not found.</summary>
    public string? Poster { get; set; }
    /// <summary>Full image URL (TMDB, w1280).</summary>
    public string? Backdrop { get; set; }
    /// <summary>TMDB user rating 0..10.</summary>
    public double? Rating { get; set; }
}

public sealed class MovieItem : LibraryTitle
{
    /// <summary>Usually one; several are versions of the same movie, the biggest first.</summary>
    public IReadOnlyList<MediaFile> Files { get; set; } = [];
}

public sealed class ShowItem : LibraryTitle
{
    public int Episodes { get; set; }
    /// <summary>Episodes not finished yet (history).</summary>
    public int Unwatched { get; set; }
}

public sealed class LibraryMessage : Message
{
    /// <summary>Same as <c>state.library.revision</c> when the list was built.</summary>
    public long Revision { get; set; }
    /// <summary>A scan or a metadata lookup is in progress; the list will change.</summary>
    public bool Scanning { get; set; }
    public IReadOnlyList<MovieItem> Movies { get; set; } = [];
    public IReadOnlyList<ShowItem> Shows { get; set; } = [];
    /// <summary>Media folders that cannot be reached now (their movies and series are missing from the lists).</summary>
    public IReadOnlyList<string> MissingFolders { get; set; } = [];
}

/// <summary><c>getShow</c>: the episodes of one series (<see cref="LibraryTitle.Key"/>).</summary>
public sealed class GetShowMessage : Message
{
    public string Key { get; set; } = "";
}

public sealed class EpisodeItem
{
    public int Season { get; set; }
    public int Episode { get; set; }
    /// <summary>The episode's name from TMDB; absent when unknown.</summary>
    public string? Title { get; set; }
    public string? Overview { get; set; }
    /// <summary>Full image URL (TMDB still, w300).</summary>
    public string? Still { get; set; }
    public MediaFile File { get; set; } = new();
}

public sealed class ShowMessage : Message
{
    public ShowItem Show { get; set; } = new();
    /// <summary>By season and episode.</summary>
    public IReadOnlyList<EpisodeItem> Episodes { get; set; } = [];
}

public sealed class PingMessage : Message;

public sealed class PongMessage : Message;

/// <summary>Response to the UDP beacon. The <c>id</c> field (the base <see cref="Message.Id"/>) is the PC GUID.</summary>
public sealed class BeaconMessage : Message
{
    public string Name { get; set; } = "";
    public int WsPort { get; set; }
    public int Protocol { get; set; } = ProtocolConstants.Version;
    public bool NeedsPairing { get; set; }
    /// <summary>Bluetooth adapter address (<c>AA:BB:CC:DD:EE:FF</c>); absent when there is no adapter or the radio is off.</summary>
    public string? Bt { get; set; }
    /// <summary>
    /// MAC addresses (<c>AA:BB:CC:DD:EE:FF</c>, wired first) of the connected adapters that will wake the PC from sleep:
    /// a phone wakes it with them (Wake-on-LAN). Empty — it cannot be woken; absent — not known yet.
    /// </summary>
    public IReadOnlyList<string>? Macs { get; set; }
    /// <summary>The encrypted WebSocket (<c>wss</c>): its port and the certificate's SHA-256 a phone pins; absent without one.</summary>
    public TlsInfo? Tls { get; set; }
}

public static class ProtocolConstants
{
    public const int Version = 1;
    public const int DefaultWsPort = 47555;
    public const int DefaultBeaconPort = 47556;
    public const int DefaultTlsPort = 47557;
    public const string DnsSdServiceType = "_sofabird._tcp";
    public const string BeaconRequest = "SOFABIRD_DISCOVER 1";

    /// <summary>RFCOMM service UUID of the companion (protocol/README.md, "Transport").</summary>
    public static readonly Guid BluetoothServiceUuid = new("47fe5bd6-98ad-4d9f-9812-174b3394037d");
    /// <summary>Service name in the SDP record (attribute 0x0100).</summary>
    public const string BluetoothServiceName = "Sofabird";
    /// <summary>Maximum length of one NDJSON line on a byte-stream transport (RFCOMM), excluding the line separator.</summary>
    public const int MaxStreamLineBytes = 1024 * 1024;
}

public static class ErrorCodes
{
    /// <summary>The phone is not paired and <c>hello</c> has no code; the companion shows the code window.</summary>
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
    /// <summary>Watch history, resume prompt and next-episode offer.</summary>
    public const string History = "history";
    /// <summary>Movies and series of the media folders with TMDB metadata (<c>getLibrary</c>, <c>getShow</c>).</summary>
    public const string Library = "library";
    /// <summary>Moonlight is installed on the PC: <c>sys moonlightStart/moonlightStop</c>, <c>state.system.moonlight*</c>.</summary>
    public const string Moonlight = "moonlight";
    /// <summary>A browser for YouTube on the TV: <c>sys youtubeOpen</c>, <c>sys youtube</c>, <c>state.youtube</c>.</summary>
    public const string YouTube = "youtube";
    /// <summary>The sleep timer: <c>sys sleepTimer</c>, <c>state.system.sleepTimer</c>.</summary>
    public const string SleepTimer = "sleepTimer";
}

public static class EventNames
{
    public const string VlcStarted = "vlcStarted";
    public const string VlcExited = "vlcExited";
    public const string InputChanged = "inputChanged";
    public const string EndReached = "endReached";
    public const string Paired = "paired";
    public const string ResumePrompt = "resumePrompt";
    public const string NextEpisode = "nextEpisode";
    /// <summary>Windows suspends in a moment: phones drop the link at once and offer to wake the PC.</summary>
    public const string Sleeping = "sleeping";
}
