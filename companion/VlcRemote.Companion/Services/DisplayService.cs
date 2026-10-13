using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using VlcRemote.Companion.Interop;
using VlcRemote.Core.Displays;

namespace VlcRemote.Companion.Services;

/// <summary>
/// Monitors and the VLC window on Windows: <see cref="Screen.AllScreens"/> (GDI sources, bounds) joined with the CCD
/// API (<see cref="DisplayConfig"/>: EDID friendly names, stable device paths, connector type) by
/// <see cref="MonitorTopology"/>; window lookup (EnumWindows by PID) and moving the window between monitors.
/// The list is cached briefly and refreshed on <see cref="SystemEvents.DisplaySettingsChanged"/>; every change of the
/// topology is logged (one line per monitor) so it can be checked remotely in the companion log.
/// </summary>
public sealed class DisplayService : IMonitorSource, IDisposable
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(2);

    private readonly ILogger _log;
    private readonly object _lock = new();
    private IReadOnlyList<MonitorInfo>? _cached;
    private long _cachedAt;
    private string? _signature;
    private int? _lastCcdError;

    public DisplayService(ILogger<DisplayService> log)
    {
        _log = log;
        if (DisplayConfig.CheckLayout() is { } problem)
            _log.LogError("DisplayConfig struct layout mismatch: {Problem}", problem);
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    /// <summary>The set of monitors (or their names/ids/geometry) changed.</summary>
    public event Action? TopologyChanged;

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        bool changed;
        IReadOnlyList<MonitorInfo> list;
        lock (_lock)
        {
            if (_cached is not null && Environment.TickCount64 - _cachedAt < CacheTtl.TotalMilliseconds) return _cached;
            list = Enumerate();
            _cached = list;
            _cachedAt = Environment.TickCount64;
            var signature = MonitorTopology.Signature(list);
            changed = _signature is not null && signature != _signature;
            if (signature != _signature)
            {
                _signature = signature;
                _log.LogInformation("Monitors ({Count}):", list.Count);
                foreach (var m in list)
                    _log.LogInformation("  Monitor {Index}: \"{Name}\" kind={Kind} primary={Primary} gdi={Gdi} bounds={W}x{H}@{X},{Y} id={Id}",
                        m.Index, m.Name, m.KindName, m.Primary, m.GdiDeviceName, m.Bounds.Width, m.Bounds.Height, m.Bounds.X, m.Bounds.Y, m.Id);
            }
        }
        if (changed)
        {
            try { TopologyChanged?.Invoke(); }
            catch (Exception ex) { _log.LogWarning(ex, "TopologyChanged handler failed"); }
        }
        return list;
    }

    private IReadOnlyList<MonitorInfo> Enumerate()
    {
        var screens = Screen.AllScreens
            .Select(s => new GdiScreen(s.DeviceName, ToRect(s.Bounds), ToRect(s.WorkingArea), s.Primary))
            .ToList();
        IReadOnlyList<DisplayTarget> targets;
        try
        {
            targets = DisplayConfig.QueryActiveTargets(out var error);
            if (error != 0 && error != _lastCcdError)
                _log.LogWarning("QueryDisplayConfig failed ({Error}); monitors are identified by GDI names only", error);
            _lastCcdError = error;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "QueryDisplayConfig failed; monitors are identified by GDI names only");
            targets = [];
        }
        return MonitorTopology.Build(screens, targets);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        lock (_lock) _cached = null;
        _ = Task.Run(() =>
        {
            try { GetMonitors(); }
            catch (Exception ex) { _log.LogWarning(ex, "Monitor refresh failed"); }
        });
    }

    private static ScreenRect ToRect(Rectangle r) => new(r.X, r.Y, r.Width, r.Height);

    /// <summary>The WinForms screen for a monitor (for placing companion windows).</summary>
    public static Screen? GetScreen(MonitorInfo? monitor) =>
        monitor is null ? null
            : Screen.AllScreens.FirstOrDefault(s => string.Equals(s.DeviceName, monitor.GdiDeviceName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Main window of a process: top-level, visible, without an owner, with a non-empty title; a title ending
    /// with "VLC media player" is preferred.
    /// </summary>
    public static IntPtr FindMainWindow(int pid)
    {
        var best = IntPtr.Zero;
        var fallback = IntPtr.Zero;
        NativeMethods.EnumWindows((h, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(h, out var p);
            if (p != pid || !NativeMethods.IsWindowVisible(h)) return true;
            if (NativeMethods.GetWindow(h, NativeMethods.GW_OWNER) != IntPtr.Zero) return true;
            var len = NativeMethods.GetWindowTextLength(h);
            if (len <= 0) return true;
            var sb = new StringBuilder(len + 1);
            NativeMethods.GetWindowText(h, sb, sb.Capacity);
            var title = sb.ToString();
            if (title.EndsWith("VLC media player", StringComparison.OrdinalIgnoreCase))
            {
                best = h;
                return false;
            }
            if (fallback == IntPtr.Zero) fallback = h;
            return true;
        }, IntPtr.Zero);
        return best != IntPtr.Zero ? best : fallback;
    }

    /// <summary>GDI device name of the monitor with the largest part of the window; <c>null</c> — none.</summary>
    public static string? GetWindowMonitorDevice(IntPtr hwnd) => GetMonitorOf(hwnd, out var info) ? info.szDevice : null;

    private static bool GetMonitorOf(IntPtr hwnd, out NativeMethods.MONITORINFOEX info)
    {
        info = new NativeMethods.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONULL);
        return monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref info);
    }

    /// <summary>
    /// Move a window to <paramref name="target"/> without activating it. A borderless window covering its monitor
    /// (fullscreen VLC that cannot be unfullscreened over HTTP) is stretched over the target bounds; a maximized one
    /// is restored, moved and maximized again; a normal one keeps its size (clamped to the work area) and is centred.
    /// </summary>
    public bool MoveWindowToMonitor(IntPtr hwnd, MonitorInfo target)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var r))
        {
            _log.LogWarning("GetWindowRect failed for window {Hwnd:X}", hwnd);
            return false;
        }
        const uint flags = NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE;
        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64();
        var hasCaption = (style & NativeMethods.WS_CAPTION) == NativeMethods.WS_CAPTION;
        var coversMonitor = GetMonitorOf(hwnd, out var current)
            && r.Left <= current.rcMonitor.Left && r.Top <= current.rcMonitor.Top
            && r.Right >= current.rcMonitor.Right && r.Bottom >= current.rcMonitor.Bottom;
        bool ok;
        if (!hasCaption && coversMonitor)
        {
            var b = target.Bounds;
            ok = NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, b.X, b.Y, b.Width, b.Height, flags);
            _log.LogInformation("Moved borderless fullscreen window {Hwnd:X} to {Gdi}: {Ok}", hwnd, target.GdiDeviceName, ok);
            return ok;
        }

        var zoomed = NativeMethods.IsZoomed(hwnd);
        if (zoomed || NativeMethods.IsIconic(hwnd))
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWNOACTIVATE);
            NativeMethods.GetWindowRect(hwnd, out r);
        }
        var work = target.WorkArea.Width > 0 ? target.WorkArea : target.Bounds;
        var w = Math.Min(r.Right - r.Left, work.Width);
        var h = Math.Min(r.Bottom - r.Top, work.Height);
        if (w < 200 || h < 150)
        {
            w = work.Width * 2 / 3;
            h = work.Height * 2 / 3;
        }
        ok = NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, work.X + (work.Width - w) / 2, work.Y + (work.Height - h) / 2, w, h, flags);
        if (zoomed) NativeMethods.ShowWindow(hwnd, NativeMethods.SW_MAXIMIZE);
        _log.LogInformation("Moved window {Hwnd:X} to {Gdi} ({W}x{H}{Maximized}): {Ok}", hwnd, target.GdiDeviceName, w, h, zoomed ? ", maximized" : "", ok);
        return ok;
    }

    public void Dispose() => SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
}
