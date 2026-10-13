using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VlcRemote.Core.Pairing;

/// <summary>A paired device: the PC stores only the SHA-256 of the token.</summary>
public sealed class PairedDevice
{
    public string TokenHash { get; set; } = "";
    public string? DeviceName { get; set; }
    public string? DeviceId { get; set; }
    public DateTimeOffset PairedAt { get; set; }
}

public interface ITokenStore
{
    IReadOnlyList<PairedDevice> Load();
    void Save(IReadOnlyList<PairedDevice> devices);
}

public sealed class InMemoryTokenStore : ITokenStore
{
    private List<PairedDevice> _items = new();
    public IReadOnlyList<PairedDevice> Load() => _items.ToList();
    public void Save(IReadOnlyList<PairedDevice> devices) => _items = devices.ToList();
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<PairedDevice>))]
internal partial class PairingJsonContext : JsonSerializerContext;

/// <summary>Token store in a JSON file (<c>%LocalAppData%\VlcRemote\paired.json</c>).</summary>
public sealed class JsonFileTokenStore : ITokenStore
{
    private readonly string _path;
    private readonly object _lock = new();

    public JsonFileTokenStore(string path) => _path = path;

    public IReadOnlyList<PairedDevice> Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_path)) return [];
            try
            {
                var json = File.ReadAllText(_path);
                return JsonSerializer.Deserialize(json, PairingJsonContext.Default.ListPairedDevice) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }

    public void Save(IReadOnlyList<PairedDevice> devices)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var json = JsonSerializer.Serialize(devices.ToList(), PairingJsonContext.Default.ListPairedDevice);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
    }
}

public sealed record PairResult(bool Success, string? Token, string? Reason);

/// <summary>
/// Pairing: a 6-digit code is valid for <see cref="CodeLifetime"/> (5 min), at most
/// <see cref="MaxAttempts"/> attempts; the token is 32 random bytes in base64url, its SHA-256 is stored.
/// </summary>
public sealed class PairingService
{
    private readonly ITokenStore _store;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private List<PairedDevice>? _devices; // the store's contents, read once: tokens are checked on every connection
    private string? _code;
    private DateTimeOffset _expiresAt;
    private int _attempts;

    public PairingService(ITokenStore store, TimeProvider? timeProvider = null)
    {
        _store = store;
        _time = timeProvider ?? TimeProvider.System;
    }

    public TimeSpan CodeLifetime { get; init; } = TimeSpan.FromMinutes(5);
    public int MaxAttempts { get; init; } = 5;

    /// <summary>Raised on successful pairing (device name).</summary>
    public event Action<PairedDevice>? Paired;

    public bool IsPairingActive
    {
        get { lock (_lock) return _code is not null && _time.GetUtcNow() < _expiresAt; }
    }

    /// <summary>The active code or <c>null</c> (none, expired).</summary>
    public string? ActiveCode
    {
        get { lock (_lock) return _code is not null && _time.GetUtcNow() < _expiresAt ? _code : null; }
    }

    public DateTimeOffset? ExpiresAt
    {
        get { lock (_lock) return _code is null ? null : _expiresAt; }
    }

    public bool HasPairedDevices
    {
        get { lock (_lock) return Devices.Count > 0; }
    }

    private List<PairedDevice> Devices => _devices ??= _store.Load().ToList();

    /// <summary>Generate a new code and open a pairing window.</summary>
    public string BeginPairing()
    {
        lock (_lock)
        {
            _code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
            _expiresAt = _time.GetUtcNow() + CodeLifetime;
            _attempts = 0;
            return _code;
        }
    }

    public void CancelPairing()
    {
        lock (_lock) _code = null;
    }

    /// <summary>Check the code; on success issue a token and remember its hash.</summary>
    public PairResult TryPair(string? code, string? deviceName, string? deviceId)
    {
        string? token = null;
        PairedDevice? device = null;
        lock (_lock)
        {
            if (_code is null) return new PairResult(false, null, "pairing not active");
            if (_time.GetUtcNow() >= _expiresAt)
            {
                _code = null;
            }
            else if (string.IsNullOrWhiteSpace(code) || !string.Equals(code.Trim(), _code, StringComparison.Ordinal))
            {
                if (++_attempts >= MaxAttempts) _code = null;
            }
            else
            {
                token = GenerateToken();
                device = new PairedDevice
                {
                    TokenHash = Hash(token),
                    DeviceName = deviceName,
                    DeviceId = deviceId,
                    PairedAt = _time.GetUtcNow(),
                };
                var list = Devices.ToList();
                // Pairing the same device again replaces the old token.
                if (!string.IsNullOrEmpty(deviceId))
                    list.RemoveAll(d => d.DeviceId == deviceId);
                list.Add(device);
                _store.Save(list);
                _devices = list;
                _code = null;
            }
        }

        if (device is not null) Paired?.Invoke(device);
        return token is null
            ? new PairResult(false, null, "wrong or expired pairing code")
            : new PairResult(true, token, null);
    }

    public bool ValidateToken(string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        var hash = Encoding.ASCII.GetBytes(Hash(token));
        lock (_lock)
        {
            foreach (var d in Devices)
            {
                if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(d.TokenHash), hash))
                    return true;
            }
        }
        return false;
    }

    public static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64UrlEncode(bytes);
    }

    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    internal static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
