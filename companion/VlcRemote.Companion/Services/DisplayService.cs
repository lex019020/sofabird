using System.Text;
using Microsoft.Extensions.Logging;
using VlcRemote.Companion.Interop;
using VlcRemote.Core.Protocol;
using VlcRemote.Companion.Localization;

namespace VlcRemote.Companion.Services;

/// <summary>
/// Экраны и окно VLC: список мониторов (<see cref="Screen.AllScreens"/>, порядок стабилизирован
/// по имени устройства), перенос главного окна VLC (найденного по PID через EnumWindows) на монитор.
/// </summary>
public sealed class DisplayService
{
    private readonly ILogger _log;

    public DisplayService(ILogger<DisplayService> log) => _log = log;

    public IReadOnlyList<DisplayEntry> ListDisplays()
    {
        var screens = Screen.AllScreens.OrderBy(s => s.DeviceName, StringComparer.Ordinal).ToArray();
        var list = new List<DisplayEntry>(screens.Length);
        for (var i = 0; i < screens.Length; i++)
        {
            var s = screens[i];
            list.Add(new DisplayEntry
            {
                Index = i,
                Name = FriendlyName(s, i),
                Primary = s.Primary,
                Bounds = new DisplayBounds { X = s.Bounds.X, Y = s.Bounds.Y, Width = s.Bounds.Width, Height = s.Bounds.Height },
            });
        }
        return list;
    }

    public Screen? GetScreen(int index)
    {
        var screens = Screen.AllScreens.OrderBy(s => s.DeviceName, StringComparer.Ordinal).ToArray();
        return index >= 0 && index < screens.Length ? screens[index] : null;
    }

    /// <summary>Индекс экрана, на котором сейчас находится окно VLC (по центру окна); <c>null</c>, если окно не найдено.</summary>
    public int? FindDisplayOfProcess(int pid)
    {
        var hwnd = FindMainWindow(pid);
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var r)) return null;
        var center = new Point((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
        var screens = Screen.AllScreens.OrderBy(s => s.DeviceName, StringComparer.Ordinal).ToArray();
        for (var i = 0; i < screens.Length; i++)
            if (screens[i].Bounds.Contains(center)) return i;
        return null;
    }

    private static string FriendlyName(Screen s, int index)
    {
        var name = s.DeviceName.Replace(@"\\.\", "");
        return s.Primary ? UiText.DisplayPrimary(name, s.Bounds.Width, s.Bounds.Height) : UiText.DisplayNormal(name, s.Bounds.Width, s.Bounds.Height);
    }

    /// <summary>
    /// Главное окно процесса: верхнеуровневое, видимое, без владельца, с непустым заголовком.
    /// Предпочитается заголовок, оканчивающийся на «VLC media player».
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

    /// <summary>Перенести окно VLC на экран <paramref name="displayIndex"/> (окно должно быть не в полноэкранном режиме).</summary>
    public bool MoveWindowToDisplay(int pid, int displayIndex)
    {
        var screen = GetScreen(displayIndex) ?? throw new ArgumentOutOfRangeException(nameof(displayIndex), "no such display");
        var hwnd = FindMainWindow(pid);
        if (hwnd == IntPtr.Zero)
        {
            _log.LogWarning("VLC main window for pid {Pid} not found", pid);
            return false;
        }
        var b = screen.Bounds;
        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        var w = b.Width * 2 / 3;
        var h = b.Height * 2 / 3;
        var ok = NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, b.X + (b.Width - w) / 2, b.Y + (b.Height - h) / 2, w, h,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_SHOWWINDOW);
        if (ok) NativeMethods.SetForegroundWindow(hwnd);
        _log.LogInformation("Moved VLC window {Hwnd} to display {Index} ({Device}): {Ok}", hwnd, displayIndex, screen.DeviceName, ok);
        return ok;
    }
}
