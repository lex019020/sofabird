using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using VlcRemote.Companion.Interop;

namespace VlcRemote.Companion.Services;

/// <summary>
/// Power: keep-awake via <c>SetThreadExecutionState</c> (the state is bound to the thread, so
/// it is always called on the UI thread), display off via <c>SC_MONITORPOWER</c>, sleep via
/// <c>SetSuspendState</c>, shutdown via <c>shutdown.exe</c>.
/// </summary>
public sealed class PowerService
{
    private readonly ILogger _log;
    private readonly Action<Action> _uiInvoke;
    private volatile bool _keepAwakeEnabled;
    private volatile bool _playing;
    private bool _keepAwakeActive; // UI thread only

    public PowerService(ILogger<PowerService> log, Action<Action> uiInvoke)
    {
        _log = log;
        _uiInvoke = uiInvoke;
    }

    /// <summary>User flag "keep the PC awake during a movie".</summary>
    public bool KeepAwakeEnabled
    {
        get => _keepAwakeEnabled;
        set
        {
            _keepAwakeEnabled = value;
            ApplyKeepAwake();
        }
    }

    /// <summary>The playback state changed (any thread).</summary>
    public void OnPlaybackState(bool playing)
    {
        _playing = playing;
        ApplyKeepAwake();
    }

    private void ApplyKeepAwake()
    {
        _uiInvoke(() =>
        {
            var on = _keepAwakeEnabled && _playing;
            if (_keepAwakeActive == on) return;
            _keepAwakeActive = on;
            var flags = on
                ? NativeMethods.ES_CONTINUOUS | NativeMethods.ES_DISPLAY_REQUIRED | NativeMethods.ES_SYSTEM_REQUIRED
                : NativeMethods.ES_CONTINUOUS;
            var prev = NativeMethods.SetThreadExecutionState(flags);
            if (prev == 0) _log.LogWarning("SetThreadExecutionState failed: {Error}", Marshal.GetLastWin32Error());
            else _log.LogInformation("Keep-awake {State}", on ? "on" : "off");
        });
    }

    public void SetDisplayPower(bool on)
    {
        if (on)
        {
            // It is more reliable to "wake" the display by simulating input, then ask to turn it on explicitly.
            var input = new NativeMethods.INPUT[]
            {
                new() { type = NativeMethods.INPUT_MOUSE, U = new NativeMethods.InputUnion { mi = new NativeMethods.MOUSEINPUT { dwFlags = NativeMethods.MOUSEEVENTF_MOVE } } },
            };
            _ = NativeMethods.SendInput(1, input, Marshal.SizeOf<NativeMethods.INPUT>()); // best effort: SC_MONITORPOWER follows
            NativeMethods.SendMessageTimeout(NativeMethods.HWND_BROADCAST, NativeMethods.WM_SYSCOMMAND,
                new IntPtr(NativeMethods.SC_MONITORPOWER), new IntPtr(-1), NativeMethods.SMTO_ABORTIFHUNG, 1000, out _);
        }
        else
        {
            NativeMethods.SendMessageTimeout(NativeMethods.HWND_BROADCAST, NativeMethods.WM_SYSCOMMAND,
                new IntPtr(NativeMethods.SC_MONITORPOWER), new IntPtr(2), NativeMethods.SMTO_ABORTIFHUNG, 1000, out _);
        }
        _log.LogInformation("Display power {State}", on ? "on" : "off");
    }

    public void Sleep()
    {
        _log.LogInformation("Sleep requested");
        if (!NativeMethods.SetSuspendState(false, false, false))
            _log.LogWarning("SetSuspendState failed: {Error}", Marshal.GetLastWin32Error());
    }

    public void Shutdown()
    {
        _log.LogInformation("Shutdown requested");
        Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 5") { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
    }
}
