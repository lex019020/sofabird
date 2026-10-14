using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Sofabird.Companion.Net;

public static class NetworkUtil
{
    /// <summary>IPv4 addresses of active interfaces (without loopback and APIPA), the "best" one first.</summary>
    public static IReadOnlyList<IPAddress> GetLanAddresses()
    {
        var result = new List<(IPAddress Address, int Score)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var props = nic.GetIPProperties();
                var hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                foreach (var ua in props.UnicastAddresses)
                {
                    var ip = ua.Address;
                    if (ip.AddressFamily != AddressFamily.InterNetwork) continue;
                    var bytes = ip.GetAddressBytes();
                    if (bytes[0] == 169 && bytes[1] == 254) continue;
                    var score = (hasGateway ? 10 : 0)
                                + (nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 ? 5 : 0)
                                - (nic.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || nic.Description.Contains("VMware", StringComparison.OrdinalIgnoreCase) || nic.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) ? 20 : 0);
                    result.Add((ip, score));
                }
            }
        }
        catch
        {
            // best effort
        }
        return result.OrderByDescending(r => r.Score).Select(r => r.Address).ToList();
    }

    public static string? GetPrimaryLanAddress() => GetLanAddresses() is [var first, ..] ? first.ToString() : null;
}
