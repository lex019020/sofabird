using System.Diagnostics;
using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;
using Sofabird.Core.Power;

namespace Sofabird.Companion.Services;

/// <summary>
/// Wake-on-LAN of this PC: the adapters' MAC addresses for the phones (<c>macs</c> in <c>welcome</c>, the beacon and
/// <c>/info</c>; a phone sends the magic packet itself) and the adapters' wake settings for the setup window (read
/// without administrator rights; turned on by the elevated helper, <see cref="WakeOnLan.FixScript"/>).
/// </summary>
public sealed class WakeOnLanService
{
    private static readonly TimeSpan ScriptTimeout = TimeSpan.FromSeconds(20);
    private readonly ILogger _log;
    private IReadOnlyList<string>? _macs;
    private DateTime _macsAt;

    public WakeOnLanService(ILogger<WakeOnLanService> log) => _log = log;

    /// <summary>
    /// The physical Ethernet and Wi-Fi adapters' MACs, wired and connected ones first (cached for a minute: every
    /// beacon asks).
    /// </summary>
    public IReadOnlyList<string> Macs()
    {
        if (_macs is not null && DateTime.UtcNow - _macsAt < TimeSpan.FromMinutes(1)) return _macs;
        var found = new List<(string Mac, int Score)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)) continue;
                if (IsVirtual(nic.Description)) continue;
                if (WakeOnLan.NormalizeMac(nic.GetPhysicalAddress().ToString()) is not { } mac) continue;
                var score = (nic.OperationalStatus == OperationalStatus.Up ? 2 : 0) + (nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 1 : 0);
                found.Add((mac, score));
            }
        }
        catch (NetworkInformationException ex)
        {
            _log.LogDebug(ex, "Listing the network adapters failed");
        }
        _macs = found.OrderByDescending(f => f.Score).Select(f => f.Mac).Distinct().ToList();
        _macsAt = DateTime.UtcNow;
        return _macs;
    }

    /// <summary>The adapters that can wake this PC and how they are set (PowerShell and powercfg, ~1 s): off the UI thread.</summary>
    public IReadOnlyList<WakeAdapter> Inspect()
    {
        var json = Run("powershell.exe", PowerShellArgs(WakeOnLan.InspectScript));
        var armed = Run("powercfg.exe", "/devicequery wake_armed");
        var adapters = WakeOnLan.Parse(json, armed);
        _log.LogDebug("Wake-on-LAN: {Adapters}", string.Join("; ", adapters.Select(a => $"{a.Name} magic packet {a.MagicPacket}, armed {a.Armed}")));
        return adapters;
    }

    /// <summary>A script for <c>powershell.exe</c>, encoded so that no quoting is needed.</summary>
    public static string PowerShellArgs(string script) =>
        "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));

    private string? Run(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(ScriptTimeout))
            {
                try { p.Kill(); } catch (InvalidOperationException) { }
                _log.LogWarning("Wake-on-LAN: {Exe} did not finish in {Timeout}", exe, ScriptTimeout);
                return null;
            }
            return output.Result;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _log.LogWarning(ex, "Wake-on-LAN: running {Exe} failed", exe);
            return null;
        }
    }

    private static bool IsVirtual(string description) =>
        description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
        || description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
        || description.Contains("VMware", StringComparison.OrdinalIgnoreCase)
        || description.Contains("VPN", StringComparison.OrdinalIgnoreCase)
        || description.Contains("TAP-", StringComparison.OrdinalIgnoreCase)
        || description.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase);
}
