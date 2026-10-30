using Microsoft.Extensions.Logging;
using Sofabird.Core.Lua;

namespace Sofabird.Companion.Vlc;

/// <summary>Our Lua pages (companion/lua/requests/*.json), embedded into the assembly as resources.</summary>
public static class EmbeddedLuaPages
{
    private static readonly string[] Names = ["tracks.json", "setvar.json"];

    /// <summary>Installs our pages next to <paramref name="vlc"/>'s own ones, in the user's VLC folder.</summary>
    public static LuaPagesInstaller Installer(VlcInstallation vlc, ILoggerFactory lf) =>
        new(vlc.LuaHttpDir, LuaPagesInstaller.DefaultTargetDir, Load(), lf.CreateLogger<LuaPagesInstaller>());

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
