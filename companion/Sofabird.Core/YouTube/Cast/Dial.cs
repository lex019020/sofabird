using System.Security;
using System.Text;

namespace Sofabird.Core.YouTube.Cast;

/// <summary>
/// DIAL, the way the YouTube app finds a TV on the network: an SSDP <c>M-SEARCH</c> for the DIAL service on
/// 239.255.255.250:1900, the device description at the answer's <c>LOCATION</c> (whose <c>Application-URL</c> header
/// points to the apps), and the "YouTube" app there: its status, and a launch (a <c>POST</c> with the pairing code the
/// TV registers with YouTube's lounge service). The texts the companion sends; the transport is elsewhere.
/// </summary>
public static class Dial
{
    public const string MulticastAddress = "239.255.255.250";
    public const int SsdpPort = 1900;
    public const string ServiceType = "urn:dial-multiscreen-org:service:dial:1";
    public const string DeviceType = "urn:dial-multiscreen-org:device:dial:1";
    public const string AppName = "YouTube";

    /// <summary>
    /// The search target to answer for an SSDP datagram: an <c>M-SEARCH</c> (<c>MAN: "ssdp:discover"</c>) for the DIAL
    /// service or device. <c>null</c> — anything else (other searches, announcements, garbage).
    /// </summary>
    public static string? SearchTarget(string datagram)
    {
        var lines = datagram.Split("\r\n");
        if (lines.Length == 0 || !lines[0].StartsWith("M-SEARCH * HTTP/1.1", StringComparison.OrdinalIgnoreCase)) return null;
        string? st = null, man = null;
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Equals("ST", StringComparison.OrdinalIgnoreCase)) st = value;
            else if (name.Equals("MAN", StringComparison.OrdinalIgnoreCase)) man = value.Trim('"');
        }
        if (!string.Equals(man, "ssdp:discover", StringComparison.OrdinalIgnoreCase)) return null;
        return st is ServiceType or DeviceType ? st : null;
    }

    /// <summary>The unicast answer to a search: where the device description is.</summary>
    public static string SearchResponse(string searchTarget, string location, string uuid) =>
        "HTTP/1.1 200 OK\r\n" +
        "CACHE-CONTROL: max-age=1800\r\n" +
        "EXT:\r\n" +
        $"LOCATION: {location}\r\n" +
        "SERVER: Windows UPnP/1.1 Sofabird/1\r\n" +
        $"ST: {searchTarget}\r\n" +
        $"USN: uuid:{uuid}::{searchTarget}\r\n" +
        "BOOTID.UPNP.ORG: 1\r\n" +
        "\r\n";

    /// <summary>The UPnP device description (served with the <c>Application-URL</c> header).</summary>
    public static string DeviceDescription(string friendlyName, string uuid)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        sb.Append("<root xmlns=\"urn:schemas-upnp-org:device-1-0\">\n");
        sb.Append("  <specVersion><major>1</major><minor>0</minor></specVersion>\n");
        sb.Append("  <device>\n");
        sb.Append("    <deviceType>").Append(DeviceType).Append("</deviceType>\n");
        sb.Append("    <friendlyName>").Append(SecurityElement.Escape(friendlyName)).Append("</friendlyName>\n");
        sb.Append("    <manufacturer>Sofabird</manufacturer>\n");
        sb.Append("    <modelName>Sofabird for Windows</modelName>\n");
        sb.Append("    <UDN>uuid:").Append(SecurityElement.Escape(uuid)).Append("</UDN>\n");
        sb.Append("  </device>\n");
        sb.Append("</root>\n");
        return sb.ToString();
    }

    /// <summary>The status of the YouTube app: running while the companion is connected to the lounge service.</summary>
    public static string AppStatus(bool running) =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
        "<service xmlns=\"urn:dial-multiscreen-org:schemas:dial\" dialVer=\"1.7\">\n" +
        $"  <name>{AppName}</name>\n" +
        "  <options allowStop=\"false\"/>\n" +
        $"  <state>{(running ? "running" : "stopped")}</state>\n" +
        (running ? "  <link rel=\"run\" href=\"run\"/>\n" : "") +
        "</service>\n";

    /// <summary>
    /// The body of a launch (<c>pairingCode=…&amp;theme=cl&amp;dialLaunch=watch</c>); <c>null</c> without a pairing
    /// code, or for another theme than the YouTube app's own (<c>cl</c>; YouTube Music would be <c>m</c>).
    /// </summary>
    public static DialLaunch? ParseLaunch(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        string? code = null, theme = null, mode = null;
        foreach (var pair in body.Trim().Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            var value = Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            switch (pair[..eq])
            {
                case "pairingCode": code = value; break;
                case "theme": theme = value; break;
                case "dialLaunch": mode = value; break;
            }
        }
        if (string.IsNullOrWhiteSpace(code) || code.Length > 128) return null;
        if (theme is not null && theme != "cl") return null;
        return new DialLaunch(code, mode);
    }
}

/// <summary>A launch of the YouTube app from a phone: the code to register, <c>watch</c> or <c>browse</c>.</summary>
public sealed record DialLaunch(string PairingCode, string? Mode);
