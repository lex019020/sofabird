using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using VlcRemote.Companion.Interop;

namespace VlcRemote.Companion.Services;

/// <summary>
/// Питание: keep-awake через <c>SetThreadExecutionState</c> (состояние привязано к потоку, поэтому
/// вызывается всегда на UI-потоке), выключение экрана <c>SC_MONITORPOWER</c>, сон
/// <c>SetSuspendState</c>, выключение <c>shutdown.exe</c>.
/// </summary>
public sealed class PowerService
{
    private readonly ILogger _log;
    private readonly Action<Action> _uiInvoke;
    private bool _keepAwakeEnabled;
    private bool _keepAwakeActive;

    public PowerService(ILogger<PowerService> log, Action<Action> uiInvoke)
    {
        _log = log;
        _uiInvoke = uiInvoke;
    }

    /// <summary>Пользовательский флаг «не давать уснуть во время фильма».</summary>
    public bool KeepAwakeEnabled
    {
        get => _keepAwakeEnabled;
        set
        {
            _keepAwakeEnabled = value;
            if (!value) ApplyKeepAwake(false);
        }
    }

    public bool KeepAwakeActive => _keepAwakeActive;

    /// <summary>Вызывается менеджером VLC при смене состояния воспроизведения.</summary>
    public void OnPlaybackState(bool playing) => ApplyKeepAwake(playing && _keepAwakeEnabled);

    private void ApplyKeepAwake(bool on)
    {
        if (_keepAwakeActive == on) return;
        _keepAwakeActive = on;
        _uiInvoke(() =>
        {
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
            // Надёжнее «разбудить» экран имитацией ввода, затем попросить включить явно.
            var input = new NativeMethods.INPUT[]
            {
                new() { type = NativeMethods.INPUT_MOUSE, U = new NativeMethods.InputUnion { mi = new NativeMethods.MOUSEINPUT { dwFlags = NativeMethods.MOUSEEVENTF_MOVE } } },
            };
            NativeMethods.SendInput(1, input, Marshal.SizeOf<NativeMethods.INPUT>());
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
