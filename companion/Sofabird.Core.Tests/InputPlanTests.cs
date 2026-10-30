using Sofabird.Core.Input;
using Sofabird.Core.Protocol;
using Xunit;

namespace Sofabird.Core.Tests;

public sealed class InputPlanTests
{
    private static IReadOnlyList<InputAction> Plan(InputMessage m, bool toStream = false) => InputPlan.For(m, toStream);

    [Fact]
    public void Moves_clicks_and_scrolls()
    {
        Assert.Equal([new MouseMove(12, -3)], Plan(new InputMessage { Kind = "move", Dx = 12, Dy = -3 }));
        Assert.Empty(Plan(new InputMessage { Kind = "move", Dx = 0, Dy = 0 }));
        Assert.Equal([new MouseMove(InputPlan.MaxMove, -InputPlan.MaxMove)], Plan(new InputMessage { Kind = "move", Dx = 99999, Dy = -99999 }));

        Assert.Equal(
            [new MouseButtonAction(MouseButton.Left, true), new MouseButtonAction(MouseButton.Left, false)],
            Plan(new InputMessage { Kind = "click" }));
        Assert.Equal(
            [new MouseButtonAction(MouseButton.Right, true), new MouseButtonAction(MouseButton.Right, false)],
            Plan(new InputMessage { Kind = "click", Button = "right" }));
        Assert.Equal([new MouseButtonAction(MouseButton.Left, true)], Plan(new InputMessage { Kind = "down" }));
        Assert.Equal([new MouseButtonAction(MouseButton.Left, false)], Plan(new InputMessage { Kind = "up", Button = "left" }));
        Assert.Empty(Plan(new InputMessage { Kind = "click", Button = "fourth" }));

        // Down on the phone (dy > 0) scrolls the page down: a negative wheel delta.
        Assert.Equal([new Wheel(-120, false), new Wheel(40, true)], Plan(new InputMessage { Kind = "scroll", Dy = 120, Dx = 40 }));
    }

    [Fact]
    public void Text_is_typed_unit_by_unit_or_pasted_into_a_stream()
    {
        Assert.Equal([new UnicodeUnit('П'), new UnicodeUnit('р'), new UnicodeUnit('и')], Plan(new InputMessage { Kind = "text", Text = "При" }));
        // A character outside the BMP is two units, both sent.
        Assert.Equal(2, Plan(new InputMessage { Kind = "text", Text = "😀" }).Count);
        Assert.Equal([new PasteIntoMoonlight("Привет")], Plan(new InputMessage { Kind = "text", Text = "Привет" }, toStream: true));
        Assert.Empty(Plan(new InputMessage { Kind = "text", Text = "" }));
    }

    [Fact]
    public void Keys_with_modifiers_press_in_order_and_release_backwards()
    {
        Assert.Equal([new KeyAction(0x0D, true), new KeyAction(0x0D, false)], Plan(new InputMessage { Kind = "key", Key = "enter" }));
        Assert.Equal(
            [new KeyAction(0x12, true), new KeyAction(0x09, true), new KeyAction(0x09, false), new KeyAction(0x12, false)],
            Plan(new InputMessage { Kind = "key", Key = "Tab", Mods = ["alt"] }));
        Assert.Equal(
            [
                new KeyAction(0x11, true), new KeyAction(0x10, true), new KeyAction(0x1B, true),
                new KeyAction(0x1B, false), new KeyAction(0x10, false), new KeyAction(0x11, false),
            ],
            Plan(new InputMessage { Kind = "key", Key = "escape", Mods = ["ctrl", "shift", "ctrl"] }));
        Assert.Empty(Plan(new InputMessage { Kind = "key", Key = "teleport" }));
        Assert.Empty(Plan(new InputMessage { Kind = "key", Key = "enter", Mods = ["hyper"] }));
        Assert.Empty(Plan(new InputMessage { Kind = "wiggle" }));
    }

    [Fact]
    public void Moonlight_paste_shortcut()
    {
        var keys = InputPlan.PasteShortcut().Cast<KeyAction>().ToList();
        Assert.Equal([0x11, 0x12, 0x10, 0x56, 0x56, 0x10, 0x12, 0x11], keys.Select(k => (int)k.VirtualKey));
        Assert.Equal([true, true, true, true, false, false, false, false], keys.Select(k => k.Down));
    }

    [Fact]
    public void Input_message_round_trips_as_json()
    {
        var json = ProtocolJson.Serialize(new InputMessage { Kind = "key", Key = "tab", Mods = ["alt"] });
        Assert.Equal("""{"t":"input","kind":"key","key":"tab","mods":["alt"]}""", json);
        var back = Assert.IsType<InputMessage>(ProtocolJson.Deserialize("""{"t":"input","kind":"move","dx":5,"dy":-2}"""));
        Assert.Equal(("move", 5, -2), (back.Kind, back.Dx, back.Dy));
    }
}
