namespace Sofabird.Core.Setup;

/// <summary>
/// The data of the companion under its earlier name, "VLC Remote Companion" (<c>%LocalAppData%\VlcRemote</c>),
/// carried over once to Sofabird: settings, watch history and the library's metadata. Paired phones are not: the phone
/// app has a new identity too and pairs again.
/// </summary>
public static class LegacyData
{
    public static readonly IReadOnlyList<string> CarriedFiles = ["settings.json", "history.json", "library-metadata.json"];

    /// <summary>
    /// Copies <see cref="CarriedFiles"/> from <paramref name="legacyDir"/> when <paramref name="dataDir"/> has no
    /// settings yet (the first start of Sofabird). Existing files are never overwritten. Returns the copied names.
    /// </summary>
    public static IReadOnlyList<string> CopyIfFirstStart(string legacyDir, string dataDir)
    {
        if (!Directory.Exists(legacyDir) || File.Exists(Path.Combine(dataDir, "settings.json"))) return [];
        Directory.CreateDirectory(dataDir);
        var copied = new List<string>();
        foreach (var name in CarriedFiles)
        {
            var from = Path.Combine(legacyDir, name);
            var to = Path.Combine(dataDir, name);
            if (!File.Exists(from) || File.Exists(to)) continue;
            File.Copy(from, to);
            copied.Add(name);
        }
        return copied;
    }
}
