using System.IO.Compression;
using System.Text;
using Sofabird.Core.YouTube;
using Xunit;

namespace Sofabird.Core.Tests;

public class AddonPackageTests
{
    private static MemoryStream Zip(params (string Name, string Text)[] files)
    {
        var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in files)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
                w.Write(text);
            }
        }
        buffer.Position = 0;
        return buffer;
    }

    [Fact]
    public void A_signed_ublock_is_recognised()
    {
        using var xpi = Zip(
            ("manifest.json", """{"name":"uBlock Origin","version":"1.66.4","browser_specific_settings":{"gecko":{"id":"uBlock0@raymondhill.net"}}}"""),
            ("META-INF/cose.sig", "sig"),
            ("META-INF/mozilla.rsa", "sig"));
        Assert.Equal(new AddonPackage("uBlock0@raymondhill.net", "1.66.4", true), AddonPackage.Inspect(xpi));
    }

    [Fact]
    public void The_old_applications_key_and_a_missing_signature()
    {
        using var xpi = Zip(("manifest.json", """{"version":"1.0","applications":{"gecko":{"id":"x@example.org"}}}"""));
        Assert.Equal(new AddonPackage("x@example.org", "1.0", false), AddonPackage.Inspect(xpi));
    }

    [Fact]
    public void Not_an_addon()
    {
        using var noManifest = Zip(("readme.txt", "hi"));
        Assert.Null(AddonPackage.Inspect(noManifest));
        using var noId = Zip(("manifest.json", """{"version":"1.0"}"""));
        Assert.Null(AddonPackage.Inspect(noId));
        using var html = new MemoryStream(Encoding.UTF8.GetBytes("<html>error</html>"));
        Assert.Null(AddonPackage.Inspect(html));
    }
}
