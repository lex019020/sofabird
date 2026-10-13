using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using VlcRemote.Core.Protocol;

namespace VlcRemote.Companion;

/// <summary>Настройки компаньона (<c>%LocalAppData%\VlcRemote\settings.json</c>).</summary>
public sealed class Settings
{
    /// <summary>Стабильный GUID установки (TXT <c>id</c>, <c>welcome.id</c>).</summary>
    public string InstallationId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Адрес, на котором слушают WebSocket и UDP-маяк. <c>0.0.0.0</c> = все интерфейсы.</summary>
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

    /// <summary>Путь к <c>vlc.exe</c>; <c>null</c> — искать в реестре/Program Files.</summary>
    public string? VlcPath { get; set; }
    /// <summary>Хост HTTP-интерфейса VLC при запуске компаньоном (<c>0.0.0.0</c> — телефон может ходить и напрямую).</summary>
    public string VlcHttpHost { get; set; } = "0.0.0.0";
    public int VlcHttpPort { get; set; } = 8080;
    /// <summary>Пароль HTTP-интерфейса VLC; генерируется при первом запуске.</summary>
    public string? VlcHttpPassword { get; set; }
    public int VlcRcPort { get; set; } = 4212;
    /// <summary>Запускать VLC сразу в полноэкранном режиме.</summary>
    public bool LaunchFullscreen { get; set; } = true;
    /// <summary>Дополнительные аргументы командной строки VLC.</summary>
    public string? VlcExtraArgs { get; set; }

    /// <summary>Корни медиатеки (информационно; обзор идёт через VLC <c>browse.json</c>).</summary>
    public List<string> MediaRoots { get; set; } = new();

    /// <summary>Индекс предпочитаемого экрана (ТВ) в списке <c>listDisplays</c>.</summary>
    public int? PreferredDisplay { get; set; }
    /// <summary>Не давать ПК/экрану уснуть во время воспроизведения.</summary>
    public bool KeepAwake { get; set; } = true;
    /// <summary>Автоматически обновлять Lua-страницы в <c>%APPDATA%\vlc\lua\http</c>.</summary>
    public bool AutoInstallLuaPages { get; set; } = true;
    /// <summary>Автоматически запускать VLC при старте компаньона.</summary>
    public bool AutoLaunchVlc { get; set; }
    /// <summary>
    /// Прописывать в <c>vlcrc</c> (пока VLC не запущен) веб-интерфейс и RC с паролем компаньона, чтобы VLC,
    /// открытый из Проводника или меню «Пуск», сразу был управляем с телефона.
    /// </summary>
    public bool ConfigureVlc { get; set; } = true;
    /// <summary>Путь к <c>vlcrc</c>; <c>null</c> — <c>%APPDATA%\vlc\vlcrc</c> (другой путь — для тестов).</summary>
    public string? VlcrcPath { get; set; }

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
internal partial class SettingsJsonContext : JsonSerializerContext;

public sealed class SettingsStore
{
    private readonly string _path;
    private readonly object _lock = new();

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
                    // Битый файл — сохраняем копию и начинаем заново.
                    File.Copy(_path, _path + ".broken", overwrite: true);
                }
            }
        }
        var wasNull = s is null;
        s ??= new Settings();
        var before = JsonSerializer.Serialize(s, SettingsJsonContext.Default.Settings);
        s.EnsureDefaults();
        var after = JsonSerializer.Serialize(s, SettingsJsonContext.Default.Settings);
        if (wasNull || before != after) Save(s);
        return s;
    }

    public void Save(Settings settings)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var json = JsonSerializer.Serialize(settings, SettingsJsonContext.Default.Settings);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
    }
}
