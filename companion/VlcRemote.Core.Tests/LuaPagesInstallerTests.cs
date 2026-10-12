using System.Text;
using VlcRemote.Core.Lua;
using Xunit;

namespace VlcRemote.Core.Tests;

public class LuaPagesInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vlcremote-tests", Guid.NewGuid().ToString("N"));
    private readonly string _stock;
    private readonly string _target;
    private readonly Dictionary<string, byte[]> _pages;

    public LuaPagesInstallerTests()
    {
        _stock = Path.Combine(_root, "stock", "http");
        _target = Path.Combine(_root, "appdata", "vlc", "lua", "http");
        // Имитация штатной папки VLC: requests/*.json, вложенные папки, бинарный файл.
        Directory.CreateDirectory(Path.Combine(_stock, "requests"));
        Directory.CreateDirectory(Path.Combine(_stock, "css", "ui-lightness", "images"));
        File.WriteAllText(Path.Combine(_stock, "requests", "status.json"), "<?vlc print('status') ?>");
        File.WriteAllText(Path.Combine(_stock, "requests", "playlist.json"), "<?vlc print('playlist') ?>");
        File.WriteAllText(Path.Combine(_stock, "index.html"), "<html/>");
        File.WriteAllBytes(Path.Combine(_stock, "css", "ui-lightness", "images", "a.png"), [1, 2, 3]);
        File.SetAttributes(Path.Combine(_stock, "index.html"), FileAttributes.ReadOnly);
        _pages = new Dictionary<string, byte[]>
        {
            ["tracks.json"] = File.ReadAllBytes(Fixtures.PathOf(Path.Combine("lua", "tracks.json"))),
            ["setvar.json"] = File.ReadAllBytes(Fixtures.PathOf(Path.Combine("lua", "setvar.json"))),
        };
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_root, true);
        }
        catch { }
    }

    [Fact]
    public void Install_CopiesStockTreeAndAddsOurPagesAndVersionFile()
    {
        var inst = new LuaPagesInstaller(_stock, _target, _pages);
        Assert.True(inst.NeedsInstall("3.0.23"));

        inst.Install("3.0.23");

        Assert.True(File.Exists(Path.Combine(_target, "requests", "status.json")));
        Assert.True(File.Exists(Path.Combine(_target, "requests", "playlist.json")));
        Assert.True(File.Exists(Path.Combine(_target, "index.html")));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(_target, "css", "ui-lightness", "images", "a.png")));
        var tracks = File.ReadAllText(Path.Combine(_target, "requests", "tracks.json"));
        Assert.Contains("vlcremote = 1", tracks);
        Assert.StartsWith("<?vlc", tracks);
        Assert.Contains("vlc.var.set", File.ReadAllText(Path.Combine(_target, "requests", "setvar.json")));
        var version = File.ReadAllText(Path.Combine(_target, LuaPagesInstaller.VersionFileName));
        Assert.Contains("vlc=3.0.23", version);
        Assert.Contains("pages=" + inst.PagesVersion, version);

        Assert.True(inst.IsInstalled("3.0.23"));
        Assert.False(inst.NeedsInstall("3.0.23"));
        // Штатный файл был read-only — копия должна быть перезаписываемой (повторная установка).
        inst.Install("3.0.23");
    }

    [Fact]
    public void NeedsInstall_WhenVlcVersionChanges_OrPagesChange()
    {
        var inst = new LuaPagesInstaller(_stock, _target, _pages);
        inst.Install("3.0.21");
        Assert.False(inst.NeedsInstall("3.0.21"));
        Assert.True(inst.NeedsInstall("3.0.23"));

        var newer = new Dictionary<string, byte[]>(_pages) { ["tracks.json"] = Encoding.UTF8.GetBytes("<?vlc print('v2') ?>") };
        var inst2 = new LuaPagesInstaller(_stock, _target, newer);
        Assert.NotEqual(inst.PagesVersion, inst2.PagesVersion);
        Assert.True(inst2.NeedsInstall("3.0.21"));
    }

    [Fact]
    public void NeedsInstall_FalseWhenStockMissing_InstallThrows()
    {
        var inst = new LuaPagesInstaller(Path.Combine(_root, "nope"), _target, _pages);
        Assert.False(inst.NeedsInstall("3.0.23"));
        Assert.Throws<DirectoryNotFoundException>(() => inst.Install("3.0.23"));
    }

    [Fact]
    public void Uninstall_RemovesTargetOnly()
    {
        var inst = new LuaPagesInstaller(_stock, _target, _pages);
        inst.Install("3.0.23");
        inst.Uninstall();
        Assert.False(Directory.Exists(_target));
        Assert.True(File.Exists(Path.Combine(_stock, "requests", "status.json")));
        inst.Uninstall(); // идемпотентно
    }
}
