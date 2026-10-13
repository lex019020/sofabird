using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;

namespace VlcRemote.Companion.Services;

public sealed record SystemVolume(int Percent, bool Muted);

/// <summary>System volume/mute of the default output device (NAudio CoreAudio).</summary>
public sealed class AudioService
{
    private readonly ILogger _log;

    public AudioService(ILogger<AudioService> log) => _log = log;

    public SystemVolume? GetSystemVolume()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            using var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var vol = dev.AudioEndpointVolume;
            return new SystemVolume((int)Math.Round(vol.MasterVolumeLevelScalar * 100), vol.Mute);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "GetSystemVolume failed");
            return null;
        }
    }

    public void SetSystemVolume(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        using var en = new MMDeviceEnumerator();
        using var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        dev.AudioEndpointVolume.MasterVolumeLevelScalar = percent / 100f;
        if (percent > 0 && dev.AudioEndpointVolume.Mute) dev.AudioEndpointVolume.Mute = false;
        _log.LogInformation("System volume → {Percent}%", percent);
    }

    public void SetSystemMute(bool mute)
    {
        using var en = new MMDeviceEnumerator();
        using var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        dev.AudioEndpointVolume.Mute = mute;
        _log.LogInformation("System mute → {Mute}", mute);
    }

    /// <summary>Active Windows output devices (the IDs match VLC's <c>adev</c> for mmdevice).</summary>
    public IReadOnlyList<(string Id, string Name)> ListRenderDevices()
    {
        var list = new List<(string, string)>();
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                list.Add((d.ID, d.FriendlyName));
                d.Dispose();
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "ListRenderDevices failed");
        }
        return list;
    }
}
