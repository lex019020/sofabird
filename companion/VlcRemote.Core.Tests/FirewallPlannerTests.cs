using VlcRemote.Core.Setup;
using Xunit;

namespace VlcRemote.Core.Tests;

public class FirewallPlannerTests
{
    private const string Companion = @"C:\Users\u\AppData\Local\VlcRemote\app\VlcRemote.Companion.exe";
    private const string Vlc = @"C:\Program Files\VideoLAN\VLC\vlc.exe";
    private static readonly IReadOnlyList<FirewallProgram> Programs = FirewallPlanner.Programs(Companion, Vlc);

    private static FirewallRule Rule(int index, string name, string? program, FwAction action, FwProfiles profiles,
        int protocol = FwProtocol.Any, bool enabled = true, FwDirection dir = FwDirection.In) =>
        new(index, name, program, dir, action, enabled, profiles, protocol);

    /// <summary>What Windows creates when the user clicks "Cancel" in the prompt on a private network.</summary>
    private static IEnumerable<FirewallRule> CancelledPrompt(int start, string program, string name) =>
    [
        Rule(start, name, program, FwAction.Block, FwProfiles.Private, FwProtocol.Tcp),
        Rule(start + 1, name, program, FwAction.Block, FwProfiles.Private, FwProtocol.Udp),
    ];

    /// <summary>What Windows creates for "Allow" with only "Private networks" ticked.</summary>
    private static IEnumerable<FirewallRule> AllowedPrivateOnly(int start, string program, string name) =>
    [
        Rule(start, name, program, FwAction.Allow, FwProfiles.Private, FwProtocol.Tcp),
        Rule(start + 1, name, program, FwAction.Allow, FwProfiles.Private, FwProtocol.Udp),
        Rule(start + 2, name, program, FwAction.Block, FwProfiles.Public, FwProtocol.Tcp),
        Rule(start + 3, name, program, FwAction.Block, FwProfiles.Public, FwProtocol.Udp),
    ];

    [Fact]
    public void Programs_include_vlc_only_when_installed()
    {
        Assert.Equal(2, FirewallPlanner.Programs(Companion, Vlc).Count);
        var only = Assert.Single(FirewallPlanner.Programs(Companion, null));
        Assert.Equal(FirewallPlanner.CompanionKey, only.Key);
        Assert.True(only.NeedsUdp);
    }

    [Fact]
    public void No_rules_means_missing_and_plan_adds_one_rule_per_program()
    {
        var status = FirewallPlanner.Analyze(true, [], Programs);
        Assert.False(status.Ok);
        Assert.True(status.AnyMissing);
        Assert.False(status.AnyBlocked);

        var plan = FirewallPlanner.Plan([], Programs);
        Assert.Empty(plan.Remove);
        Assert.Collection(plan.Add,
            a =>
            {
                Assert.Equal(FirewallPlanner.CompanionRuleName, a.Name);
                Assert.Equal(Companion, a.Program);
                Assert.Equal(FwProfiles.Private, a.Profiles);
                Assert.Equal(FwProtocol.Any, a.Protocol);
                Assert.Equal(FwDirection.In, a.Direction);
                Assert.Equal(FwAction.Allow, a.Action);
                Assert.Equal(FirewallPlanner.Grouping, a.Grouping);
            },
            a => Assert.Equal(FirewallPlanner.VlcRuleName, a.Name));
    }

    [Fact]
    public void Rules_from_deploy_script_are_enough()
    {
        FirewallRule[] rules =
        [
            Rule(0, FirewallPlanner.CompanionRuleName, Companion, FwAction.Allow, FwProfiles.Private),
            Rule(1, FirewallPlanner.VlcRuleName, Vlc, FwAction.Allow, FwProfiles.Private),
        ];
        Assert.True(FirewallPlanner.Analyze(true, rules, Programs).Ok);
        Assert.True(FirewallPlanner.Plan(rules, Programs).IsEmpty);
    }

    [Fact]
    public void Prompt_allow_for_private_only_is_ok_and_public_blocks_are_kept()
    {
        var rules = AllowedPrivateOnly(0, Companion, "vlcremote.companion.exe")
            .Concat(AllowedPrivateOnly(10, Vlc, "VLC media player")).ToList();
        var status = FirewallPlanner.Analyze(true, rules, Programs);
        Assert.True(status.Ok);
        Assert.True(FirewallPlanner.Plan(rules, Programs).IsEmpty);
    }

    [Fact]
    public void Cancelled_prompt_is_blocked_and_plan_removes_blocks_and_adds_allow()
    {
        var rules = CancelledPrompt(0, Companion, "vlcremote.companion.exe")
            .Append(Rule(5, FirewallPlanner.VlcRuleName, Vlc, FwAction.Allow, FwProfiles.Private)).ToList();
        var status = FirewallPlanner.Analyze(true, rules, Programs);
        Assert.False(status.Ok);
        Assert.True(status.AnyBlocked);
        var companion = status.Programs.Single(p => p.Program.Key == FirewallPlanner.CompanionKey);
        Assert.Equal(2, companion.BlockingRules.Count);
        Assert.True(status.Programs.Single(p => p.Program.Key == FirewallPlanner.VlcKey).Ok);

        var plan = FirewallPlanner.Plan(rules, Programs);
        Assert.Equal([0, 1], plan.Remove.Select(r => r.Index));
        var add = Assert.Single(plan.Add);
        Assert.Equal(Companion, add.Program);
    }

