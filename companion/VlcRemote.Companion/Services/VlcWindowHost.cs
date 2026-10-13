using VlcRemote.Companion.Vlc;
using VlcRemote.Core.Displays;

namespace VlcRemote.Companion.Services;

/// <summary><see cref="IVlcWindowHost"/> over <see cref="VlcManager"/> (process, HTTP fullscreen) and Win32 windows.</summary>
public sealed class VlcWindowHost : IVlcWindowHost
{
    private readonly VlcManager _vlc;
    private readonly DisplayService _display;

    public VlcWindowHost(VlcManager vlc, DisplayService display)
    {
        _vlc = vlc;
        _display = display;
    }

    public int? ProcessId => _vlc.IsProcessRunning ? _vlc.ProcessId : null;

    public long FindMainWindow(int pid) => DisplayService.FindMainWindow(pid).ToInt64();

    public string? GetWindowMonitor(long window) => DisplayService.GetWindowMonitorDevice(new IntPtr(window));

    public bool IsControllable => _vlc.RunState == VlcRunState.Running;

    public Task<bool?> GetFullscreenAsync(CancellationToken ct) => _vlc.GetFullscreenAsync(ct);

    public Task SetFullscreenAsync(bool on, CancellationToken ct) => _vlc.SetFullscreenAsync(on, ct);

    public bool MoveWindow(long window, MonitorInfo target) => _display.MoveWindowToMonitor(new IntPtr(window), target);

    public bool PlaybackHeld => _vlc.PlaybackHeld;

    public bool RestoreWindow(long window) => DisplayService.RestoreWindow(new IntPtr(window));

    public DateTimeOffset? LastFullscreenAt => _vlc.LastFullscreenAt;

    public bool IsPlaying => _vlc.IsPlaying;

    public Task PauseAsync(CancellationToken ct) => _vlc.PauseAsync(ct);
}

/// <summary><see cref="IDisplayPreferenceStore"/> over the companion settings file.</summary>
public sealed class SettingsDisplayPreference : IDisplayPreferenceStore
{
    private readonly Settings _settings;
    private readonly SettingsStore _store;

    public SettingsDisplayPreference(Settings settings, SettingsStore store)
    {
        _settings = settings;
        _store = store;
    }

    public string? PreferredId => _settings.PreferredDisplayId;
    public string? PreferredName => _settings.PreferredDisplayName;

    public void SetPreferred(string? id, string? name)
    {
        _settings.PreferredDisplayId = id;
        _settings.PreferredDisplayName = name;
        _settings.PreferredDisplay = null;
        _store.Save(_settings);
    }
}
