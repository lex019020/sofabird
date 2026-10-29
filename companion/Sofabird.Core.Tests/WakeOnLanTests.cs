using Sofabird.Core.Power;
using Xunit;

namespace Sofabird.Core.Tests;

public sealed class WakeOnLanTests
{
    // What the inspect script printed on the developer PC (Realtek 2.5GbE; the Wi‑Fi card has no such setting).
    private const string OneAdapter =
        """[{"name":"Ethernet","description":"Realtek Gaming 2.5GbE Family Controller","mac":"02-1A-2B-3C-4D-5E","wired":true,"magicPacket":true}]""";

    private const string Armed = "HID Keyboard Device\r\nRealtek Gaming 2.5GbE Family Controller\r\nHID Keyboard Device (001)\r\n";

    [Fact]
    public void An_adapter_with_magic_packet_that_may_wake_the_pc_is_ready()
    {
        var adapters = WakeOnLan.Parse(OneAdapter, Armed);
        var a = Assert.Single(adapters);
        Assert.Equal(("Ethernet", "02:1A:2B:3C:4D:5E", true), (a.Name, a.Mac, a.Wired));
        Assert.True(a.Ready);
        Assert.Equal(WakeState.Ready, WakeOnLan.State(adapters));
        Assert.Empty(WakeOnLan.ToFix(adapters));
        Assert.Equal(["02:1A:2B:3C:4D:5E"], WakeOnLan.WakeMacs(adapters));
    }

    [Fact]
    public void Off_or_not_armed_needs_setup_and_none_is_unsupported()
    {
        var notArmed = WakeOnLan.Parse(OneAdapter, "HID Keyboard Device");
        Assert.Equal(WakeState.NeedsSetup, WakeOnLan.State(notArmed));
        Assert.Empty(WakeOnLan.WakeMacs(notArmed));
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
            """[{"name":"Wi-Fi","description":"Intel Wi-Fi 6","mac":"02-1A-2B-3C-4D-5F","wired":false,"magicPacket":true},""" +
            """{"name":"Ethernet","description":"Intel I219","mac":"02:1a:2b:3c:4d:5e","wired":true,"magicPacket":false}]""",
            "Intel Wi-Fi 6");
        Assert.Equal(["Ethernet", "Wi-Fi"], adapters.Select(a => a.Name));
        Assert.Equal(["Ethernet"], WakeOnLan.ToFix(adapters).Select(a => a.Name));
    }

    [Fact]
    public void An_adapter_the_firmware_does_not_let_wake_the_pc_is_blocked_and_gives_no_macs()
    {
        // The TV laptop (2026-10-29): on the cable, everything turned on, yet only the Wi-Fi card may wake it from sleep.
        const string json =
            """[{"name":"Ethernet","description":"Realtek PCIe GbE Family Controller","mac":"02-AA-BB-CC-DD-01","wired":true,""" +
            """ "connected":true,"magicPacket":true},{"name":"Wi-Fi","description":"Intel(R) Wireless-AC 9462",""" +
            """ "mac":"02-AA-BB-CC-DD-02","wired":false,"connected":false,"magicPacket":true}]""";
        const string armed = "Realtek PCIe GbE Family Controller\r\nIntel(R) Wireless-AC 9462\r\n";
        const string capable = "HID Keyboard Device\r\nIntel(R) Wireless-AC 9462\r\n";

        var adapters = WakeOnLan.Parse(json, armed, capable);
        Assert.Equal(WakeState.Unsupported, WakeOnLan.State(adapters));
        Assert.Equal("Ethernet", Assert.Single(WakeOnLan.Blocked(adapters)).Name);
        Assert.Empty(WakeOnLan.WakeMacs(adapters)); // the phone offers no waking
        Assert.Empty(WakeOnLan.ToFix(adapters));

        // On Wi-Fi it could: that one is ready.
        var onWifi = WakeOnLan.Parse(json.Replace("\"connected\":false", "\"connected\":true", StringComparison.Ordinal), armed, capable);
        Assert.Equal(WakeState.Ready, WakeOnLan.State(onWifi));
        Assert.Equal(["02:AA:BB:CC:DD:02"], WakeOnLan.WakeMacs(onWifi));
    }

    [Theory]
    [InlineData("02-1A-2B-3C-4D-5E", "02:1A:2B:3C:4D:5E")]
    [InlineData("021a2b3c4d5e", "02:1A:2B:3C:4D:5E")]
    [InlineData("02:1A:2B:3C:4D:5E", "02:1A:2B:3C:4D:5E")]
    [InlineData("000000000000", null)]
    [InlineData("02-1A-2B-3C-4D", null)]
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
