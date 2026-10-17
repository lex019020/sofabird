using System.IO.Compression;
using System.Text.Json;

namespace Sofabird.Core.YouTube;

/// <summary>
/// A Firefox add-on file (XPI, a zip) checked before it goes into the TV profile: its id and version from
/// <c>manifest.json</c>, and whether it carries Mozilla's signature (<c>META-INF/</c>). Firefox verifies the signature
/// itself; this only keeps a wrong or broken download out.
/// </summary>
public sealed record AddonPackage(string Id, string Version, bool MozillaSigned)
{
    /// <summary>The add-on in <paramref name="xpi"/>, or <c>null</c> when it is not a readable WebExtension.</summary>
    public static AddonPackage? Inspect(Stream xpi)
    {
        try
        {
            using var zip = new ZipArchive(xpi, ZipArchiveMode.Read, leaveOpen: true);
            var entry = zip.GetEntry("manifest.json");
            if (entry is null) return null;
            using var stream = entry.Open();
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            var id = Gecko(root, "browser_specific_settings") ?? Gecko(root, "applications");
            var version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(version)) return null;
            var mozillaSigned = zip.Entries.Any(e => e.FullName is "META-INF/mozilla.rsa" or "META-INF/cose.sig");
            return new AddonPackage(id, version, mozillaSigned);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException)
        {
            return null;
        }
    }

    private static string? Gecko(JsonElement root, string key) =>
        root.TryGetProperty(key, out var settings) && settings.ValueKind == JsonValueKind.Object
        && settings.TryGetProperty("gecko", out var gecko) && gecko.ValueKind == JsonValueKind.Object
        && gecko.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString()
            : null;
}
