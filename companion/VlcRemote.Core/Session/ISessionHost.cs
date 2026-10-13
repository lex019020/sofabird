using VlcRemote.Core.Pairing;
using VlcRemote.Core.Protocol;

namespace VlcRemote.Core.Session;

/// <summary>
/// То, что компаньон предоставляет сессии: сведения о ПК, сопряжение и исполнители запросов.
/// Исполнители бросают <see cref="ProtocolException"/> (→ <c>err</c> с кодом) либо
/// исключения VLC (<see cref="Vlc.VlcException"/> → <c>vlc_error</c>/<c>vlc_not_running</c>).
/// </summary>
public interface ISessionHost
{
    string MachineName { get; }
    string InstallationId { get; }
    IReadOnlyList<string> Caps { get; }
    VlcInfo VlcInfo { get; }
    /// <summary>Bluetooth adapter address for <c>welcome.bt</c>; <c>null</c> — no adapter, radio off or Bluetooth disabled.</summary>
    string? BluetoothAddress { get; }
    PairingService Pairing { get; }

    /// <summary>
    /// Несопряжённый телефон прислал <c>hello</c> без кода: показать на экране ПК окно с кодом
    /// (или поднять уже открытое). Не должен блокировать и бросать — сессия всё равно ответит
    /// <c>pairing_required</c> и закроется.
    /// </summary>
    void RequestPairing(DeviceInfo? device);

    Task<StateMessage> GetStateAsync(CancellationToken ct);
    Task ExecuteCommandAsync(string name, CommandArgs args, CancellationToken ct);
    /// <summary>Системная команда; возвращает ответное сообщение (например, <c>displays</c>) или <c>null</c> → <c>ok</c>.</summary>
    Task<Message?> ExecuteSysAsync(string name, CommandArgs args, CancellationToken ct);
    Task<IReadOnlyList<BrowseEntry>> BrowseAsync(string uri, CancellationToken ct);
    Task<IReadOnlyList<PlaylistEntry>> GetPlaylistAsync(CancellationToken ct);
    Task<TracksInfo> GetTracksAsync(CancellationToken ct);
    Task<IReadOnlyList<AudioDeviceEntry>> GetAudioDevicesAsync(CancellationToken ct);

    /// <summary><c>getHistory</c> with a validated <paramref name="filter"/> (<c>continue</c>|<c>all</c>) and paging.</summary>
    Task<HistoryMessage> GetHistoryAsync(string filter, int limit, int offset, CancellationToken ct);
    /// <summary><c>historyUpdate</c>: mark watched/unwatched (<paramref name="finished"/>) or <paramref name="remove"/>.</summary>
    Task UpdateHistoryAsync(string uri, bool? finished, bool remove, CancellationToken ct);
    /// <summary><c>historyClear</c>.</summary>
    Task ClearHistoryAsync(CancellationToken ct);
}
