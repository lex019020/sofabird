using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using VlcRemote.Core.Protocol;

namespace VlcRemote.Companion;

/// <summary>Companion settings (<c>%LocalAppData%\VlcRemote\settings.json</c>).</summary>
public sealed class Settings
{
    /// <summary>Stable installation GUID (TXT <c>id</c>, <c>welcome.id</c>).</summary>
    public string InstallationId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Address the WebSocket and the UDP beacon listen on. <c>0.0.0.0</c> = all interfaces.</summary>
    public string ListenAddress { get; set; } = "0.0.0.0";
    public int WsPort { get; set; } = ProtocolConstants.DefaultWsPort;
    public int BeaconPort { get; set; } = ProtocolConstants.DefaultBeaconPort;
    public bool AdvertiseDnsSd { get; set; } = true;
    /// <summary>Bluetooth RFCOMM server (phone without Wi-Fi). <c>false</c> — do not advertise the service.</summary>
    public bool Bluetooth { get; set; } = true;
    /// <summary>
    /// Require an authenticated (MITM-protected) Bluetooth link key (<c>BluetoothEncryptionWithAuthentication</c>)
    /// instead of encryption with any bond (<c>BluetoothEncryptionAllowNullAuthentication</c>). Off by default:
    /// "Just Works" bonds would be rejected; the protocol token authenticates the phone anyway.
    /// </summary>
    public bool BluetoothRequireAuthentication { get; set; }

    /// <summary>Path to <c>vlc.exe</c>; <c>null</c> — look it up in the registry/Program Files.</summary>
    public string? VlcPath { get; set; }
    /// <summary>Host of the VLC HTTP interface when the companion starts VLC (<c>0.0.0.0</c> — the phone can also talk to it directly).</summary>
    public string VlcHttpHost { get; set; } = "0.0.0.0";
    public int VlcHttpPort { get; set; } = 8080;
    /// <summary>Password of the VLC HTTP interface; generated on the first start.</summary>
    public string? VlcHttpPassword { get; set; }
    public int VlcRcPort { get; set; } = 4212;
    /// <summary>Start VLC in fullscreen mode right away.</summary>
    public bool LaunchFullscreen { get; set; } = true;
    /// <summary>Extra VLC command-line arguments.</summary>
    public string? VlcExtraArgs { get; set; }

    /// <summary>Media library roots (informational; browsing goes through VLC <c>browse.json</c>).</summary>
    public List<string> MediaRoots { get; set; } = new();

    /// <summary>
    /// Stable id of the preferred monitor (the TV): every new VLC window is moved there once.
    /// <c>null</c> — the companion does not manage the VLC window.
    /// </summary>
    public string? PreferredDisplayId { get; set; }
    /// <summary>Last known name of the preferred monitor (shown in the tray while it is disconnected).</summary>
    public string? PreferredDisplayName { get; set; }
    /// <summary>
    /// Legacy (≤ 0.x): index of the preferred monitor in <c>listDisplays</c>. Read only for the one-time migration
    /// to <see cref="PreferredDisplayId"/> (<see cref="MigratePreferredDisplay"/>), then removed from the file.
    /// </summary>
    public int? PreferredDisplay { get; set; }
    /// <summary>Keep the PC/display awake during playback.</summary>
    public bool KeepAwake { get; set; } = true;
    /// <summary>
    /// "This PC is a media station" (setup window): the power plan keeps it working with the lid closed and without
    /// sleep, on mains and on battery; the tray warns when Windows changes that. <c>null</c> — not decided yet.
    /// </summary>
    public bool? MediaStation { get; set; }
    /// <summary>
    /// VLC output device chosen from the phone (mmdevice endpoint id); when it disappears (TV off) and comes back,
    /// VLC is switched back to it. <c>null</c> — VLC uses the Windows default device.
    /// </summary>
    public string? VlcAudioDevice { get; set; }
    /// <summary>Update the Lua pages in <c>%APPDATA%\vlc\lua\http</c> automatically.</summary>
    public bool AutoInstallLuaPages { get; set; } = true;
    /// <summary>
    /// Automatic updates: <c>"beta"</c> — test builds too, <c>"stable"</c> — releases only, <c>null</c> — the kind of build
    /// that is installed (tray: "Get test builds").
    /// </summary>
    public string? UpdateChannel { get; set; }
    /// <summary>The version that ran last time: a different one at start means "updated" (a tray notice).</summary>
    public string? LastRunVersion { get; set; }
    /// <summary>Start VLC automatically when the companion starts.</summary>
    public bool AutoLaunchVlc { get; set; }
    /// <summary>
    /// Write the web interface and RC with the companion's password into <c>vlcrc</c> (while VLC is not running), so that VLC
    /// opened from Explorer or the Start menu can be controlled from the phone right away.
    /// </summary>
    public bool ConfigureVlc { get; set; } = true;
    /// <summary>Path to <c>vlcrc</c>; <c>null</c> — <c>%APPDATA%\vlc\vlcrc</c> (another path is for tests).</summary>
    public string? VlcrcPath { get; set; }

