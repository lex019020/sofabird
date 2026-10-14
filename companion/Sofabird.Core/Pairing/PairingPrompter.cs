namespace Sofabird.Core.Pairing;

public enum PairingPromptAction
{
    /// <summary>The same phone asked recently — leave the window alone.</summary>
    Throttled,
    /// <summary>A new code was created — show the window.</summary>
    ShowNew,
    /// <summary>A code is already active — raise the window to the top (the code stays the same).</summary>
    BringToFront,
}

public sealed record PairingPrompt(PairingPromptAction Action, string? Code, string? DeviceName, string? DeviceId);

/// <summary>
/// Automatic pairing: a phone without a token sent <c>hello</c> without a code → the companion itself
/// shows the code window. If a code is already active, no new one is created (the window is only raised).
/// Limits: at most one window per <see cref="PerDeviceInterval"/> per device (the phone may
/// reconnect in a loop) and at most one new code per <see cref="NewCodeInterval"/> overall
/// (otherwise brute-forcing "5 attempts per code" could be repeated forever).
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
