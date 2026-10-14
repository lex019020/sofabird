using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
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
/// Applying firewall rules needs administrator rights, the companion itself runs without them. So the companion
/// starts a copy of itself elevated (one UAC prompt): <c>Sofabird.exe --apply-firewall --companion-exe
/// "&lt;path&gt;" [--vlc-exe "&lt;path&gt;"] [--data-dir "&lt;dir&gt;"]</c>. The helper removes rules that block the
/// programs on private networks, adds allow rules (<see cref="FirewallPlanner"/>), writes to the companion log and
/// exits with one of the <c>Exit*</c> codes. It never starts the tray, the listeners or VLC.
/// </summary>
public static class FirewallElevation
{
    public const string HelperArg = "--apply-firewall";
    public const int ExitOk = 0;
    public const int ExitBadArgs = 2;
    public const int ExitError = 3;
    public const int ExitStillNotOk = 4;
    private const int ErrorCancelled = 1223;

    /// <summary>Starts the elevated helper and waits for it. Never throws.</summary>
    public static async Task<(FirewallApplyOutcome Outcome, string? Error)> RunElevatedAsync(
        string companionExe, string? vlcExe, string dataDir, ILogger log, CancellationToken ct)
    {
        var args = $"{HelperArg} --companion-exe {Quote(companionExe)}";
        if (!string.IsNullOrWhiteSpace(vlcExe)) args += $" --vlc-exe {Quote(vlcExe)}";
        args += $" --data-dir {Quote(dataDir)}";
        var psi = new ProcessStartInfo(companionExe, args)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            log.LogInformation("Starting the elevated firewall helper");
            using var p = Process.Start(psi);
            if (p is null) return (FirewallApplyOutcome.Failed, "code -1"); // technical detail, shown in parentheses
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            log.LogInformation("Firewall helper exited with code {Code}", p.ExitCode);
            return p.ExitCode == ExitOk
                ? (FirewallApplyOutcome.Done, null)
                : (FirewallApplyOutcome.Failed, $"code {p.ExitCode}");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            log.LogInformation("The user declined the UAC prompt for the firewall helper");
            return (FirewallApplyOutcome.Cancelled, null);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            log.LogWarning(ex, "Starting the firewall helper failed");
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
        try
        {
            var programs = FirewallPlanner.Programs(companion!, vlc);
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
