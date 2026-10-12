namespace VlcRemote.Companion.Vlc;

/// <summary>Наши Lua-страницы (companion/lua/requests/*.json), встроенные в сборку как ресурсы.</summary>
public static class EmbeddedLuaPages
{
    private static readonly string[] Names = ["tracks.json", "setvar.json"];

    public static IReadOnlyDictionary<string, byte[]> Load()
    {
        var asm = typeof(EmbeddedLuaPages).Assembly;
        var dict = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var name in Names)
        {
            using var stream = asm.GetManifestResourceStream("lua.requests." + name)
                               ?? throw new InvalidOperationException($"Embedded resource lua.requests.{name} not found");
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            dict[name] = ms.ToArray();
        }
        return dict;
    }
}