    /// <summary>
    /// The "Setup and permissions" window was completed (Apply) for <see cref="SetupVersion"/>. Until then it opens at
    /// every start; a newer item set (<see cref="VlcRemote.Core.Setup.SetupPolicy.CurrentVersion"/>) shows it once more.
    /// </summary>
    public bool SetupCompleted { get; set; }
    /// <summary>Item-set version of the setup window the user completed last.</summary>
    public int SetupVersion { get; set; }
    /// <summary>
    /// Network access (firewall rules) was chosen in the setup window: the companion checks the rules at startup and
    /// offers to fix them. <c>false</c> — the user manages the firewall on their own.
    /// </summary>
    public bool ManageFirewall { get; set; } = true;

    public void EnsureDefaults()
    {
        if (string.IsNullOrWhiteSpace(InstallationId) || !Guid.TryParse(InstallationId, out _))
            InstallationId = Guid.NewGuid().ToString();
        if (string.IsNullOrWhiteSpace(VlcHttpPassword))
            VlcHttpPassword = GeneratePassword();
        if (WsPort <= 0 || WsPort > 65535) WsPort = ProtocolConstants.DefaultWsPort;
        if (BeaconPort <= 0 || BeaconPort > 65535) BeaconPort = ProtocolConstants.DefaultBeaconPort;
        if (VlcHttpPort <= 0 || VlcHttpPort > 65535) VlcHttpPort = 8080;
        if (VlcRcPort <= 0 || VlcRcPort > 65535) VlcRcPort = 4212;
        if (string.IsNullOrWhiteSpace(ListenAddress)) ListenAddress = "0.0.0.0";
        if (string.IsNullOrWhiteSpace(VlcHttpHost)) VlcHttpHost = "0.0.0.0";
    }

    /// <summary>
    /// One-time migration of the legacy index: map it to the id of the monitor at that index now (dropped if out of
    /// range). Returns <c>true</c> if the settings changed and should be saved.
    /// </summary>
    public bool MigratePreferredDisplay(IReadOnlyList<VlcRemote.Core.Displays.MonitorInfo> monitors)
    {
        var r = VlcRemote.Core.Displays.MonitorTopology.MigrateLegacyPreference(PreferredDisplay, PreferredDisplayId, PreferredDisplayName, monitors);
        if (!r.Changed) return false;
        PreferredDisplayId = r.Id;
        PreferredDisplayName = r.Name;
        PreferredDisplay = null;
        return true;
    }

    private static string GeneratePassword()
    {
        const string alphabet = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return string.Create(20, alphabet, static (span, a) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = a[RandomNumberGenerator.GetInt32(a.Length)];
        });
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(Settings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>
/// The settings file. <see cref="Load"/> returns the one <see cref="Settings"/> instance the companion shares;
/// every change goes through <see cref="Update"/>, so concurrent changes are written one at a time.
/// </summary>
public sealed class SettingsStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private Settings? _current;

    public SettingsStore(string path) => _path = path;

    public Settings Load()
    {
        Settings? s = null;
        lock (_lock)
        {
            if (File.Exists(_path))
            {
                try
                {
                    s = JsonSerializer.Deserialize(File.ReadAllText(_path), SettingsJsonContext.Default.Settings);
                }
                catch (JsonException)
                {
                    // Corrupt file — keep a copy and start over.
                    File.Copy(_path, _path + ".broken", overwrite: true);
                }
            }
        }
        var wasNull = s is null;
        s ??= new Settings();
        var before = JsonSerializer.Serialize(s, SettingsJsonContext.Default.Settings);
        s.EnsureDefaults();
        var after = JsonSerializer.Serialize(s, SettingsJsonContext.Default.Settings);
        lock (_lock)
        {
            _current = s;
            if (wasNull || before != after) Write(s);
        }
        return s;
    }

    /// <summary>Changes the loaded settings and writes them.</summary>
    public void Update(Action<Settings> change)
    {
        lock (_lock)
        {
            var settings = _current ?? throw new InvalidOperationException("Settings are not loaded");
            change(settings);
            Write(settings);
        }
    }

    private void Write(Settings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var json = JsonSerializer.Serialize(settings, SettingsJsonContext.Default.Settings);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _path, overwrite: true);
    }
}
