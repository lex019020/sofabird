using Sofabird.Core.Protocol;

namespace Sofabird.Core.Input;

/// <summary>One thing to inject on the PC (the companion turns these into <c>SendInput</c> calls).</summary>
public abstract record InputAction;

/// <summary>Relative mouse move, in pixels.</summary>
public sealed record MouseMove(int Dx, int Dy) : InputAction;

public enum MouseButton { Left, Right, Middle }

public sealed record MouseButtonAction(MouseButton Button, bool Down) : InputAction;

/// <summary>Wheel by <paramref name="Delta"/> (120 = one notch; positive — up / right).</summary>
public sealed record Wheel(int Delta, bool Horizontal) : InputAction;

/// <summary>A key by its virtual-key code (sent as a scan code, which games and Moonlight see too).</summary>
public sealed record KeyAction(ushort VirtualKey, bool Down) : InputAction;

/// <summary>One UTF-16 unit typed as itself (<c>KEYEVENTF_UNICODE</c>): any language, no keyboard layout needed.</summary>
public sealed record UnicodeUnit(char Unit) : InputAction;

/// <summary>
/// Text for a Moonlight stream: Moonlight does not pass Unicode key events on, but it types the clipboard on the other
/// computer on Ctrl+Alt+Shift+V — so the text goes to the clipboard and that shortcut is pressed.
/// </summary>
public sealed record PasteIntoMoonlight(string Text) : InputAction;

/// <summary>
/// The phone's <c>input</c> messages (touchpad and keyboard, <c>caps.input</c>) as input actions (pure, tested):
/// <c>move</c> (<c>dx</c>, <c>dy</c>), <c>click</c> / <c>down</c> / <c>up</c> (<c>button</c>), <c>scroll</c>
/// (<c>dx</c>, <c>dy</c> in wheel units, positive — down / right), <c>text</c>, <c>key</c> (a name, with
/// <c>mods</c>). Anything unknown gives nothing.
/// </summary>
public static class InputPlan
{
    /// <summary>A phone sends at most this much per message: a runaway value does not throw the cursor away.</summary>
    public const int MaxMove = 4000;
    public const int MaxText = 4000;

    private static readonly Dictionary<string, ushort> Keys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"] = 0x0D, ["backspace"] = 0x08, ["tab"] = 0x09, ["escape"] = 0x1B, ["space"] = 0x20,
        ["pageup"] = 0x21, ["pagedown"] = 0x22, ["end"] = 0x23, ["home"] = 0x24,
        ["left"] = 0x25, ["up"] = 0x26, ["right"] = 0x27, ["down"] = 0x28, ["delete"] = 0x2E,
        ["win"] = 0x5B, ["f4"] = 0x73, ["f5"] = 0x74, ["f11"] = 0x7A,
        ["volumemute"] = 0xAD, ["volumedown"] = 0xAE, ["volumeup"] = 0xAF,
        ["c"] = 0x43, ["v"] = 0x56, ["a"] = 0x41, ["z"] = 0x5A, ["l"] = 0x4C, ["d"] = 0x44,
    };

    private static readonly Dictionary<string, ushort> Modifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = 0x11, ["alt"] = 0x12, ["shift"] = 0x10, ["win"] = 0x5B,
    };

    public const ushort VkControl = 0x11;
    public const ushort VkMenu = 0x12;
    public const ushort VkShift = 0x10;
    public const ushort VkV = 0x56;

    /// <param name="toStream">A Moonlight stream is on the screen: text goes through <see cref="PasteIntoMoonlight"/>.</param>
    public static IReadOnlyList<InputAction> For(InputMessage input, bool toStream)
    {
        switch (input.Kind)
        {
            case "move":
                var (dx, dy) = (Clamp(input.Dx), Clamp(input.Dy));
                return dx == 0 && dy == 0 ? [] : [new MouseMove(dx, dy)];
            case "click":
                return Button(input.Button) is { } b ? [new MouseButtonAction(b, true), new MouseButtonAction(b, false)] : [];
            case "down" or "up":
                return Button(input.Button) is { } button ? [new MouseButtonAction(button, input.Kind == "down")] : [];
            case "scroll":
                var actions = new List<InputAction>();
                if (Clamp(input.Dy) is var v and not 0) actions.Add(new Wheel(-v, Horizontal: false));
                if (Clamp(input.Dx) is var h and not 0) actions.Add(new Wheel(h, Horizontal: true));
                return actions;
            case "text":
                var text = input.Text ?? "";
                if (text.Length == 0) return [];
                if (text.Length > MaxText) text = text[..MaxText];
                return toStream ? [new PasteIntoMoonlight(text)] : text.Select(c => (InputAction)new UnicodeUnit(c)).ToList();
            case "key":
                return Key(input.Key, input.Mods);
            default:
                return [];
        }
    }

    /// <summary>The keys of Moonlight's "type the clipboard" shortcut, pressed and released.</summary>
    public static IReadOnlyList<InputAction> PasteShortcut() =>
        Chord([VkControl, VkMenu, VkShift], VkV);

    private static List<InputAction> Key(string? name, IReadOnlyList<string>? mods)
    {
        if (name is null || !Keys.TryGetValue(name, out var vk)) return [];
        var held = new List<ushort>();
        foreach (var m in mods ?? [])
        {
            if (!Modifiers.TryGetValue(m, out var mvk)) return []; // an unknown modifier: better nothing than the wrong key
            if (!held.Contains(mvk)) held.Add(mvk);
        }
        return Chord(held, vk);
    }

    private static List<InputAction> Chord(IReadOnlyList<ushort> mods, ushort vk)
    {
        var list = new List<InputAction>();
        list.AddRange(mods.Select(m => new KeyAction(m, true)));
        list.Add(new KeyAction(vk, true));
        list.Add(new KeyAction(vk, false));
        list.AddRange(mods.Reverse().Select(m => new KeyAction(m, false)));
        return list;
    }

    private static MouseButton? Button(string? name) => (name ?? "left").ToLowerInvariant() switch
    {
        "left" => MouseButton.Left,
        "right" => MouseButton.Right,
        "middle" => MouseButton.Middle,
        _ => null,
    };

    private static int Clamp(int? value) => Math.Clamp(value ?? 0, -MaxMove, MaxMove);
}
