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
    PairingService Pairing { get; }

    Task<StateMessage> GetStateAsync(CancellationToken ct);
    Task ExecuteCommandAsync(string name, CommandArgs args, CancellationToken ct);
    /// <summary>Системная команда; возвращает ответное сообщение (например, <c>displays</c>) или <c>null</c> → <c>ok</c>.</summary>
    Task<Message?> ExecuteSysAsync(string name, CommandArgs args, CancellationToken ct);
    Task<IReadOnlyList<BrowseEntry>> BrowseAsync(string uri, CancellationToken ct);
    Task<IReadOnlyList<PlaylistEntry>> GetPlaylistAsync(CancellationToken ct);
    Task<TracksInfo> GetTracksAsync(CancellationToken ct);
    Task<IReadOnlyList<AudioDeviceEntry>> GetAudioDevicesAsync(CancellationToken ct);
}
