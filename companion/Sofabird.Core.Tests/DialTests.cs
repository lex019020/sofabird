using Sofabird.Core.YouTube.Cast;
using Xunit;

namespace Sofabird.Core.Tests;

public class DialTests
{
    /// <summary>The search the YouTube app sends (as captured from stock YouTube 21.40).</summary>
    private const string YouTubeSearch = "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 1\r\n" +
                                         "ST: urn:dial-multiscreen-org:service:dial:1\r\nUSER-AGENT: YouTube/21.40.163 (Android)\r\n\r\n";

    [Fact]
    public void Answers_the_youtube_apps_search_for_dial() =>
        Assert.Equal(Dial.ServiceType, Dial.SearchTarget(YouTubeSearch));

    [Theory]
    [InlineData("M-SEARCH * HTTP/1.1\r\nMAN: \"ssdp:discover\"\r\nST: urn:dial-multiscreen-org:device:dial:1\r\n\r\n", Dial.DeviceType)]
    [InlineData("m-search * HTTP/1.1\r\nman: ssdp:discover\r\nst: urn:dial-multiscreen-org:service:dial:1\r\n\r\n", Dial.ServiceType)]
    public void Accepts_the_device_type_and_any_case(string datagram, string expected) =>
        Assert.Equal(expected, Dial.SearchTarget(datagram));

    [Theory]
    [InlineData("M-SEARCH * HTTP/1.1\r\nMAN: \"ssdp:discover\"\r\nST: ssdp:all\r\n\r\n")] // Windows and media players: not for us
    [InlineData("M-SEARCH * HTTP/1.1\r\nMAN: \"ssdp:discover\"\r\nST: urn:schemas-upnp-org:device:MediaRenderer:1\r\n\r\n")]
    [InlineData("M-SEARCH * HTTP/1.1\r\nST: urn:dial-multiscreen-org:service:dial:1\r\n\r\n")] // no MAN
    [InlineData("NOTIFY * HTTP/1.1\r\nNT: urn:dial-multiscreen-org:service:dial:1\r\nNTS: ssdp:alive\r\n\r\n")]
    [InlineData("SOFABIRD_DISCOVER 1")]
    [InlineData("")]
    public void Ignores_everything_else(string datagram) => Assert.Null(Dial.SearchTarget(datagram));

    [Fact]
    public void The_answer_says_where_the_description_is()
    {
        var text = Dial.SearchResponse(Dial.ServiceType, "http://192.168.1.20:47555/dial/dd.xml", "f0e1d2c3-0000-4000-8000-000000000001");
        Assert.StartsWith("HTTP/1.1 200 OK\r\n", text);
        Assert.Contains("\r\nLOCATION: http://192.168.1.20:47555/dial/dd.xml\r\n", text);
        Assert.Contains("\r\nST: urn:dial-multiscreen-org:service:dial:1\r\n", text);
        Assert.Contains("\r\nUSN: uuid:f0e1d2c3-0000-4000-8000-000000000001::urn:dial-multiscreen-org:service:dial:1\r\n", text);
        Assert.EndsWith("\r\n\r\n", text);
    }

    [Fact]
    public void The_description_names_the_pc_safely()
    {
        var xml = Dial.DeviceDescription("Living room <PC> & co", "abc");
        Assert.Contains("<friendlyName>Living room &lt;PC&gt; &amp; co</friendlyName>", xml);
        Assert.Contains("<deviceType>urn:dial-multiscreen-org:device:dial:1</deviceType>", xml);
        Assert.Contains("<UDN>uuid:abc</UDN>", xml);
        System.Xml.Linq.XDocument.Parse(xml); // well-formed
    }

    [Fact]
    public void The_app_runs_while_connected_to_youtube()
    {
        var running = Dial.AppStatus(running: true);
        Assert.Contains("<state>running</state>", running);
        Assert.Contains("<link rel=\"run\" href=\"run\"/>", running);
        Assert.Contains("allowStop=\"false\"", running);
        var stopped = Dial.AppStatus(running: false);
        Assert.Contains("<state>stopped</state>", stopped);
        Assert.DoesNotContain("<link", stopped);
        System.Xml.Linq.XDocument.Parse(running);
        System.Xml.Linq.XDocument.Parse(stopped);
    }

    [Fact]
    public void A_launch_carries_the_pairing_code()
    {
        var launch = Dial.ParseLaunch("pairingCode=7c1e2f3a-aaaa-bbbb-cccc-1234567890ab&theme=cl&dialLaunch=watch&v=aqz-KE-bpKQ");
        Assert.Equal("7c1e2f3a-aaaa-bbbb-cccc-1234567890ab", launch?.PairingCode);
        Assert.Equal("watch", launch?.Mode);
        Assert.Equal("a b", Dial.ParseLaunch("pairingCode=a%20b")?.PairingCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("theme=cl&dialLaunch=browse")] // no code
    [InlineData("pairingCode=abc&theme=m")] // YouTube Music: not played here
    public void Refuses_a_launch_without_a_code_for_us(string? body) => Assert.Null(Dial.ParseLaunch(body));
}
