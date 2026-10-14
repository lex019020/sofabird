using System.Globalization;
using Sofabird.Core.Localization;

namespace Sofabird.Core.Displays;

/// <summary>Rectangle in virtual-screen (physical pixel) coordinates.</summary>
public readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;
}

public enum MonitorKind
{
    Unknown,
    /// <summary>Built-in laptop panel (eDP / LVDS / embedded UDI / "internal").</summary>
    Internal,
    /// <summary>HDMI, DisplayPort, DVI, Miracast, …</summary>
    External,
}

/// <summary>A GDI screen (<c>Screen.AllScreens</c>): one per desktop source; clones share a source.</summary>
public sealed record GdiScreen(string DeviceName, ScreenRect Bounds, ScreenRect WorkArea, bool Primary);

/// <summary>
/// One active display path target (<c>QueryDisplayConfig</c> + <c>DisplayConfigGetDeviceInfo</c>):
/// the monitor behind a GDI source. Several targets share a source in duplicate (clone) mode.
/// </summary>
/// <param name="SourceGdiName"><c>viewGdiDeviceName</c> of the path source (<c>\\.\DISPLAY1</c>).</param>
/// <param name="MonitorDevicePath"><c>monitorDevicePath</c> (stable across reconnects), may be empty.</param>
/// <param name="FriendlyName"><c>monitorFriendlyDeviceName</c> from EDID, may be empty.</param>
/// <param name="OutputTechnology">Raw <c>DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY</c>.</param>
public sealed record DisplayTarget(string SourceGdiName, string? MonitorDevicePath, string? FriendlyName, uint OutputTechnology);

/// <summary>A monitor as the companion shows it: stable id, readable name, kind and geometry.</summary>
public sealed class MonitorInfo
{
    /// <summary>Position in the list (ordered by GDI device name) — the legacy <c>display</c> index.</summary>
    public int Index { get; init; }
    /// <summary>Stable id: <c>monitorDevicePath</c>, or the GDI device name when the path is unknown.</summary>
    public string Id { get; init; } = "";
    /// <summary>English name (friendly name from EDID or the English fallback).</summary>
    public string Name { get; init; } = "";
    /// <summary>Localizable fallback name when EDID has none; <c>null</c> — <see cref="Name"/> is a real name.</summary>
    public LocalizableText? NameText { get; init; }
    public MonitorKind Kind { get; init; }
    public bool Primary { get; init; }
    public ScreenRect Bounds { get; init; }
    public ScreenRect WorkArea { get; init; }
    /// <summary>GDI device name (<c>\\.\DISPLAY2</c>); changes when monitors are reconnected.</summary>
    public string GdiDeviceName { get; init; } = "";

    /// <summary>Name in <paramref name="culture"/> (fallback names are localized, EDID names are not).</summary>
    public string DisplayName(CultureInfo? culture) => NameText?.Render(culture) ?? Name;

    public string KindName => Kind switch
    {
        MonitorKind.Internal => "internal",
        MonitorKind.External => "external",
        _ => "unknown",
    };

    public override string ToString() => $"#{Index} {GdiDeviceName} \"{Name}\" {KindName}{(Primary ? " primary" : "")} " +
        $"{Bounds.Width}x{Bounds.Height}@{Bounds.X},{Bounds.Y} id={Id}";
}

/// <summary>Enumerates connected monitors (implemented over Win32 in the companion; faked in tests).</summary>
public interface IMonitorSource
{
    IReadOnlyList<MonitorInfo> GetMonitors();
}

/// <summary>Persistent preferred monitor (companion settings).</summary>
public interface IDisplayPreferenceStore
{
    /// <summary>Preferred monitor id; <c>null</c> — do not manage the VLC window.</summary>
    string? PreferredId { get; }
    /// <summary>Last known name of the preferred monitor (for the tray while it is disconnected).</summary>
    string? PreferredName { get; }
    /// <summary>Remember (and persist) the preferred monitor; <c>null</c> clears it.</summary>
    void SetPreferred(string? id, string? name);
}

/// <summary>The VLC process and its main window as the display logic needs them.</summary>
public interface IVlcWindowHost
{
    /// <summary>PID of the VLC process the companion follows; <c>null</c> — VLC is not running.</summary>
    int? ProcessId { get; }
    /// <summary>Main window handle of <paramref name="pid"/>; 0 — no visible main window (yet).</summary>
    long FindMainWindow(int pid);
    /// <summary>GDI device name of the monitor that holds most of the window; <c>null</c> if unknown.</summary>
    string? GetWindowMonitor(long window);
    /// <summary>VLC answers HTTP with the companion password (fullscreen can be toggled).</summary>
    bool IsControllable { get; }
    /// <summary>Fullscreen state of the video output via HTTP; <c>null</c> — not controllable or failed.</summary>
    Task<bool?> GetFullscreenAsync(CancellationToken ct);
    Task SetFullscreenAsync(bool enabled, CancellationToken ct);
    /// <summary>
    /// Move the window to <paramref name="target"/>: a normal window keeps its size (clamped to the work area) and is
    /// centred, a maximized one is maximized again there, a borderless fullscreen one covers the target bounds.
    /// </summary>
    bool MoveWindow(long window, MonitorInfo target);
    /// <summary>
    /// VLC is kept paused for the resume question: a paused file may have no video output yet (VLC ignores
    /// fullscreen requests then), so waiting for fullscreen does not time out until playback goes on.
    /// </summary>
    bool PlaybackHeld => false;
    /// <summary>Un-minimize the window without activating it; <c>true</c> if it was minimized.</summary>
    bool RestoreWindow(long window) => false;
    /// <summary>When VLC was last seen fullscreen by the status polling; <c>null</c> — not since it started.</summary>
    DateTimeOffset? LastFullscreenAt => null;
    /// <summary>VLC is playing (not paused or stopped).</summary>
    bool IsPlaying => false;
    /// <summary>Pause playback (idempotent).</summary>
    Task PauseAsync(CancellationToken ct) => Task.CompletedTask;
}
