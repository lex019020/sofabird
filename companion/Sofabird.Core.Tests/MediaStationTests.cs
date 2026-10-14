using Sofabird.Core.Displays;
using Sofabird.Core.Power;
using Xunit;

namespace Sofabird.Core.Tests;

public class TopologyRecoveryTests
{
    private const string Tv = MonitorTopologyTests.TvPath;
    private const string Panel = MonitorTopologyTests.PanelPath;
    private static readonly IReadOnlyList<MonitorInfo> Both = MonitorTopologyTests.LaptopAndTv();
    private static readonly IReadOnlyList<MonitorInfo> PanelOnly = [Both[0]];
    private static readonly IReadOnlyList<MonitorInfo> TvOnly = [Both[1]];

    private static TopologyReaction Decide(string? preferred = Tv, string? vlcOn = Tv, IReadOnlyList<MonitorInfo>? before = null,
        IReadOnlyList<MonitorInfo>? after = null, bool awaiting = false) =>
        TopologyRecovery.Decide(new TopologyInput(preferred, vlcOn, before ?? Both, after ?? TvOnly, awaiting));

    [Fact]
    public void LidClosed_WindowOnTheTv_IsRestored() => Assert.Equal(TopologyReaction.Restore, Decide());

    [Fact]
    public void NoPreference_NotManaged() => Assert.Equal(TopologyReaction.NotManaged, Decide(preferred: null));

    [Fact]
    public void WindowKeptOnThePanelOnPurpose_IsLeftAlone() =>
        Assert.Equal(TopologyReaction.None, Decide(vlcOn: Panel, after: Both, before: [.. Both, .. PanelOnly]));

    [Fact]
    public void WindowsMonitorWentAway_GoesToTheTv() =>
        Assert.Equal(TopologyReaction.Restore, Decide(vlcOn: Panel, after: TvOnly));

    [Fact]
    public void UnknownWindowMonitor_CountsAsOnThePreferredOne() =>
        Assert.Equal(TopologyReaction.Restore, Decide(vlcOn: null));

    [Fact]
    public void TvGone_WhileVlcWasOnIt_Pauses() =>
        Assert.Equal(TopologyReaction.PreferredLost, Decide(after: PanelOnly));

    [Fact]
    public void TvGone_WhileVlcWasOnThePanel_NothingToDo() =>
        Assert.Equal(TopologyReaction.None, Decide(vlcOn: Panel, after: PanelOnly));

    [Fact]
    public void TvWasNotThereBefore_NothingToDo() =>
        Assert.Equal(TopologyReaction.None, Decide(before: PanelOnly, after: PanelOnly));

    [Fact]
    public void TvBack_AfterItWasLost_RestoresEvenThoughWindowsMovedTheWindow() =>
        Assert.Equal(TopologyReaction.Restore, Decide(vlcOn: Panel, before: PanelOnly, after: Both, awaiting: true));

    [Fact]
    public void FullscreenMemory()
    {
        var at = new DateTimeOffset(2026, 10, 13, 16, 47, 4, TimeSpan.Zero);
        var memory = TimeSpan.FromSeconds(5);
        Assert.True(TopologyRecovery.WasFullscreen(at.AddSeconds(-2), at, memory));
        Assert.False(TopologyRecovery.WasFullscreen(at.AddSeconds(-30), at, memory));
        Assert.False(TopologyRecovery.WasFullscreen(null, at, memory));
    }
}

public class MediaStationPowerTests
{
    [Fact]
    public void TypicalLaptopDefaults_EverythingDeviates()
    {
        // Balanced plan out of the box: lid → sleep, sleep after 30/15 min, hibernate after 3 h on battery.
        var defaults = new Dictionary<(string, PowerSource), uint>
        {
            [("lidClose", PowerSource.Ac)] = 1, [("lidClose", PowerSource.Dc)] = 1,
            [("sleepAfter", PowerSource.Ac)] = 1800, [("sleepAfter", PowerSource.Dc)] = 900,
            [("hibernateAfter", PowerSource.Ac)] = 0, [("hibernateAfter", PowerSource.Dc)] = 10800,
        };
        var deviations = MediaStationPower.Check((s, src) => defaults[(s.Key, src)]);
        Assert.Equal(5, deviations.Count);
        Assert.DoesNotContain(deviations, d => d.Setting == MediaStationPower.HibernateAfter && d.Source == PowerSource.Ac);
        Assert.All(deviations, d => Assert.Equal(0u, d.Wanted));
    }

    [Fact]
    public void TheTvLaptopAsMeasured_OnlyBatterySettingsDeviate()
    {
        // 2026-10-13: lid AC "do nothing", DC "sleep"; sleep AC never, DC 600 s; hibernate AC never, DC 10800 s.
        var measured = new Dictionary<(string, PowerSource), uint>
        {
            [("lidClose", PowerSource.Ac)] = 0, [("lidClose", PowerSource.Dc)] = 1,
            [("sleepAfter", PowerSource.Ac)] = 0, [("sleepAfter", PowerSource.Dc)] = 600,
            [("hibernateAfter", PowerSource.Ac)] = 0, [("hibernateAfter", PowerSource.Dc)] = 10800,
        };
        var deviations = MediaStationPower.Check((s, src) => measured[(s.Key, src)]);
        Assert.All(deviations, d => Assert.Equal(PowerSource.Dc, d.Source));
        Assert.Equal("hibernateAfter/Dc=10800;lidClose/Dc=1;sleepAfter/Dc=600", MediaStationPower.Signature(deviations));
    }

    [Fact]
    public void MissingSettingsAreSkipped_AndAConformingPlanHasNoDeviations()
    {
        Assert.Empty(MediaStationPower.Check((_, _) => null));
        Assert.Empty(MediaStationPower.Check((_, _) => 0u));
    }
}
