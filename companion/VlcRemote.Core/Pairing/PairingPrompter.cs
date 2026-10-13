namespace VlcRemote.Core.Pairing;

public enum PairingPromptAction
{
    /// <summary>Тот же телефон просил недавно — окно не трогаем.</summary>
    Throttled,
    /// <summary>Создан новый код — показать окно.</summary>
    ShowNew,
    /// <summary>Код уже активен — поднять окно поверх (код прежний).</summary>
    BringToFront,
}

public sealed record PairingPrompt(PairingPromptAction Action, string? Code, string? DeviceName, string? DeviceId);

/// <summary>
/// Автоматическое сопряжение: телефон без токена прислал <c>hello</c> без кода → компаньон сам
/// показывает окно с кодом. Если код уже активен, новый не создаётся (окно только поднимается).
/// Ограничения: не чаще одного окна в <see cref="PerDeviceInterval"/> на устройство (телефон может
/// переподключаться в цикле) и не чаще одного нового кода в <see cref="NewCodeInterval"/> вообще
/// (иначе перебор «5 попыток на код» можно повторять бесконечно).
/// </summary>
public sealed class PairingPrompter
{
    private readonly PairingService _pairing;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, DateTimeOffset> _lastByDevice = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private DateTimeOffset _lastNewCode = DateTimeOffset.MinValue;

    public PairingPrompter(PairingService pairing, TimeProvider? timeProvider = null)
    {
        _pairing = pairing;
        _time = timeProvider ?? TimeProvider.System;
    }

    public TimeSpan PerDeviceInterval { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan NewCodeInterval { get; init; } = TimeSpan.FromSeconds(10);

    public PairingPrompt Request(string? deviceName, string? deviceId)
    {
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            var key = deviceId ?? deviceName ?? "";
            if (_lastByDevice.TryGetValue(key, out var last) && now - last < PerDeviceInterval)
                return new PairingPrompt(PairingPromptAction.Throttled, null, deviceName, deviceId);

            if (_pairing.ActiveCode is { } active)
            {
                _lastByDevice[key] = now;
                return new PairingPrompt(PairingPromptAction.BringToFront, active, deviceName, deviceId);
            }

            if (now - _lastNewCode < NewCodeInterval)
                return new PairingPrompt(PairingPromptAction.Throttled, null, deviceName, deviceId);

            _lastByDevice[key] = now;
            _lastNewCode = now;
            if (_lastByDevice.Count > 256)
            {
                foreach (var old in _lastByDevice.Where(kv => now - kv.Value >= PerDeviceInterval).Select(kv => kv.Key).ToList())
                    _lastByDevice.Remove(old);
            }
            var code = _pairing.BeginPairing();
            return new PairingPrompt(PairingPromptAction.ShowNew, code, deviceName, deviceId);
        }
    }
}
