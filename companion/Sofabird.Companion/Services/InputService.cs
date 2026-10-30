using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Sofabird.Companion.Interop;
using Sofabird.Core.Input;
using Sofabird.Core.Protocol;

namespace Sofabird.Companion.Services;

/// <summary>
/// The phone as a touchpad and keyboard (<c>input</c> messages): <see cref="InputPlan"/> turns a message into actions,
/// this injects them with <c>SendInput</c> into whatever has the focus on the PC — a Moonlight stream passes them on to
/// its computer. Keys go as scan codes (games and Moonlight read those); text as Unicode units, or into a Moonlight
/// stream through the clipboard and Moonlight's Ctrl+Alt+Shift+V. Windows of programs running as administrator do not
/// take input from a normal process (UIPI).
/// </summary>
public sealed class InputService
{
    private static readonly int InputSize = Marshal.SizeOf<NativeMethods.INPUT>();
    private readonly ILogger _log;
    private readonly Action<Action> _uiInvoke;
    private readonly Func<bool> _streaming;

    /// <param name="uiInvoke">Runs on the UI thread (the clipboard needs an STA thread).</param>
    /// <param name="streaming">A Moonlight stream is on the screen now.</param>
    public InputService(ILogger<InputService> log, Action<Action> uiInvoke, Func<bool> streaming)
    {
        _log = log;
        _uiInvoke = uiInvoke;
        _streaming = streaming;
    }

    public void Handle(InputMessage input)
    {
        foreach (var action in InputPlan.For(input, _streaming()))
        {
            if (action is PasteIntoMoonlight paste) PasteIntoStream(paste.Text);
            else Send(action);
        }
    }

    private void PasteIntoStream(string text) => _uiInvoke(() =>
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (ExternalException ex)
        {
            _log.LogDebug(ex, "Input: the clipboard is busy, text not typed");
            return;
        }
        foreach (var key in InputPlan.PasteShortcut()) Send(key);
    });

    private void Send(InputAction action)
    {
        var inputs = action switch
        {
            MouseMove m => [Mouse(m.Dx, m.Dy, 0, NativeMethods.MOUSEEVENTF_MOVE)],
            MouseButtonAction b => [Mouse(0, 0, 0, ButtonFlag(b))],
            Wheel w => [Mouse(0, 0, unchecked((uint)w.Delta), w.Horizontal ? NativeMethods.MOUSEEVENTF_HWHEEL : NativeMethods.MOUSEEVENTF_WHEEL)],
            KeyAction k => [Key(k)],
            UnicodeUnit u =>
            [
                Keyboard(0, u.Unit, NativeMethods.KEYEVENTF_UNICODE),
                Keyboard(0, u.Unit, NativeMethods.KEYEVENTF_UNICODE | NativeMethods.KEYEVENTF_KEYUP),
            ],
            _ => Array.Empty<NativeMethods.INPUT>(),
        };
        if (inputs.Length == 0) return;
        if (NativeMethods.SendInput((uint)inputs.Length, inputs, InputSize) != inputs.Length)
            _log.LogDebug("Input: SendInput refused {Action} ({Error})", action, Marshal.GetLastWin32Error());
    }

    private static uint ButtonFlag(MouseButtonAction b) => (b.Button, b.Down) switch
    {
        (MouseButton.Left, true) => NativeMethods.MOUSEEVENTF_LEFTDOWN,
        (MouseButton.Left, false) => NativeMethods.MOUSEEVENTF_LEFTUP,
        (MouseButton.Right, true) => NativeMethods.MOUSEEVENTF_RIGHTDOWN,
        (MouseButton.Right, false) => NativeMethods.MOUSEEVENTF_RIGHTUP,
        (MouseButton.Middle, true) => NativeMethods.MOUSEEVENTF_MIDDLEDOWN,
        _ => NativeMethods.MOUSEEVENTF_MIDDLEUP,
    };

    private static NativeMethods.INPUT Mouse(int dx, int dy, uint data, uint flags) => new()
    {
        type = NativeMethods.INPUT_MOUSE,
        U = new NativeMethods.InputUnion { mi = new NativeMethods.MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags } },
    };

    /// <summary>A key as its scan code; the arrows, Home/End, Delete and the Windows key are "extended".</summary>
    private static NativeMethods.INPUT Key(KeyAction k)
    {
        var scan = (ushort)NativeMethods.MapVirtualKey(k.VirtualKey, NativeMethods.MAPVK_VK_TO_VSC);
        var flags = NativeMethods.KEYEVENTF_SCANCODE | (k.Down ? 0 : NativeMethods.KEYEVENTF_KEYUP);
        if (k.VirtualKey is >= 0x21 and <= 0x28 or 0x2E or 0x5B) flags |= NativeMethods.KEYEVENTF_EXTENDEDKEY;
        // Media keys have no useful scan code: sent by their virtual key.
        if (k.VirtualKey is >= 0xAD and <= 0xAF) return Keyboard(k.VirtualKey, 0, k.Down ? 0 : NativeMethods.KEYEVENTF_KEYUP);
        return Keyboard(0, scan, flags);
    }

    private static NativeMethods.INPUT Keyboard(ushort vk, ushort scan, uint flags) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        U = new NativeMethods.InputUnion { ki = new NativeMethods.KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } },
    };
}
