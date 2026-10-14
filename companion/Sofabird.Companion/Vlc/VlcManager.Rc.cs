using Microsoft.Extensions.Logging;
using Sofabird.Core.Protocol;
using Sofabird.Core.Session;
using Sofabird.Core.State;
using Sofabird.Core.Vlc;

namespace Sofabird.Companion.Vlc;

// RC and audio devices: the RC connection, status events, current tracks via RC, output devices.
public sealed partial class VlcManager
{
    // ------------------------------------------------------------------ RC

    private async Task ConnectRcAsync(CancellationToken ct)
    {
        try
        {
            await _rc.ConnectAsync(ct).ConfigureAwait(false);
        }
        catch (VlcException ex)
        {
            _log.LogInformation("RC not available: {Error}", ex.Message);
        }
    }

    private void OnRcStatusChanged(RcStatusChange change)
    {
        _log.LogDebug("RC: {Raw}", change.Raw);
        switch (change.Kind)
        {
            case RcStatusKind.StopState when change.Number == 0:
                _endReachedHint = true;
                break;
            case RcStatusKind.NewInput:
                _playlistDirty = true;
                _rcTracksDirty = true;
                break;
        }
        Poke();
    }

    private async Task RefreshRcTracksAsync(CancellationToken ct)
    {
        if (!_rc.IsConnected)
        {
            await ConnectRcAsync(ct).ConfigureAwait(false);
            if (!_rc.IsConnected) return;
        }
        try
        {
            var audio = await _rc.ListTracksAsync(VlcTrackKind.Audio, ct).ConfigureAwait(false);
            if (audio is null) return; // paused — RC refused, try later
            var spu = await _rc.ListTracksAsync(VlcTrackKind.Subtitle, ct).ConfigureAwait(false);
            var video = await _rc.ListTracksAsync(VlcTrackKind.Video, ct).ConfigureAwait(false);
            _rcTracks = new RcTrackSelection(
                audio.FirstOrDefault(t => t.Current)?.Id,
                spu?.FirstOrDefault(t => t.Current)?.Id,
                video?.FirstOrDefault(t => t.Current)?.Id);
            _rcTracksDirty = false;
        }
        catch (VlcException ex)
        {
            _log.LogDebug("RC track listing failed: {Error}", ex.Message);
        }
    }

    public async Task<IReadOnlyList<AudioDeviceEntry>> GetAudioDevicesAsync(CancellationToken ct)
    {
        EnsureRunning();
        var list = await _rc.ListAudioDevicesAsync(ct).ConfigureAwait(false);
        var result = list.Select(d => new AudioDeviceEntry { Id = d.Id, Name = d.Text, Current = d.Current }).ToList();
        _lastAudioDevice = result.FirstOrDefault(d => d.Current)?.Id;
        return result;
    }

    public async Task SetAudioDeviceAsync(string id, CancellationToken ct)
    {
        EnsureRunning();
        if (string.IsNullOrEmpty(id))
        {
            // RC cannot select "the default device" with an empty id; try through our Lua page.
            if (TracksPageAvailable)
            {
                var r = await _http.SetVarAsync("aout", "device", "", ct).ConfigureAwait(false);
                if (!r.Ok) throw ProtocolException.VlcError(r.Error ?? "setvar failed");
            }
            else
            {
                throw ProtocolException.Unsupported("selecting the default audio device requires Lua pages (tracks.json) installed in VLC");
            }
        }
        else
        {
            await _rc.SetAudioDeviceAsync(id, ct).ConfigureAwait(false);
        }
        _lastAudioDevice = id;
        Poke();
    }

    public string? LastAudioDevice => _lastAudioDevice;

    /// <summary>
    /// The output device chosen from the phone came back (e.g. the TV's HDMI audio after the TV was off): VLC fell back
    /// to another device meanwhile, so switch it back. Returns <c>true</c> if VLC was switched.
    /// </summary>
    public async Task<bool> RestoreAudioDeviceAsync(string id, CancellationToken ct)
    {
        if (RunState != VlcRunState.Running || string.IsNullOrEmpty(id)) return false;
        var devices = await GetAudioDevicesAsync(ct).ConfigureAwait(false);
        if (!devices.Any(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase))) return false;
        if (devices.Any(d => d.Current && string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase))) return false;
        await _rc.SetAudioDeviceAsync(id, ct).ConfigureAwait(false);
        _lastAudioDevice = id;
        Poke();
        return true;
    }

    /// <summary>When the status polling last saw VLC fullscreen.</summary>
    public DateTimeOffset? LastFullscreenAt { get; private set; }

    public bool IsPlaying => RunState == VlcRunState.Running && _lastStatus?.IsPlaying == true;

    public async Task PauseAsync(CancellationToken ct)
    {
        EnsureRunning();
        _lastStatus = await Cmd("pl_forcepause", ct).ConfigureAwait(false);
        Poke();
    }
}
