using System.Diagnostics;
using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;
using Sofabird.Core.Power;

namespace Sofabird.Companion.Services;

/// <summary>
/// Wake-on-LAN of this PC: which adapters will wake it (read without administrator rights — PowerShell and powercfg,
/// ~1 s — at the start and when the network changes; turned on by the elevated helper, <see cref="WakeOnLan.FixScript"/>),
/// their MAC addresses for the phones (<c>macs</c> in <c>welcome</c>, the beacon and <c>/info</c>; a phone sends the
/// magic packet itself) and the setup window's item.
/// </summary>
public sealed class WakeOnLanService : IDisposable
{
    private static readonly TimeSpan ScriptTimeout = TimeSpan.FromSeconds(20);
    /// <summary>A cable plugged in or Wi-Fi joining changes the addresses several times: look once it settles.</summary>
    private static readonly TimeSpan NetworkSettle = TimeSpan.FromSeconds(5);
    private readonly ILogger _log;
    private readonly System.Threading.Timer _settle;
    private IReadOnlyList<WakeAdapter>? _adapters;
    private int _inspecting;

    public WakeOnLanService(ILogger<WakeOnLanService> log)
    {
        _log = log;
        _settle = new System.Threading.Timer(_ => Refresh());
    }

    /// <summary>Looks at the adapters now (in the background) and again whenever the network changes.</summary>
    public void Start()
    {
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        Refresh();
    }

    /// <summary>
    /// <c>macs</c> for the phones: the connected adapters that will wake this PC (<see cref="WakeOnLan.WakeMacs"/>);
    /// empty — it cannot be woken (the phone then offers no waking); <c>null</c> — not looked at yet.
    /// </summary>
    public IReadOnlyList<string>? Macs() => Volatile.Read(ref _adapters) is { } adapters ? WakeOnLan.WakeMacs(adapters) : null;

    /// <summary>The adapters that can wake this PC and how they are set (~1 s): off the UI thread. Also updates <see cref="Macs"/>.</summary>
    public IReadOnlyList<WakeAdapter> Inspect()
    {
        var json = Run("powershell.exe", PowerShellArgs(WakeOnLan.InspectScript));
        var armed = Run("powercfg.exe", "/devicequery wake_armed");
        var capable = Run("powercfg.exe", "/devicequery wake_from_any");
        var adapters = WakeOnLan.Parse(json, armed, capable);
        var previous = Interlocked.Exchange(ref _adapters, adapters);
        var summary = string.Join("; ", adapters.Select(a =>
            $"{a.Name} ({a.Description}) connected {a.Connected}, can wake {a.CanWake}, magic packet {a.MagicPacket}, armed {a.Armed}"));
        if (previous is null || !previous.SequenceEqual(adapters))
            _log.LogInformation("Wake-on-LAN: {State}; {Adapters}", WakeOnLan.State(adapters), summary);
        return adapters;
    }

    private void Refresh()
    {
        if (Interlocked.Exchange(ref _inspecting, 1) == 1) return;
        _ = Task.Run(() =>
        {
            try
            {
                Inspect();
            }
            finally
            {
                Volatile.Write(ref _inspecting, 0);
            }
        });
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => _settle.Change(NetworkSettle, Timeout.InfiniteTimeSpan);

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        _settle.Dispose();
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
}
