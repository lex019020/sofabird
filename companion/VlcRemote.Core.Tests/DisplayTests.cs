using System.Globalization;
using System.Text.Json;
using VlcRemote.Core.Displays;
using VlcRemote.Core.Protocol;
using VlcRemote.Core.Session;
using Xunit;

namespace VlcRemote.Core.Tests;

public class MonitorTopologyTests
{
    internal const string PanelPath = @"\\?\DISPLAY#NCP0042#4&1a2b3c4d&0&UID8388688#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    internal const string TvPath = @"\\?\DISPLAY#SAM0F99#5&2f1e7a0b&0&UID4352#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

    internal static GdiScreen Screen(int n, int x, int w = 1920, int h = 1080, bool primary = false) =>
        new($@"\\.\DISPLAY{n}", new ScreenRect(x, 0, w, h), new ScreenRect(x, 0, w, h - 40), primary);

    /// <summary>The TV laptop: internal panel (primary) + Samsung TV over HDMI.</summary>
    internal static IReadOnlyList<MonitorInfo> LaptopAndTv(int tvGdi = 2) => MonitorTopology.Build(
        [Screen(1, 0, primary: true), Screen(tvGdi, 1920, 3840, 2160)],
        [
            new DisplayTarget(@"\\.\DISPLAY1", PanelPath, "LM156LF9L01", MonitorTopology.OutputInternal),
            new DisplayTarget($@"\\.\DISPLAY{tvGdi}", TvPath, "SAMSUNG", MonitorTopology.OutputHdmi),
        ]);

    [Fact]
    public void InternalPanelAndHdmiTv()
    {
        var list = LaptopAndTv();
        Assert.Equal(2, list.Count);

        var panel = list[0];
        Assert.Equal(0, panel.Index);
        Assert.Equal(PanelPath, panel.Id);
        Assert.Equal("LM156LF9L01", panel.Name);
        Assert.Null(panel.NameText);
        Assert.Equal(MonitorKind.Internal, panel.Kind);
        Assert.Equal("internal", panel.KindName);
        Assert.True(panel.Primary);
        Assert.Equal(@"\\.\DISPLAY1", panel.GdiDeviceName);

        var tv = list[1];
        Assert.Equal(1, tv.Index);
        Assert.Equal(TvPath, tv.Id);
        Assert.Equal("SAMSUNG", tv.Name);
        Assert.Equal(MonitorKind.External, tv.Kind);
        Assert.False(tv.Primary);
        Assert.Equal(new ScreenRect(1920, 0, 3840, 2160), tv.Bounds);
    }

    [Fact]
    public void TvWithEmptyFriendlyName_GetsLocalizedFallback()
    {
        var list = MonitorTopology.Build(
            [Screen(1, 0, primary: true), Screen(3, 1920)],
            [
                new DisplayTarget(@"\\.\DISPLAY1", PanelPath, "", MonitorTopology.OutputDisplayPortEmbedded),
                new DisplayTarget(@"\\.\DISPLAY3", TvPath, "  ", MonitorTopology.OutputHdmi),
            ]);
        var panel = list[0];
        Assert.Equal("Built-in display", panel.Name);
        Assert.Equal("Встроенный экран", panel.DisplayName(CultureInfo.GetCultureInfo("ru-RU")));

        var tv = list[1];
        Assert.Equal(TvPath, tv.Id);
        Assert.Equal("Display 3", tv.Name); // the GDI display number
        Assert.Equal("Экран 3", tv.DisplayName(CultureInfo.GetCultureInfo("ru")));
        Assert.Equal("Display 3", tv.DisplayName(null));
    }

    [Fact]
    public void CloneMode_OneEntryPerSource_TvRepresentsIt()
    {
        var list = MonitorTopology.Build(
            [Screen(1, 0, primary: true)],
            [
                new DisplayTarget(@"\\.\DISPLAY1", PanelPath, "LM156LF9L01", MonitorTopology.OutputInternal),
                new DisplayTarget(@"\\.\DISPLAY1", TvPath, "SAMSUNG", MonitorTopology.OutputHdmi),
            ]);
        var m = Assert.Single(list);
        Assert.Equal(TvPath, m.Id);
        Assert.Equal("SAMSUNG + LM156LF9L01", m.Name);
        Assert.Equal(MonitorKind.External, m.Kind);
    }

