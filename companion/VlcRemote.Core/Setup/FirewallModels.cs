namespace VlcRemote.Core.Setup;

/// <summary>Windows Firewall profiles (<c>NET_FW_PROFILE_TYPE2</c>).</summary>
[Flags]
public enum FwProfiles
{
    None = 0,
    Domain = 1,
    Private = 2,
    Public = 4,
    All = 0x7FFFFFFF,
}

/// <summary><c>NET_FW_ACTION</c>.</summary>
public enum FwAction
{
    Block = 0,
    Allow = 1,
}

/// <summary><c>NET_FW_RULE_DIRECTION</c>.</summary>
public enum FwDirection
{
    In = 1,
    Out = 2,
}

/// <summary>IP protocol numbers used by firewall rules.</summary>
public static class FwProtocol
{
    public const int Tcp = 6;
    public const int Udp = 17;
    public const int Any = 256;
}

/// <summary>
/// A firewall rule as read from the system. <see cref="Index"/> identifies the rule within one read
/// (the rule source maps it back to the native rule for removal).
/// </summary>
public sealed record FirewallRule(
    int Index,
    string Name,
    string? Program,
    FwDirection Direction,
    FwAction Action,
    bool Enabled,
    FwProfiles Profiles,
    int Protocol);

/// <summary>A rule to create.</summary>
public sealed record FirewallRuleSpec(
    string Name,
    string Description,
    string Program,
    FwProfiles Profiles,
    int Protocol = FwProtocol.Any,
    FwDirection Direction = FwDirection.In,
    FwAction Action = FwAction.Allow,
    string? Grouping = null);

/// <summary>
/// A program that must accept inbound connections from the phone.
/// <see cref="RuleName"/> is the name of the rule the companion (and <c>deploy-windows.ps1 -Firewall</c>) creates.
/// </summary>
public sealed record FirewallProgram(string Key, string Path, string RuleName, bool NeedsUdp);

/// <summary>State of one program: allowed on private networks, and the rules that block it there.</summary>
public sealed record FirewallProgramStatus(
    FirewallProgram Program,
    bool Allowed,
    IReadOnlyList<FirewallRule> BlockingRules)
{
    public bool Blocked => BlockingRules.Count > 0;
    public bool Ok => Allowed && !Blocked;
}

/// <summary>Firewall state for all programs the companion needs.</summary>
public sealed record FirewallStatus(bool PrivateProfileEnabled, IReadOnlyList<FirewallProgramStatus> Programs)
{
    /// <summary>Nothing to do: the firewall is off for private networks, or every program is allowed and not blocked.</summary>
    public bool Ok => !PrivateProfileEnabled || Programs.All(p => p.Ok);
    public bool AnyBlocked => PrivateProfileEnabled && Programs.Any(p => p.Blocked);
    public bool AnyMissing => PrivateProfileEnabled && Programs.Any(p => !p.Allowed);
}

/// <summary>What to change: rules to remove (by <see cref="FirewallRule.Index"/>) and rules to add.</summary>
public sealed record FirewallPlan(IReadOnlyList<FirewallRule> Remove, IReadOnlyList<FirewallRuleSpec> Add)
{
    public bool IsEmpty => Remove.Count == 0 && Add.Count == 0;
}

/// <summary>Access to the system firewall (COM <c>HNetCfg.FwPolicy2</c> in the companion, in-memory in tests).</summary>
public interface IFirewallRuleSource
{
    /// <summary>Whether the firewall is on for the given profile.</summary>
    bool IsEnabled(FwProfiles profile);

    /// <summary>All rules; <see cref="FirewallRule.Index"/> is valid until the next call.</summary>
    IReadOnlyList<FirewallRule> ReadRules();

    /// <summary>Removes a rule from the last <see cref="ReadRules"/> (requires administrator rights).</summary>
    void Remove(FirewallRule rule);

    /// <summary>Adds a rule (requires administrator rights).</summary>
    void Add(FirewallRuleSpec rule);
}
