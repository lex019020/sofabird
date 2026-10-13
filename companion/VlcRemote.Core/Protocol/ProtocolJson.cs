using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VlcRemote.Core.Protocol;

/// <summary>
/// Source-generated контекст сериализации протокола. Все сообщения сериализуются через
/// базовый тип <see cref="Message"/>, чтобы в JSON попал дискриминатор <c>t</c>.
/// Реальный экземпляр (<see cref="ProtocolJson"/>) создаётся с теми же настройками плюс
/// <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>: кириллица в названиях дорожек
/// и файлов уходит как есть, а не как <c>М</c>.
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
[JsonSerializable(typeof(HistoryUpdateMessage))]
[JsonSerializable(typeof(HistoryClearMessage))]
[JsonSerializable(typeof(InfoResponse))]
public partial class ProtocolJsonContext : JsonSerializerContext
{
}

/// <summary>Ответ <c>GET /info</c>.</summary>
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
}

public static class ProtocolJson
{
    /// <summary>Контекст с настройками атрибута плюс relaxed-экранирование (не для HTML).</summary>
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

    /// <summary>Разобрать строку; любая ошибка формата (включая отсутствие <c>t</c>) — <see cref="JsonException"/>.</summary>
    public static Message? Deserialize(string line)
    {
        try
        {
            return JsonSerializer.Deserialize(line, Context.Message);
        }
        catch (NotSupportedException ex)
        {
            // STJ бросает NotSupportedException, если нет дискриминатора "t".
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