    [Fact]
    public void ReconnectedTvWithAnotherGdiIndex_KeepsItsId()
    {
        var before = LaptopAndTv(tvGdi: 2);
        var after = LaptopAndTv(tvGdi: 5);
        Assert.Equal(@"\\.\DISPLAY5", after[1].GdiDeviceName);
        Assert.Equal(before[1].Id, after[1].Id);
        Assert.Equal(before[0].Id, after[0].Id);
        Assert.Same(after[1], MonitorTopology.FindById(after, TvPath.ToUpperInvariant()));
    }

    [Fact]
    public void NoDisplayConfigData_FallsBackToGdiNames()
    {
        var list = MonitorTopology.Build([Screen(2, 1920), Screen(1, 0, primary: true)], []);
        Assert.Equal([@"\\.\DISPLAY1", @"\\.\DISPLAY2"], list.Select(m => m.Id));
        Assert.Equal([0, 1], list.Select(m => m.Index));
        Assert.All(list, m => Assert.Equal(MonitorKind.Unknown, m.Kind));
        Assert.Equal("Display 2", list[1].Name);
    }

    [Fact]
    public void MissingDevicePath_UsesGdiName()
    {
        var list = MonitorTopology.Build([Screen(1, 0, primary: true)],
            [new DisplayTarget(@"\\.\DISPLAY1", "", "DELL U2720Q", MonitorTopology.OutputDisplayPortExternal)]);
        Assert.Equal(@"\\.\DISPLAY1", list[0].Id);
        Assert.Equal("DELL U2720Q", list[0].Name);
        Assert.Equal(MonitorKind.External, list[0].Kind);
    }

    [Fact]
    public void IdenticalMonitors_AreToldApartByNumber()
    {
        var list = MonitorTopology.Build([Screen(1, 0, primary: true), Screen(2, 1920)],
        [
            new DisplayTarget(@"\\.\DISPLAY1", "path-a", "DELL U2720Q", MonitorTopology.OutputDisplayPortExternal),
            new DisplayTarget(@"\\.\DISPLAY2", "path-b", "DELL U2720Q", MonitorTopology.OutputHdmi),
        ]);
        Assert.Equal(["DELL U2720Q (1)", "DELL U2720Q (2)"], list.Select(m => m.Name));
        Assert.Equal(["path-a", "path-b"], list.Select(m => m.Id));
    }

    [Theory]
    [InlineData(MonitorTopology.OutputInternal, MonitorKind.Internal)]
    [InlineData(MonitorTopology.OutputDisplayPortEmbedded, MonitorKind.Internal)]
    [InlineData(MonitorTopology.OutputUdiEmbedded, MonitorKind.Internal)]
    [InlineData(MonitorTopology.OutputLvds, MonitorKind.Internal)]
    [InlineData(MonitorTopology.OutputHdmi, MonitorKind.External)]
    [InlineData(MonitorTopology.OutputDisplayPortExternal, MonitorKind.External)]
    [InlineData(15u, MonitorKind.External)] // Miracast
    [InlineData(MonitorTopology.OutputOther, MonitorKind.Unknown)]
    public void KindOfOutputTechnology(uint tech, MonitorKind kind) => Assert.Equal(kind, MonitorTopology.KindOf(tech));

    [Theory]
    [InlineData(@"\\.\DISPLAY1", 1)]
    [InlineData(@"\\.\DISPLAY12", 12)]
    [InlineData("WinDisc", null)]
    public void DisplayNumber(string gdi, int? expected) => Assert.Equal(expected, MonitorTopology.DisplayNumber(gdi));

    [Fact]
    public void LegacyIndexMigration()
    {
        var list = LaptopAndTv();
        Assert.Equal((TvPath, "SAMSUNG", true), MonitorTopology.MigrateLegacyPreference(1, null, null, list));
        // Out of range: dropped, but the legacy key still has to go.
        Assert.Equal((null, null, true), MonitorTopology.MigrateLegacyPreference(7, null, null, list));
        // An id that is already there wins.
        Assert.Equal(("x", "X", true), MonitorTopology.MigrateLegacyPreference(0, "x", "X", list));
        // Nothing to migrate.
        Assert.Equal((TvPath, "SAMSUNG", false), MonitorTopology.MigrateLegacyPreference(null, TvPath, "SAMSUNG", list));
    }
}

