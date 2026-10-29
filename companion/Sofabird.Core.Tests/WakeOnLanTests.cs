using Sofabird.Core.Power;
using Xunit;

namespace Sofabird.Core.Tests;

public sealed class WakeOnLanTests
{
    // What the inspect script printed on the developer PC (Realtek 2.5GbE; the Wi‑Fi card has no such setting).
    private const string OneAdapter =
        """[{"name":"Ethernet","description":"Realtek Gaming 2.5GbE Family Controller","mac":"BC-FC-E7-08-6A-02","wired":true,"magicPacket":true}]""";

    private const string Armed = "HID Keyboard Device\r\nRealtek Gaming 2.5GbE Family Controller\r\nHID Keyboard Device (001)\r\n";

    [Fact]
    public void An_adapter_with_magic_packet_that_may_wake_the_pc_is_ready()
    {
        var adapters = WakeOnLan.Parse(OneAdapter, Armed);
        var a = Assert.Single(adapters);
        Assert.Equal(("Ethernet", "BC:FC:E7:08:6A:02", true), (a.Name, a.Mac, a.Wired));
        Assert.True(a.Ready);
        Assert.Equal(WakeState.Ready, WakeOnLan.State(adapters));
        Assert.Empty(WakeOnLan.ToFix(adapters));
    }

    [Fact]
    public void Off_or_not_armed_needs_setup_and_none_is_unsupported()
    {
        var notArmed = WakeOnLan.Parse(OneAdapter, "HID Keyboard Device");
        Assert.Equal(WakeState.NeedsSetup, WakeOnLan.State(notArmed));
        Assert.Equal("Ethernet", Assert.Single(WakeOnLan.ToFix(notArmed)).Name);

        var off = WakeOnLan.Parse(OneAdapter.Replace("\"magicPacket\":true", "\"magicPacket\":false", StringComparison.Ordinal), Armed);
        Assert.Equal(WakeState.NeedsSetup, WakeOnLan.State(off));

        Assert.Equal(WakeState.Unsupported, WakeOnLan.State(WakeOnLan.Parse("[]", Armed)));
        Assert.Empty(WakeOnLan.Parse(null, null));
        Assert.Empty(WakeOnLan.Parse("not json", ""));
    }

    [Fact]
    public void Output_before_the_json_and_a_wired_adapter_first()
    {
        var adapters = WakeOnLan.Parse(
            "Preparing modules for first use.\r\n" +
            """[{"name":"Wi-Fi","description":"Intel Wi-Fi 6","mac":"04-68-74-9A-2B-FB","wired":false,"magicPacket":true},""" +
            """{"name":"Ethernet","description":"Intel I219","mac":"bc:fc:e7:08:6a:02","wired":true,"magicPacket":false}]""",
            "Intel Wi-Fi 6");
        Assert.Equal(["Ethernet", "Wi-Fi"], adapters.Select(a => a.Name));
        Assert.Equal(["Ethernet"], WakeOnLan.ToFix(adapters).Select(a => a.Name));
    }

    [Theory]
    [InlineData("BC-FC-E7-08-6A-02", "BC:FC:E7:08:6A:02")]
    [InlineData("bcfce7086a02", "BC:FC:E7:08:6A:02")]
    [InlineData("BC:FC:E7:08:6A:02", "BC:FC:E7:08:6A:02")]
    [InlineData("000000000000", null)]
    [InlineData("BC-FC-E7-08-6A", null)]
    [InlineData("", null)]
    public void Mac_addresses(string text, string? mac) => Assert.Equal(mac, WakeOnLan.NormalizeMac(text));

    [Fact]
    public void The_fix_script_quotes_adapter_names()
    {
        var script = WakeOnLan.FixScript(["Ethernet", "Kid's LAN"]);
        Assert.Contains("@('Ethernet','Kid''s LAN')", script, StringComparison.Ordinal);
        Assert.Contains("*WakeOnMagicPacket", script, StringComparison.Ordinal);
        Assert.Contains("powercfg /deviceenablewake", script, StringComparison.Ordinal);
    }
}