    [Fact]
    public void Block_wins_over_allow()
    {
        FirewallRule[] rules =
        [
            Rule(0, FirewallPlanner.CompanionRuleName, Companion, FwAction.Allow, FwProfiles.Private),
            Rule(1, "vlcremote.companion.exe", Companion, FwAction.Block, FwProfiles.All, FwProtocol.Tcp),
        ];
        var status = FirewallPlanner.Analyze(true, rules, [Programs[0]]);
        Assert.True(status.Programs[0].Allowed);
        Assert.True(status.Programs[0].Blocked);
        Assert.False(status.Ok);

        var plan = FirewallPlanner.Plan(rules, [Programs[0]]);
        Assert.Equal(1, Assert.Single(plan.Remove).Index);
        Assert.Empty(plan.Add); // the allow rule stays
    }

    [Fact]
    public void Disabled_outbound_and_public_rules_do_not_count()
    {
        FirewallRule[] rules =
        [
            Rule(0, FirewallPlanner.CompanionRuleName, Companion, FwAction.Allow, FwProfiles.Private, enabled: false),
            Rule(1, "x", Companion, FwAction.Allow, FwProfiles.Private, dir: FwDirection.Out),
            Rule(2, "y", Companion, FwAction.Allow, FwProfiles.Public),
            Rule(3, "z", Companion, FwAction.Block, FwProfiles.Private, enabled: false),
        ];
        var s = FirewallPlanner.Analyze(rules, Programs[0]);
        Assert.False(s.Allowed);
        Assert.False(s.Blocked);

        var plan = FirewallPlanner.Plan(rules, [Programs[0]]);
        // Our own disabled rule for this program is replaced.
        Assert.Equal(0, Assert.Single(plan.Remove).Index);
        Assert.Single(plan.Add);
    }

    [Fact]
    public void Companion_needs_udp_too_vlc_does_not()
    {
        FirewallRule[] rules =
        [
            Rule(0, "c", Companion, FwAction.Allow, FwProfiles.Private, FwProtocol.Tcp),
            Rule(1, "v", Vlc, FwAction.Allow, FwProfiles.Private, FwProtocol.Tcp),
        ];
        var status = FirewallPlanner.Analyze(true, rules, Programs);
        Assert.False(status.Programs[0].Allowed);
        Assert.True(status.Programs[1].Allowed);
    }

    [Fact]
    public void Paths_match_case_insensitively_and_own_rules_of_other_copies_are_kept()
    {
        FirewallRule[] rules =
        [
            Rule(0, FirewallPlanner.CompanionRuleName, @"D:\dev\bin\VlcRemote.Companion.exe", FwAction.Allow, FwProfiles.Private),
            Rule(1, "vlc", @"c:\program files\videolan\vlc\VLC.EXE", FwAction.Allow, FwProfiles.Private | FwProfiles.Public),
        ];
        var status = FirewallPlanner.Analyze(true, rules, Programs);
        Assert.False(status.Programs[0].Allowed);
        Assert.True(status.Programs[1].Allowed);

        var plan = FirewallPlanner.Plan(rules, Programs);
        Assert.Empty(plan.Remove);
        Assert.Equal(Companion, Assert.Single(plan.Add).Program);
    }

    [Fact]
    public void Firewall_off_for_private_is_ok()
    {
        var status = FirewallPlanner.Analyze(false, [], Programs);
        Assert.True(status.Ok);
        Assert.False(status.AnyMissing);
        Assert.False(status.AnyBlocked);
    }

    [Fact]
    public void Fix_applies_plan_to_the_rule_source()
    {
        var source = new InMemoryFirewall(CancelledPrompt(0, Companion, "vlcremote.companion.exe")
            .Concat(CancelledPrompt(0, Vlc, "VLC media player")));
        var (plan, after) = FirewallPlanner.Fix(source, Programs);
        Assert.Equal(4, plan.Remove.Count);
        Assert.Equal(2, plan.Add.Count);
        Assert.True(after.Ok);
        Assert.Equal(2, source.Rules.Count);
        Assert.All(source.Rules, r => Assert.Equal(FwAction.Allow, r.Action));

        // Idempotent: a second run changes nothing.
        var (again, _) = FirewallPlanner.Fix(source, Programs);
        Assert.True(again.IsEmpty);
    }

    private sealed class InMemoryFirewall : IFirewallRuleSource
    {
        public List<FirewallRule> Rules { get; }

        public InMemoryFirewall(IEnumerable<FirewallRule> rules) => Rules = rules.ToList();

        public bool IsEnabled(FwProfiles profile) => true;

        public IReadOnlyList<FirewallRule> ReadRules() => Rules.Select((r, i) => r with { Index = i }).ToList();

        public void Remove(FirewallRule rule) => Rules.RemoveAll(r => r with { Index = 0 } == rule with { Index = 0 });

        public void Add(FirewallRuleSpec rule) =>
            Rules.Add(new FirewallRule(0, rule.Name, rule.Program, rule.Direction, rule.Action, true, rule.Profiles, rule.Protocol));
    }
}