public class AutoPlacementTests
{
    private static readonly IReadOnlyList<MonitorInfo> Monitors = MonitorTopologyTests.LaptopAndTv();
    private const string Panel = @"\\.\DISPLAY1";
    private const string Tv = @"\\.\DISPLAY2";

    private static PlacementDecision Decide(string? preferred = MonitorTopologyTests.TvPath, string? windowOn = Panel,
        long window = 42, long[]? handled = null, bool startedBefore = false, IReadOnlyList<MonitorInfo>? monitors = null) =>
        AutoPlacement.Decide(new PlacementInput(preferred, monitors ?? Monitors, window, windowOn, handled ?? [], startedBefore));

    [Fact]
    public void MovesNewWindowToConnectedPreferredMonitor() => Assert.Equal(PlacementDecision.Move, Decide());

    [Fact]
    public void UnknownWindowPosition_StillMoves() => Assert.Equal(PlacementDecision.Move, Decide(window: 0, windowOn: null));

    [Fact]
    public void AlreadyThere() => Assert.Equal(PlacementDecision.AlreadyThere, Decide(windowOn: Tv));

    [Fact]
    public void HandledWindowIsNeverMovedAgain() => Assert.Equal(PlacementDecision.AlreadyHandled, Decide(handled: [42]));

    [Fact]
    public void NoPreference() => Assert.Equal(PlacementDecision.NoPreference, Decide(preferred: null));

    [Fact]
    public void PreferredTvOff() =>
        Assert.Equal(PlacementDecision.PreferredNotConnected, Decide(monitors: [Monitors[0]]));

    [Fact]
    public void VlcOlderThanCompanion() => Assert.Equal(PlacementDecision.StartedBeforeCompanion, Decide(startedBefore: true));
}

public class DisplayControllerTests
{
    private sealed class FakeMonitors : IMonitorSource
    {
        public IReadOnlyList<MonitorInfo> List { get; set; } = MonitorTopologyTests.LaptopAndTv();
        public IReadOnlyList<MonitorInfo> GetMonitors() => List;
    }

    private sealed class FakePrefs : IDisplayPreferenceStore
    {
        public string? PreferredId { get; set; }
        public string? PreferredName { get; set; }
        public int Saves { get; private set; }

        public void SetPreferred(string? id, string? name)
        {
            PreferredId = id;
            PreferredName = name;
            Saves++;
        }
    }

    private sealed class FakeVlc : IVlcWindowHost
    {
        public int? ProcessId { get; set; }
        /// <summary>pid → window; the window "appears" after <see cref="WindowAppearsAfter"/> lookups.</summary>
        public Dictionary<int, long> Windows { get; } = new();
        public int WindowAppearsAfter { get; set; }
        public int Lookups { get; private set; }
        public Dictionary<long, string> WindowMonitor { get; } = new();
        public bool IsControllable { get; set; }
        public bool Fullscreen { get; set; }
        /// <summary>The resume question keeps VLC paused.</summary>
        public bool PlaybackHeld { get; set; }
        /// <summary>No video output yet: VLC ignores fullscreen requests.</summary>
        public bool NoVideoOutput { get; set; }
        public List<string> Log { get; } = new();

        public long FindMainWindow(int pid)
        {
            Lookups++;
            if (Lookups <= WindowAppearsAfter) return 0;
            return Windows.TryGetValue(pid, out var w) ? w : 0;
        }

        public string? GetWindowMonitor(long window) => WindowMonitor.TryGetValue(window, out var m) ? m : null;

        public Task<bool?> GetFullscreenAsync(CancellationToken ct) => Task.FromResult<bool?>(IsControllable ? Fullscreen : null);

        public Task SetFullscreenAsync(bool on, CancellationToken ct)
        {
            Log.Add(on ? "fullscreen" : "unfullscreen");
            if (!NoVideoOutput) Fullscreen = on;
            return Task.CompletedTask;
        }

        public bool MoveWindow(long window, MonitorInfo target)
        {
            Log.Add($"move {window} -> {target.GdiDeviceName}");
            WindowMonitor[window] = target.GdiDeviceName;
            return true;
        }
    }

