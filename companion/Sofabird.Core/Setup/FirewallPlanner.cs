namespace Sofabird.Core.Setup;

/// <summary>
/// Pure decisions about Windows Firewall rules for the companion and VLC.
/// <list type="bullet">
/// <item>A program is <b>allowed</b> when an enabled inbound allow rule for it covers the private profile and TCP
/// (and UDP for the companion's discovery beacon).</item>
/// <item>A program is <b>blocked</b> when an enabled inbound block rule for it covers the private profile (block wins
/// over allow). Clicking "Cancel" in the Windows "allow access" prompt creates such rules silently. Block rules for
/// the public profile only are left alone: they match the intended policy (private networks only).</item>
/// <item>The fix removes the blocking rules and, for programs that are not allowed, replaces our own named rules
/// for that program with a fresh allow rule (private profile, any protocol).</item>
/// </list>
/// </summary>
public static class FirewallPlanner
{
    public const string CompanionKey = "companion";
    public const string VlcKey = "vlc";
    /// <summary>Same names as <c>scripts/deploy-windows.ps1 -Firewall</c>, so both manage the same rules.</summary>
    public const string CompanionRuleName = "Sofabird";
    public const string VlcRuleName = "VLC (Sofabird)";
    public const string Grouping = "Sofabird";

    /// <summary>
    /// Rules from the companion's earlier name ("VLC Remote Companion"): ours by name, and the ones Windows created
    /// from its own prompt for the old program (named after the file). They do not count as access for Sofabird, so the
    /// setup window replaces them: every plan removes them along with adding the rules under the new names.
    /// </summary>
    public static readonly IReadOnlyList<string> LegacyRuleNames = ["VLC Remote Companion", "VLC (VLC Remote)"];
    public const string LegacyProgramFile = "VlcRemote.Companion.exe";

    /// <summary>The programs that must accept connections from the phone; VLC only when it is installed.</summary>
    public static IReadOnlyList<FirewallProgram> Programs(string companionExe, string? vlcExe)
    {
        var list = new List<FirewallProgram> { new(CompanionKey, companionExe, CompanionRuleName, NeedsUdp: true) };
        if (!string.IsNullOrWhiteSpace(vlcExe)) list.Add(new FirewallProgram(VlcKey, vlcExe, VlcRuleName, NeedsUdp: false));
        return list;
    }

    /// <summary>Compares program paths case-insensitively, ignoring <c>.</c>/<c>..</c> segments and separators.</summary>
    public static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path)
    {
        var p = path.Trim().Trim('"').Replace('/', '\\');
        try { p = System.IO.Path.GetFullPath(p); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        return p.TrimEnd('\\');
    }

    private static bool CoversPrivate(FwProfiles profiles) => (profiles & FwProfiles.Private) != 0;

    private static bool Covers(int ruleProtocol, int protocol) => ruleProtocol == FwProtocol.Any || ruleProtocol == protocol;

    private static bool IsLegacy(FirewallRule r) =>
        LegacyRuleNames.Contains(r.Name, StringComparer.OrdinalIgnoreCase)
        || string.Equals(System.IO.Path.GetFileName(r.Program?.Trim().Trim('"')), LegacyProgramFile, StringComparison.OrdinalIgnoreCase);

    private static bool IsInboundFor(FirewallRule r, FirewallProgram p) =>
        !IsLegacy(r) && r.Enabled && r.Direction == FwDirection.In && CoversPrivate(r.Profiles) && SamePath(r.Program, p.Path);

    public static FirewallProgramStatus Analyze(IEnumerable<FirewallRule> rules, FirewallProgram program)
    {
        var mine = rules.Where(r => IsInboundFor(r, program)).ToList();
        var allows = mine.Where(r => r.Action == FwAction.Allow).ToList();
        var allowed = allows.Any(r => Covers(r.Protocol, FwProtocol.Tcp))
            && (!program.NeedsUdp || allows.Any(r => Covers(r.Protocol, FwProtocol.Udp)));
        var blocking = mine.Where(r => r.Action == FwAction.Block
            && (Covers(r.Protocol, FwProtocol.Tcp) || Covers(r.Protocol, FwProtocol.Udp))).ToList();
        return new FirewallProgramStatus(program, allowed, blocking);
    }

    public static FirewallStatus Analyze(bool privateProfileEnabled, IReadOnlyList<FirewallRule> rules, IEnumerable<FirewallProgram> programs) =>
        new(privateProfileEnabled, programs.Select(p => Analyze(rules, p)).ToList());

    public static FirewallPlan Plan(IReadOnlyList<FirewallRule> rules, IEnumerable<FirewallProgram> programs)
    {
        var remove = new List<FirewallRule>();
        var add = new List<FirewallRuleSpec>();
        foreach (var program in programs)
        {
            var status = Analyze(rules, program);
            remove.AddRange(status.BlockingRules);
            if (status.Allowed) continue;
            // Replace our own rules for this program (disabled, wrong profile or protocol) with a fresh one. Rules with
            // the same name for another path are kept: they may belong to another copy of the companion.
            remove.AddRange(rules.Where(r => string.Equals(r.Name, program.RuleName, StringComparison.OrdinalIgnoreCase)
                && SamePath(r.Program, program.Path)));
            add.Add(new FirewallRuleSpec(
                program.RuleName,
                $"Allows the phone to connect to {System.IO.Path.GetFileName(program.Path)} on private (home) networks (Sofabird).",
                program.Path,
                FwProfiles.Private,
                FwProtocol.Any,
                Grouping: Grouping));
        }
        remove.AddRange(rules.Where(IsLegacy));
        var unique = remove.GroupBy(r => r.Index).Select(g => g.First()).OrderBy(r => r.Index).ToList();
        return new FirewallPlan(unique, add);
    }

    public static FirewallStatus Inspect(IFirewallRuleSource source, IEnumerable<FirewallProgram> programs) =>
        Analyze(source.IsEnabled(FwProfiles.Private), source.ReadRules(), programs);

    /// <summary>
    /// Reads the rules, applies the plan and reads them again. Used by the elevated helper
    /// (<c>Sofabird.exe --apply-firewall</c>). Returns the plan that was applied and the final state.
    /// </summary>
    public static (FirewallPlan Plan, FirewallStatus After) Fix(IFirewallRuleSource source, IReadOnlyList<FirewallProgram> programs)
    {
        var plan = Plan(source.ReadRules(), programs);
        foreach (var r in plan.Remove) source.Remove(r);
        foreach (var r in plan.Add) source.Add(r);
        return (plan, Inspect(source, programs));
    }
}
