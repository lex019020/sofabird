using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Sofabird.Companion.Services;
using Sofabird.Core.Power;
using Sofabird.Core.Setup;

namespace Sofabird.Companion.Setup;

public enum FirewallApplyOutcome
{
    /// <summary>The rules are in place (or nothing had to change).</summary>
    Done,
    /// <summary>The user declined the UAC prompt.</summary>
    Cancelled,
    /// <summary>The helper failed or the rules are still not right afterwards.</summary>
    Failed,
}

/// <summary>
/// Firewall rules and Wake-on-LAN need administrator rights, the companion itself runs without them. So the companion
/// starts a copy of itself elevated (one UAC prompt for both): <c>Sofabird.exe --apply-firewall --companion-exe
/// "&lt;path&gt;" [--vlc-exe "&lt;path&gt;"] [--data-dir "&lt;dir&gt;"] [--skip-firewall] [--wake-adapter
/// "&lt;name&gt;"]…</c>. The helper removes rules that block the programs on private networks, adds allow rules
/// (<see cref="FirewallPlanner"/>), turns waking on for the adapters (<see cref="WakeOnLan.FixScript"/>), writes to
/// the companion log and exits with one of the <c>Exit*</c> codes. It never starts the tray, the listeners or VLC.
/// </summary>
public static class SetupElevation
{
    public const string HelperArg = "--apply-firewall";
    public const int ExitOk = 0;
    public const int ExitBadArgs = 2;
    public const int ExitError = 3;
    public const int ExitStillNotOk = 4;
    public const int ExitWakeFailed = 5;
    private static readonly TimeSpan WakeTimeout = TimeSpan.FromSeconds(60);
    private const int ErrorCancelled = 1223;

    /// <summary>Starts the elevated helper and waits for it. Never throws.</summary>
    public static async Task<(FirewallApplyOutcome Outcome, string? Error)> RunElevatedAsync(
        string companionExe, string? vlcExe, string dataDir, bool firewall, IReadOnlyList<string> wakeAdapters, ILogger log,
        CancellationToken ct)
    {
        var args = $"{HelperArg} --companion-exe {Quote(companionExe)}";
        if (!string.IsNullOrWhiteSpace(vlcExe)) args += $" --vlc-exe {Quote(vlcExe)}";
        args += $" --data-dir {Quote(dataDir)}";
        if (!firewall) args += " --skip-firewall";
        foreach (var adapter in wakeAdapters.Where(a => !a.Contains('"', StringComparison.Ordinal))) args += $" --wake-adapter {Quote(adapter)}";
        var psi = new ProcessStartInfo(companionExe, args)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            log.LogInformation("Starting the elevated setup helper (firewall {Firewall}, wake {Wake})", firewall, string.Join(", ", wakeAdapters));
            using var p = Process.Start(psi);
            if (p is null) return (FirewallApplyOutcome.Failed, "code -1"); // technical detail, shown in parentheses
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            log.LogInformation("Setup helper exited with code {Code}", p.ExitCode);
            return p.ExitCode == ExitOk
                ? (FirewallApplyOutcome.Done, null)
                : (FirewallApplyOutcome.Failed, $"code {p.ExitCode}");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            log.LogInformation("The user declined the UAC prompt for the setup helper");
            return (FirewallApplyOutcome.Cancelled, null);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            log.LogWarning(ex, "Starting the setup helper failed");
            return (FirewallApplyOutcome.Failed, ex.Message);
        }
    }

    // Paths cannot contain quotes; a trailing backslash would escape the closing quote, but these are files.
    private static string Quote(string s) => "\"" + s.TrimEnd('\\') + "\"";

    /// <summary>Entry point of the elevated helper (<see cref="HelperArg"/>).</summary>
    public static int RunHelper(string[] args, ILogger log)
    {
        var companion = Value(args, "--companion-exe");
        var vlc = Value(args, "--vlc-exe");
        if (!IsExe(companion) || (vlc is not null && !IsExe(vlc)))
        {
            log.LogError("Firewall helper: bad arguments (companion {Companion}, vlc {Vlc})", companion, vlc);
            return ExitBadArgs;
        }
        var firewall = !args.Contains("--skip-firewall", StringComparer.OrdinalIgnoreCase);
        var code = firewall ? ApplyFirewall(companion!, vlc, log) : ExitOk;
        var wake = Values(args, "--wake-adapter");
        if (wake.Count > 0 && !ApplyWake(wake, log) && code == ExitOk) code = ExitWakeFailed;
        return code;
    }

    private static int ApplyFirewall(string companion, string? vlc, ILogger log)
    {
        try
        {
            var programs = FirewallPlanner.Programs(companion, vlc);
            var (plan, after) = FirewallPlanner.Fix(new ComFirewallRuleSource(programs), programs);
            foreach (var r in plan.Remove)
                log.LogInformation("Firewall: removed rule '{Name}' ({Action}, {Profiles}, protocol {Protocol}) for {Program}",
                    r.Name, r.Action, r.Profiles, r.Protocol, r.Program);
            foreach (var r in plan.Add)
                log.LogInformation("Firewall: added rule '{Name}' (allow inbound, {Profiles}) for {Program}", r.Name, r.Profiles, r.Program);
            if (plan.IsEmpty) log.LogInformation("Firewall: nothing to change");
            return after.Ok ? ExitOk : ExitStillNotOk;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Firewall helper failed");
            return ExitError;
        }
    }

    /// <summary>"Wake on Magic Packet" and arming for the adapters (PowerShell, elevated); whether it worked.</summary>
    private static bool ApplyWake(IReadOnlyList<string> adapters, ILogger log)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powershell.exe", WakeOnLanService.PowerShellArgs(WakeOnLan.FixScript(adapters)))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            });
            if (p is null) return false;
            var errors = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(WakeTimeout))
            {
                try { p.Kill(); } catch (InvalidOperationException) { }
                log.LogError("Wake-on-LAN: PowerShell did not finish in {Timeout}", WakeTimeout);
                return false;
            }
            if (p.ExitCode != 0)
            {
                log.LogError("Wake-on-LAN: turning it on for {Adapters} failed ({Code}): {Errors}", string.Join(", ", adapters), p.ExitCode, errors.Result.Trim());
                return false;
            }
            log.LogInformation("Wake-on-LAN: turned on for {Adapters}", string.Join(", ", adapters));
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            log.LogError(ex, "Wake-on-LAN: starting PowerShell failed");
            return false;
        }
    }

    private static List<string> Values(string[] args, string name)
    {
        var values = new List<string>();
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                values.Add(args[i + 1]);
        return values;
    }

    private static string? Value(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private static bool IsExe(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)
        && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path);
}