    [Fact]
    public async Task NewWindow_ManualOpen_GoesFullscreenOnceVlcIsControllable()
    {
        var monitors = new FakeMonitors();
        var prefs = new FakePrefs { PreferredId = TvPath };
        var vlc = new FakeVlc { ProcessId = null }; // cold start: the manager has not adopted the process yet
        vlc.Windows[10] = 42;
        vlc.WindowMonitor[42] = @"\\.\DISPLAY1";
        var delays = 0;
        var c = new DisplayController(monitors, prefs, vlc, delay: (_, _) =>
        {
            if (++delays == 2) vlc.ProcessId = 10;
            if (delays == 3) vlc.IsControllable = true; // HTTP comes up a moment after the window
            return Task.CompletedTask;
        })
        {
            WindowWaitTimeout = TimeSpan.FromSeconds(1),
            WindowPollInterval = TimeSpan.FromMilliseconds(100),
            FullscreenWaitTimeout = TimeSpan.FromSeconds(5),
            FullscreenPollInterval = TimeSpan.FromMilliseconds(500),
            FullscreenAfterManualPlacement = () => true,
        };

        Assert.Equal(PlacementDecision.Move, await c.PlaceNewWindowAsync(10, false, null, CancellationToken.None));
        Assert.True(vlc.Fullscreen);
        Assert.Equal("fullscreen", vlc.Log[^1]);
    }

