using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sofabird.Core.Protocol;

/// <summary>
/// Source-generated serialization context of the protocol. All messages are serialized through
/// the base type <see cref="Message"/>, so that the <c>t</c> discriminator ends up in the JSON.
/// The actual instance (<see cref="ProtocolJson"/>) is created with the same options plus
/// <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>: Cyrillic in track and file names
/// is sent as is, not as <c>\u041C</c>.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    AllowOutOfOrderMetadataProperties = true,
    PropertyNameCaseInsensitive = true,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(Message))]
[JsonSerializable(typeof(HelloMessage))]
[JsonSerializable(typeof(WelcomeMessage))]
[JsonSerializable(typeof(ErrMessage))]
[JsonSerializable(typeof(OkMessage))]
[JsonSerializable(typeof(CmdMessage))]
[JsonSerializable(typeof(SysMessage))]
[JsonSerializable(typeof(BrowseMessage))]
[JsonSerializable(typeof(EntriesMessage))]
[JsonSerializable(typeof(GetStateMessage))]
[JsonSerializable(typeof(GetPlaylistMessage))]
[JsonSerializable(typeof(PlaylistMessage))]
[JsonSerializable(typeof(GetTracksMessage))]
[JsonSerializable(typeof(TracksMessage))]
[JsonSerializable(typeof(GetAudioDevicesMessage))]
[JsonSerializable(typeof(AudioDevicesMessage))]
[JsonSerializable(typeof(DisplaysMessage))]
[JsonSerializable(typeof(StateMessage))]
[JsonSerializable(typeof(EventMessage))]
[JsonSerializable(typeof(PingMessage))]
[JsonSerializable(typeof(PongMessage))]
[JsonSerializable(typeof(BeaconMessage))]
[JsonSerializable(typeof(GetHistoryMessage))]
[JsonSerializable(typeof(HistoryMessage))]
[JsonSerializable(typeof(GetLibraryMessage))]
[JsonSerializable(typeof(LibraryMessage))]
[JsonSerializable(typeof(GetShowMessage))]
[JsonSerializable(typeof(ShowMessage))]
[JsonSerializable(typeof(HistoryUpdateMessage))]
[JsonSerializable(typeof(HistoryClearMessage))]
[JsonSerializable(typeof(InfoResponse))]
public partial class ProtocolJsonContext : JsonSerializerContext
{
}

/// <summary>Response to <c>GET /info</c>.</summary>
public sealed class InfoResponse
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public int Protocol { get; set; } = ProtocolConstants.Version;
    public VlcInfo Vlc { get; set; } = new();
    public int WsPort { get; set; }
    public bool NeedsPairing { get; set; }
    [JsonPropertyName("id")]
    public string PcId { get; set; } = "";
    /// <summary>Bluetooth adapter address (<c>AA:BB:CC:DD:EE:FF</c>); absent when there is no adapter or the radio is off.</summary>
    public string? Bt { get; set; }
    /// <summary>MAC addresses of the PC's network adapters (<c>AA:BB:CC:DD:EE:FF</c>, wired and connected first): a phone wakes the PC with them (Wake-on-LAN).</summary>
    public IReadOnlyList<string>? Macs { get; set; }
}

public static class ProtocolJson
{
    /// <summary>Context with the attribute options plus relaxed escaping (not for HTML).</summary>
    public static ProtocolJsonContext Context { get; } = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        AllowOutOfOrderMetadataProperties = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    public static string Serialize(Message message) =>
        JsonSerializer.Serialize(message, Context.Message);

    public static string SerializeInfo(InfoResponse info) =>
        JsonSerializer.Serialize(info, Context.InfoResponse);

    /// <summary>Parse a line; any format error (including a missing <c>t</c>) is a <see cref="JsonException"/>.</summary>
    public static Message? Deserialize(string line)
    {
        try
        {
            return JsonSerializer.Deserialize(line, Context.Message);
        }
        catch (NotSupportedException ex)
        {
            // STJ throws NotSupportedException when the "t" discriminator is missing.
            throw new JsonException("missing or invalid message type 't'", ex);
        }
    }

    public static Message? Deserialize(ReadOnlySpan<byte> utf8)
    {
        try
        {
            return JsonSerializer.Deserialize(utf8, Context.Message);
        }
        catch (NotSupportedException ex)
        {
            throw new JsonException("missing or invalid message type 't'", ex);
        }
    }
}
