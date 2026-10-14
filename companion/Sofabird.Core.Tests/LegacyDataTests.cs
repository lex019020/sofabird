using Sofabird.Core.Setup;
using Xunit;

namespace Sofabird.Core.Tests;

public sealed class LegacyDataTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sofabird-legacy-" + Guid.NewGuid().ToString("N")[..8]);
    private string Legacy => Path.Combine(_root, "VlcRemote");
    private string Data => Path.Combine(_root, "Sofabird");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void WriteLegacy(params string[] names)
    {
        Directory.CreateDirectory(Legacy);
        foreach (var n in names) File.WriteAllText(Path.Combine(Legacy, n), "old " + n);
    }

    [Fact]
    public void The_first_start_carries_settings_history_and_metadata_but_not_pairings()
    {
        WriteLegacy("settings.json", "history.json", "library-metadata.json", "paired.json");
        Directory.CreateDirectory(Path.Combine(Data, "logs")); // the log folder exists before the migration runs

        var copied = LegacyData.CopyIfFirstStart(Legacy, Data);

        Assert.Equal(["settings.json", "history.json", "library-metadata.json"], copied);
        Assert.Equal("old history.json", File.ReadAllText(Path.Combine(Data, "history.json")));
        Assert.False(File.Exists(Path.Combine(Data, "paired.json")));
    }

    [Fact]
    public void Nothing_is_copied_once_Sofabird_has_its_own_settings()
    {
        WriteLegacy("settings.json", "history.json");
        Directory.CreateDirectory(Data);
        File.WriteAllText(Path.Combine(Data, "settings.json"), "new");

        Assert.Empty(LegacyData.CopyIfFirstStart(Legacy, Data));
        Assert.Equal("new", File.ReadAllText(Path.Combine(Data, "settings.json")));
        Assert.False(File.Exists(Path.Combine(Data, "history.json")));
    }

    [Fact]
    public void No_earlier_install_means_nothing_to_do()
    {
        Directory.CreateDirectory(_root);
        Assert.Empty(LegacyData.CopyIfFirstStart(Legacy, Data));
    }
}