    [Fact]
    public async Task Fullscreen_WaitsWhileTheResumeQuestionHoldsVlcPaused()
    {
        var vlc = new FakeVlc { ProcessId = 10, IsControllable = true, PlaybackHeld = true, NoVideoOutput = true };
        var delays = 0;
        var c = new DisplayController(new FakeMonitors(), new FakePrefs(), vlc, delay: (_, _) =>
        {
            // The phone answers long after the normal timeout (10 polls); the video output appears a moment later.
            if (++delays == 40) vlc.PlaybackHeld = false;
            if (delays == 43) vlc.NoVideoOutput = false;
            return Task.CompletedTask;
        })
        {
            FullscreenWaitTimeout = TimeSpan.FromSeconds(5),
            FullscreenPollInterval = TimeSpan.FromMilliseconds(500),
        };

        Assert.True(await c.EnsureFullscreenAsync(10, CancellationToken.None));
        Assert.True(vlc.Fullscreen);
        Assert.Equal(44, delays); // the request after the output appeared, then the check

        // Without the question the usual timeout applies.
        var vlc2 = new FakeVlc { ProcessId = 10, IsControllable = true, NoVideoOutput = true };
        var c2 = new DisplayController(new FakeMonitors(), new FakePrefs(), vlc2, delay: (_, _) => Task.CompletedTask)
        {
            FullscreenWaitTimeout = TimeSpan.FromSeconds(5),
            FullscreenPollInterval = TimeSpan.FromMilliseconds(500),
        };
        Assert.False(await c2.EnsureFullscreenAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task NewWindow_FullscreenOnlyForManualOpensWhenEnabled()
    {
        // Setting off: moved, not fullscreened.
        var (c1, _, prefs1, vlc1) = Create();
        prefs1.PreferredId = TvPath;
        vlc1.ProcessId = 10;
        vlc1.IsControllable = true;
        vlc1.Windows[10] = 42;
        vlc1.WindowMonitor[42] = @"\\.\DISPLAY1";
        await c1.PlaceNewWindowAsync(10, false, null, CancellationToken.None);
        Assert.False(vlc1.Fullscreen);

        // Companion launch (explicit target): the launch handles fullscreen itself.
        var monitors = new FakeMonitors();
        var prefs = new FakePrefs { PreferredId = TvPath };
        var vlc = new FakeVlc { ProcessId = 11, IsControllable = true };
        vlc.Windows[11] = 43;
        vlc.WindowMonitor[43] = @"\\.\DISPLAY1";
        var c2 = new DisplayController(monitors, prefs, vlc, delay: (_, _) => Task.CompletedTask)
        {
            WindowPollInterval = TimeSpan.FromMilliseconds(100),
            FullscreenAfterManualPlacement = () => true,
        };
        await c2.PlaceNewWindowAsync(11, false, TvPath, CancellationToken.None);
        Assert.False(vlc.Fullscreen);
    }

    private static (DisplayController c, FakeMonitors monitors, FakePrefs prefs, FakeVlc vlc) Create()
    {
        var monitors = new FakeMonitors();
        var prefs = new FakePrefs();
        var vlc = new FakeVlc();
        var c = new DisplayController(monitors, prefs, vlc, delay: (_, _) => Task.CompletedTask)
        {
            WindowWaitTimeout = TimeSpan.FromSeconds(10),
            WindowPollInterval = TimeSpan.FromMilliseconds(100),
        };
        return (c, monitors, prefs, vlc);
    }

    private static CommandArgs Args(string json) => new(JsonDocument.Parse(json).RootElement.Clone());

    private const string TvPath = MonitorTopologyTests.TvPath;
    private const string PanelPath = MonitorTopologyTests.PanelPath;

    [Fact]
    public async Task MoveToDisplay_VlcNotRunning_OnlyRemembers()
    {
        var (c, _, prefs, vlc) = Create();
        var changed = 0;
        c.Changed += () => changed++;
        var reply = await c.ExecuteAsync("moveToDisplay", Args($$"""{"displayId":{{JsonSerializer.Serialize(TvPath)}}}"""), CancellationToken.None);
        Assert.Null(reply);
        Assert.Equal(TvPath, prefs.PreferredId);
        Assert.Equal("SAMSUNG", prefs.PreferredName);
        Assert.Equal(1, changed);
        Assert.Empty(vlc.Log);
    }

    [Fact]
    public async Task MoveToDisplay_LegacyIndex()
    {
        var (c, _, prefs, _) = Create();
        await c.ExecuteAsync("moveToDisplay", Args("""{"display":1}"""), CancellationToken.None);
        Assert.Equal(TvPath, prefs.PreferredId);
        var ex = await Assert.ThrowsAsync<ProtocolException>(() => c.ExecuteAsync("moveToDisplay", Args("""{"display":5}"""), CancellationToken.None));
        Assert.Equal(ErrorCodes.BadRequest, ex.Code);
        ex = await Assert.ThrowsAsync<ProtocolException>(() => c.ExecuteAsync("moveToDisplay", Args("{}"), CancellationToken.None));
        Assert.Equal(ErrorCodes.BadRequest, ex.Code);
    }

    [Fact]
    public async Task MoveToDisplay_TvOff_RemembersWhenVlcIsClosed_ErrorWhenItRuns()
    {
        var (c, monitors, prefs, vlc) = Create();
        monitors.List = [monitors.List[0]];
        await c.MoveToDisplayAsync(TvPath, null, CancellationToken.None);
        Assert.Equal(TvPath, prefs.PreferredId);

        prefs.PreferredId = null;
        vlc.ProcessId = 10;
        var ex = await Assert.ThrowsAsync<ProtocolException>(() => c.MoveToDisplayAsync(TvPath, null, CancellationToken.None));
        Assert.Equal(ErrorCodes.BadRequest, ex.Code);
        Assert.Equal("Этот экран не подключён", ex.MessageFor(CultureInfo.GetCultureInfo("ru")));
        Assert.Equal(TvPath, prefs.PreferredId);
        Assert.Empty(vlc.Log);
    }

    [Fact]
    public async Task MoveToDisplay_FullscreenControllableVlc_UnfullscreenMoveFullscreen()
    {
        var (c, _, prefs, vlc) = Create();
        vlc.ProcessId = 10;
        vlc.Windows[10] = 42;
        vlc.WindowMonitor[42] = @"\\.\DISPLAY1";
        vlc.IsControllable = true;
        vlc.Fullscreen = true;
        await c.MoveToDisplayAsync(TvPath, null, CancellationToken.None);
        Assert.Equal(["unfullscreen", @"move 42 -> \\.\DISPLAY2", "fullscreen"], vlc.Log);
        Assert.Equal(TvPath, prefs.PreferredId);

        // Already there: nothing to do.
        vlc.Log.Clear();
        await c.MoveToDisplayAsync(TvPath, null, CancellationToken.None);
        Assert.Empty(vlc.Log);
    }

    [Fact]
    public async Task MoveToDisplay_WindowedOrUncontrollable_JustMoves()
    {
        var (c, _, _, vlc) = Create();
        vlc.ProcessId = 10;
        vlc.Windows[10] = 42;
        vlc.WindowMonitor[42] = @"\\.\DISPLAY2";
        vlc.Fullscreen = true; // not controllable: the host moves the (borderless) window as is
        await c.MoveToDisplayAsync(PanelPath, null, CancellationToken.None);
        Assert.Equal([@"move 42 -> \\.\DISPLAY1"], vlc.Log);
    }

    [Fact]
    public async Task MoveToDisplay_RunningVlcWithoutWindow_Errors()
    {
        var (c, _, _, vlc) = Create();
        vlc.ProcessId = 10;
        await c.PlaceNewWindowAsync(10, startedBeforeCompanion: true, null, CancellationToken.None);
        var ex = await Assert.ThrowsAsync<ProtocolException>(() => c.MoveToDisplayAsync(TvPath, null, CancellationToken.None));
        Assert.Equal(ErrorCodes.VlcError, ex.Code);
    }

    [Fact]
    public async Task MoveToDisplay_NewVlcWhoseWindowIsNotThereYet_NoError()
    {
        var (c, _, prefs, vlc) = Create();
        vlc.ProcessId = 10; // not announced yet: the one-time placement will use the new preference
        await c.MoveToDisplayAsync(TvPath, null, CancellationToken.None);
        Assert.Equal(TvPath, prefs.PreferredId);
    }

    [Fact]
    public async Task PreferredDisplay_SetAndClear()
    {
        var (c, _, prefs, vlc) = Create();
        vlc.ProcessId = 10;
        vlc.Windows[10] = 42;
        vlc.WindowMonitor[42] = @"\\.\DISPLAY1";
        await c.ExecuteAsync("preferredDisplay", Args($$"""{"displayId":{{JsonSerializer.Serialize(TvPath)}}}"""), CancellationToken.None);
        Assert.Equal(TvPath, prefs.PreferredId);
        Assert.Empty(vlc.Log); // only remembers

        // An id that is not connected now (TV off) is remembered too, keeping the known name.
        await c.ExecuteAsync("preferredDisplay", Args("""{"displayId":null}"""), CancellationToken.None);
        Assert.Null(prefs.PreferredId);
        Assert.Null(prefs.PreferredName);
        await c.ExecuteAsync("preferredDisplay", Args("""{"displayId":"\\\\?\\DISPLAY#OFF"}"""), CancellationToken.None);
        Assert.Equal(@"\\?\DISPLAY#OFF", prefs.PreferredId);
        Assert.Null(prefs.PreferredName);

        // Same value again: not saved again.
        var saves = prefs.Saves;
        c.SetPreferred(@"\\?\DISPLAY#OFF");
        Assert.Equal(saves, prefs.Saves);
    }

    [Fact]
    public async Task ListDisplays_MarksCurrentAndPreferred()
    {
        var (c, _, prefs, vlc) = Create();
        prefs.PreferredId = TvPath;
        vlc.ProcessId = 10;
        vlc.Windows[10] = 42;
        vlc.WindowMonitor[42] = @"\\.\DISPLAY1";
        var reply = Assert.IsType<DisplaysMessage>(await c.ExecuteAsync("listDisplays", default, CancellationToken.None));
        Assert.Equal(TvPath, reply.PreferredId);
        Assert.Collection(reply.Items,
            panel =>
            {
                Assert.Equal(PanelPath, panel.Id);
                Assert.Equal("internal", panel.Kind);
                Assert.True(panel.Current);
                Assert.False(panel.Preferred);
                Assert.True(panel.Primary);
            },
            tv =>
            {
                Assert.Equal(TvPath, tv.Id);
                Assert.Equal("SAMSUNG", tv.Name);
                Assert.Equal("external", tv.Kind);
                Assert.False(tv.Current);
                Assert.True(tv.Preferred);
                Assert.Equal(3840, tv.Bounds.Width);
            });
        Assert.Equal(PanelPath, c.FindVlcMonitor()?.Id);

        var json = ProtocolJson.Serialize(reply);
        Assert.Contains("\"preferredId\":", json);
        Assert.Contains("\"kind\":\"external\"", json);
        Assert.DoesNotContain("nameText", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NewWindow_IsPlacedOnceOnThePreferredMonitor()
    {
        var (c, _, prefs, vlc) = Create();
        prefs.PreferredId = TvPath;
        vlc.ProcessId = 10;
        vlc.Windows[10] = 42;
        vlc.WindowAppearsAfter = 3; // the window shows up a bit later
        vlc.WindowMonitor[42] = @"\\.\DISPLAY1";

        Assert.Equal(PlacementDecision.Move, await c.PlaceNewWindowAsync(10, false, null, CancellationToken.None));
        Assert.Equal([@"move 42 -> \\.\DISPLAY2"], vlc.Log);

        // The user drags it back: the companion does not fight.
        vlc.WindowMonitor[42] = @"\\.\DISPLAY1";
        Assert.Equal(PlacementDecision.AlreadyHandled, await c.PlaceNewWindowAsync(10, false, null, CancellationToken.None));
        Assert.Single(vlc.Log);
    }

    [Fact]
    public async Task NewWindow_OneOffTargetOverridesPreferred()
    {
        var (c, _, prefs, vlc) = Create();
        prefs.PreferredId = TvPath;
        vlc.Windows[11] = 43;
        vlc.WindowMonitor[43] = @"\\.\DISPLAY2";
        Assert.Equal(PlacementDecision.Move, await c.PlaceNewWindowAsync(11, false, PanelPath, CancellationToken.None));
        Assert.Equal([@"move 43 -> \\.\DISPLAY1"], vlc.Log);
        Assert.Equal(TvPath, prefs.PreferredId); // a one-off target is not remembered
    }

    [Fact]
    public async Task NewWindow_SkippedWithoutWaiting()
    {
        var (c, monitors, prefs, vlc) = Create();
        vlc.Windows[10] = 42;
        Assert.Equal(PlacementDecision.NoPreference, await c.PlaceNewWindowAsync(10, false, null, CancellationToken.None));

        prefs.PreferredId = TvPath;
        monitors.List = [monitors.List[0]];
        Assert.Equal(PlacementDecision.PreferredNotConnected, await c.PlaceNewWindowAsync(11, false, null, CancellationToken.None));
        Assert.Equal(PlacementDecision.StartedBeforeCompanion, await c.PlaceNewWindowAsync(12, true, null, CancellationToken.None));
        Assert.Equal(0, vlc.Lookups);
        Assert.Empty(vlc.Log);
    }

    [Fact]
    public async Task NewWindow_AlreadyOnTarget_OrNeverAppears()
    {
        var (c, _, prefs, vlc) = Create();
        prefs.PreferredId = TvPath;
        vlc.Windows[10] = 42;
        vlc.WindowMonitor[42] = @"\\.\DISPLAY2";
        Assert.Equal(PlacementDecision.AlreadyThere, await c.PlaceNewWindowAsync(10, false, null, CancellationToken.None));
        Assert.Equal(PlacementDecision.WindowNotFound, await c.PlaceNewWindowAsync(99, false, null, CancellationToken.None));
        Assert.Equal(1 + 101, vlc.Lookups); // 10 s / 100 ms polls + the first lookup
        Assert.Empty(vlc.Log);
    }

    [Fact]
    public async Task NewWindow_MovedByCommandFirst_IsNotMovedAgain()
    {
        var (c, _, prefs, vlc) = Create();
        vlc.ProcessId = 10;
        vlc.Windows[10] = 42;
        vlc.WindowMonitor[42] = @"\\.\DISPLAY2";
        await c.MoveToDisplayAsync(PanelPath, null, CancellationToken.None); // phone moved it before the poll saw the process
        prefs.PreferredId = TvPath;
        Assert.Equal(PlacementDecision.AlreadyHandled, await c.PlaceNewWindowAsync(10, false, null, CancellationToken.None));
        Assert.Equal([@"move 42 -> \\.\DISPLAY1"], vlc.Log);
    }

    [Fact]
    public void DisplaysMessage_LocalizesFallbackNames()
    {
        var monitors = MonitorTopology.Build([MonitorTopologyTests.Screen(1, 0, primary: true), MonitorTopologyTests.Screen(2, 1920)], []);
        var msg = new DisplaysMessage
        {
            Items = monitors.Select(m => new DisplayEntry { Index = m.Index, Id = m.Id, Name = m.Name, NameText = m.NameText }).ToList(),
        };
        msg.Localize(CultureInfo.GetCultureInfo("ru-RU"));
        Assert.Equal(["Экран 1", "Экран 2"], msg.Items.Select(i => i.Name));
    }
}
