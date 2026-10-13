using VlcRemote.Core.Vlc;
using Xunit;

namespace VlcRemote.Core.Tests;

public sealed class LocalFileBrowserTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vlcr-browse-" + Guid.NewGuid().ToString("N")[..8]);

    public LocalFileBrowserTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "Фильмы 2001"));
        Directory.CreateDirectory(Path.Combine(_dir, "a folder"));
        File.WriteAllText(Path.Combine(_dir, "b movie.mkv"), "x");
        File.WriteAllText(Path.Combine(_dir, "A movie.mkv"), "xyz");
        var hidden = Path.Combine(_dir, "hidden.txt");
        File.WriteAllText(hidden, "h");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        File.WriteAllText(Path.Combine(_dir, ".dotfile"), "d");
    }

    public void Dispose()
    {
        try { File.SetAttributes(Path.Combine(_dir, "hidden.txt"), FileAttributes.Normal); Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Lists_folders_first_then_files_sorted_and_skips_hidden()
    {
        var items = new LocalFileBrowser().Browse(LocalFileBrowser.ToUri(_dir, isDir: true));
        Assert.Equal(new[] { "a folder", "Фильмы 2001", "A movie.mkv", "b movie.mkv" }, items.Select(i => i.Name).ToArray());
        Assert.Equal("dir", items[0].Type);
        Assert.EndsWith("/", items[0].Uri);
        Assert.Equal(3, items[2].Size);
        Assert.StartsWith("file:///", items[2].Uri);
        Assert.Contains("A%20movie.mkv", items[2].Uri);
    }

    [Fact]
    public void Uri_round_trips_with_spaces_and_cyrillic()
    {
        var sub = Path.Combine(_dir, "Фильмы 2001");
        var uri = LocalFileBrowser.ToUri(sub, isDir: true);
        Assert.Equal(sub + Path.DirectorySeparatorChar, LocalFileBrowser.ToLocalPath(uri));
        Assert.Empty(new LocalFileBrowser().Browse(uri));
    }

    [Fact]
    public void Unc_uri_maps_to_unc_path()
    {
        Assert.Equal(@"\\192.168.0.43\shared\movies\", LocalFileBrowser.ToLocalPath("file://192.168.0.43/shared/movies/"));
        Assert.Equal("file://192.168.0.43/shared/x%20y/", LocalFileBrowser.ToUri(@"\\192.168.0.43\shared\x y", isDir: true));
    }

    [Fact]
    public void Root_lists_extra_roots_and_ready_drives()
    {
        var items = new LocalFileBrowser(() => new[] { _dir, _dir, @"Z:\does-not-exist" }).Browse("file:///");
        Assert.Single(items, i => i.Uri == LocalFileBrowser.ToUri(_dir, isDir: true));
        Assert.Contains(items, i => i.Uri.StartsWith("file:///C:/", StringComparison.OrdinalIgnoreCase));
        Assert.All(items, i => Assert.Equal("dir", i.Type));
    }

    [Fact]
    public void Missing_folder_throws()
    {
        Assert.Throws<DirectoryNotFoundException>(() => new LocalFileBrowser().Browse(LocalFileBrowser.ToUri(Path.Combine(_dir, "nope"), isDir: true)));
    }
}
