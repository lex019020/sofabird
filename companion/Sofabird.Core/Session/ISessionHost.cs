using Sofabird.Core.Pairing;
using Sofabird.Core.Protocol;

namespace Sofabird.Core.Session;

/// <summary>
/// What the companion provides to a session: PC details, pairing and request executors.
/// Executors throw <see cref="ProtocolException"/> (→ <c>err</c> with a code) or
/// VLC exceptions (<see cref="Vlc.VlcException"/> → <c>vlc_error</c>/<c>vlc_not_running</c>).
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
    /// An unpaired phone sent <c>hello</c> without a code: show the code window on the PC screen
    /// (or raise the one already open). Must not block or throw — the session answers
    /// <c>pairing_required</c> and closes anyway.
    /// </summary>
    void RequestPairing(DeviceInfo? device);

    Task<StateMessage> GetStateAsync(CancellationToken ct);
    Task ExecuteCommandAsync(string name, CommandArgs args, CancellationToken ct);
    /// <summary>A system command; returns the response message (for example, <c>displays</c>) or <c>null</c> → <c>ok</c>.</summary>
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

    /// <summary><c>getLibrary</c>: the movies and series of the media folders.</summary>
    Task<LibraryMessage> GetLibraryAsync(CancellationToken ct);
    /// <summary><c>getShow</c>: one series with its episodes; <c>null</c> when the library has no such series.</summary>
    Task<ShowMessage?> GetShowAsync(string key, CancellationToken ct);
}
