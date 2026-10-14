using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;

namespace Sofabird.Companion.Services;

/// <summary>
/// Windows audio endpoint notifications: raises <see cref="DeviceArrived"/> when an output device becomes active
/// (e.g. the TV's HDMI audio after the TV was switched off and on, or the cable re-plugged).
/// </summary>
public sealed class AudioDeviceWatcher : IDisposable
{
    private readonly ILogger _log;
    private MMDeviceEnumerator? _enumerator;
    private MMDeviceNotificationClient? _client;

    public AudioDeviceWatcher(ILogger<AudioDeviceWatcher> log) => _log = log;

    /// <summary>Endpoint id of a device that became active. Raised on an audio system thread: do not block it.</summary>
    public event Action<string>? DeviceArrived;

    public void Start()
    {
        try
        {
            _enumerator = new MMDeviceEnumerator();
            // Not marshalled to the UI thread: the handler only starts a background task.
            _client = _enumerator.CreateNotificationClient(useSynchronizationContext: false);
            _client.DeviceStateChanged += (_, e) =>
            {
                if (e.NewState == DeviceState.Active) Raise(e.DeviceId);
            };
            _client.DeviceAdded += (_, e) => Raise(e.DeviceId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Audio device notifications are not available");
            Dispose();
        }
    }

    private void Raise(string id)
    {
        try { DeviceArrived?.Invoke(id); }
        catch (Exception ex) { _log.LogWarning(ex, "DeviceArrived handler failed"); }
    }

    public void Dispose()
    {
        _client?.Dispose();
        _client = null;
        _enumerator?.Dispose();
        _enumerator = null;
    }
}
