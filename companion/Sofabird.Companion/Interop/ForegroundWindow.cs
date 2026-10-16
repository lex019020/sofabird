using System.Runtime.InteropServices;

namespace Sofabird.Companion.Interop;

/// <summary>
/// Brings a window of another program to the front from the companion, which runs in the background: Windows refuses
/// <c>SetForegroundWindow</c> to a process that did not get the last input, so the call is made while attached to the
/// input of the current foreground window, and once more after a synthetic Alt press if that is not enough.
/// </summary>
internal static class ForegroundWindow
{
    public static bool Activate(IntPtr hwnd)
    {
        if (NativeMethods.IsIconic(hwnd)) NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        if (NativeMethods.GetForegroundWindow() == hwnd) return true;
        SetWhileAttached(hwnd);
        if (NativeMethods.GetForegroundWindow() == hwnd) return true;
        PressAlt();
        SetWhileAttached(hwnd);
        return NativeMethods.GetForegroundWindow() == hwnd;
    }

    /// <summary>The class name of a window (SDL windows, such as Moonlight's stream, are <c>SDL_app</c>).</summary>
    public static string ClassName(IntPtr hwnd)
    {
        var buffer = new char[256];
        var length = NativeMethods.GetClassName(hwnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : "";
    }

    /// <summary>Visible top-level windows of a process.</summary>
    public static IReadOnlyList<IntPtr> WindowsOf(int pid)
    {
        var list = new List<IntPtr>();
        NativeMethods.EnumWindows((h, _) =>
        {
            if (NativeMethods.GetWindowThreadProcessId(h, out var p) != 0 && p == pid && NativeMethods.IsWindowVisible(h)) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static void SetWhileAttached(IntPtr hwnd)
    {
        var foreground = NativeMethods.GetForegroundWindow();
        var foregroundThread = foreground == IntPtr.Zero ? 0 : NativeMethods.GetWindowThreadProcessId(foreground, out _);
        var self = NativeMethods.GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != self && NativeMethods.AttachThreadInput(self, foregroundThread, true);
        try
        {
            NativeMethods.BringWindowToTop(hwnd);
            NativeMethods.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) NativeMethods.AttachThreadInput(self, foregroundThread, false);
        }
    }

    private static void PressAlt() => PressKey(NativeMethods.VK_MENU);

    /// <summary>A key press and release, delivered to the foreground window.</summary>
    public static void PressKey(ushort virtualKey)
    {
        var input = new NativeMethods.INPUT[]
        {
            new() { type = NativeMethods.INPUT_KEYBOARD, U = new NativeMethods.InputUnion { ki = new NativeMethods.KEYBDINPUT { wVk = virtualKey } } },
            new() { type = NativeMethods.INPUT_KEYBOARD, U = new NativeMethods.InputUnion { ki = new NativeMethods.KEYBDINPUT { wVk = virtualKey, dwFlags = NativeMethods.KEYEVENTF_KEYUP } } },
        };
        _ = NativeMethods.SendInput((uint)input.Length, input, Marshal.SizeOf<NativeMethods.INPUT>());
    }
}
