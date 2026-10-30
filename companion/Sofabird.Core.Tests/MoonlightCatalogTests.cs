using System.Collections.Concurrent;
using Sofabird.Core.Protocol;
using Sofabird.Core.Streaming;
using Xunit;

namespace Sofabird.Core.Tests;

public sealed class MoonlightCatalogTests
{
    private const string Header = "Name, ID, HDR Support, App Collection Game, Hidden, Direct Launch, Path to Boxart\n";

    /// <summary>Moonlight's settings: what it remembers of each paired computer.</summary>
    private static readonly IReadOnlyList<MoonlightHost> Paired =
    [
        new("GAMING-PC", ["Steam Big Picture", "Desktop"]),
        new("office-pc", []),
    ];

    /// <summary><c>Moonlight.exe list</c> as a fake: per host its output, a failure (<c>null</c>) or no answer at all.</summary>
    private sealed class FakeMoonlight
    {
        public ConcurrentDictionary<string, int> Calls { get; } = new();
        public Dictionary<string, Func<CancellationToken, Task<string?>>> Hosts { get; } = new();

        public Task<string?> ListAsync(string host, CancellationToken ct)
        {
            Calls.AddOrUpdate(host, 1, (_, n) => n + 1);
            return Hosts.TryGetValue(host, out var run) ? run(ct) : Task.FromResult<string?>(null);
        }

        public static Func<CancellationToken, Task<string?>> Prints(string output) => _ => Task.FromResult<string?>(output);

        /// <summary>A computer that is off: Moonlight seeks it until it is stopped.</summary>
        public static Func<CancellationToken, Task<string?>> NeverAnswers() => async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        };
    }

    private static MoonlightCatalog Catalog(FakeMoonlight moonlight, TimeSpan? timeout = null) =>
        new(() => Paired, moonlight.ListAsync, timeout ?? TimeSpan.FromSeconds(5));

    [Fact]
    public async Task Without_refresh_the_remembered_apps_come_desktop_first_and_moonlight_is_not_run()
    {
        var moonlight = new FakeMoonlight();
        var items = await Catalog(moonlight).ListAsync(refresh: false, CancellationToken.None);

        Assert.Equal(["GAMING-PC", "office-pc"], items.Select(h => h.Name));
        Assert.Equal(["Desktop", "Steam Big Picture"], items[0].Apps);
        Assert.Empty(items[1].Apps);
        Assert.All(items, h => Assert.Null(h.Stale));
        Assert.Empty(moonlight.Calls);
    }

    [Fact]
    public async Task A_refresh_asks_every_computer_and_keeps_the_remembered_list_of_one_that_does_not_answer()
    {
        var moonlight = new FakeMoonlight();
        moonlight.Hosts["GAMING-PC"] = FakeMoonlight.Prints(Header + "\"Steam Big Picture\", 2, false, false, false, false, a.png\n"
            + "\"Desktop\", 1, false, false, false, false, b.png\n\"Cyberpunk 2077\", 3, true, false, false, false, c.png\n");
        moonlight.Hosts["office-pc"] = FakeMoonlight.NeverAnswers();
        var catalog = Catalog(moonlight, TimeSpan.FromMilliseconds(200));

        var items = await catalog.ListAsync(refresh: true, CancellationToken.None);

        Assert.Equal(["Desktop", "Steam Big Picture", "Cyberpunk 2077"], items[0].Apps);
        Assert.Null(items[0].Stale);
        Assert.Empty(items[1].Apps);
        Assert.True(items[1].Stale);
        Assert.Equal(1, moonlight.Calls["GAMING-PC"]);
        Assert.Equal(1, moonlight.Calls["office-pc"]);

        // The fresh list stays (Moonlight may not have saved it): for the next plain request and for starting a stream.
        var later = await catalog.ListAsync(refresh: false, CancellationToken.None);
        Assert.Equal(["Desktop", "Steam Big Picture", "Cyberpunk 2077"], later[0].Apps);
        Assert.Null(later[0].Stale);
        Assert.Equal("Cyberpunk 2077", MoonlightCommandLine.ChooseApp(catalog.Hosts()[0], "cyberpunk 2077"));
    }

    [Fact]
    public async Task A_failed_list_keeps_what_was_known()
    {
        var moonlight = new FakeMoonlight();
        moonlight.Hosts["GAMING-PC"] = FakeMoonlight.Prints("Failed to connect to GAMING-PC\n"); // not a list
        moonlight.Hosts["office-pc"] = _ => throw new InvalidOperationException("Moonlight.exe is gone");

        var items = await Catalog(moonlight).ListAsync(refresh: true, CancellationToken.None);

        Assert.Equal(["Desktop", "Steam Big Picture"], items[0].Apps);
        Assert.True(items[0].Stale);
        Assert.True(items[1].Stale);
    }

    [Fact]
    public async Task Refreshes_at_the_same_time_share_one_run_of_moonlight()
    {
        var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var moonlight = new FakeMoonlight();
        moonlight.Hosts["GAMING-PC"] = _ => release.Task;
        moonlight.Hosts["office-pc"] = FakeMoonlight.Prints(Header + "\"Desktop\", 1, false, false, false, false, x.png\n");
        var catalog = Catalog(moonlight);

        var first = catalog.ListAsync(refresh: true, CancellationToken.None);
        var second = catalog.ListAsync(refresh: true, CancellationToken.None);
        // A phone that gives up only stops its own waiting.
        using var gaveUp = new CancellationTokenSource();
        var third = catalog.ListAsync(refresh: true, gaveUp.Token);
        await gaveUp.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => third);

        release.SetResult(Header + "\"Desktop\", 1, false, false, false, false, x.png\n");
        var (a, b) = (await first, await second);
        Assert.Equal(a, b);
        Assert.Equal(["Desktop"], a[0].Apps);
        Assert.Equal(1, moonlight.Calls["GAMING-PC"]);

        // Once done, the next refresh runs Moonlight again.
        await catalog.ListAsync(refresh: true, CancellationToken.None);
        Assert.Equal(2, moonlight.Calls["GAMING-PC"]);
    }

    [Fact]
    public void The_reply_serializes_with_stale_only_when_set()
    {
        var json = ProtocolJson.Serialize(new MoonlightHostsMessage
        {
            Id = "9",
            Items =
            [
                new MoonlightHostEntry { Name = "GAMING-PC", Apps = ["Desktop", "Steam Big Picture"] },
                new MoonlightHostEntry { Name = "office-pc", Apps = [], Stale = true },
            ],
        });
        Assert.Equal(
            """{"t":"moonlightHosts","id":"9","items":[""" +
            """{"name":"GAMING-PC","apps":["Desktop","Steam Big Picture"]},{"name":"office-pc","apps":[],"stale":true}]}""",
            json);
        var back = Assert.IsType<MoonlightHostsMessage>(ProtocolJson.Deserialize(json));
        Assert.Equal("Steam Big Picture", back.Items[0].Apps[1]);
    }
}
