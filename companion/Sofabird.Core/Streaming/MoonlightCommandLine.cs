namespace Sofabird.Core.Streaming;

/// <summary>A computer paired in Moonlight on this PC and the apps it offers (Sunshine's "Desktop", games…).</summary>
public sealed record MoonlightHost(string Name, IReadOnlyList<string> Apps);

/// <summary>
/// The command line of Moonlight-qt (installed separately; GPL-3.0): <c>stream &lt;host&gt; &lt;app&gt;</c> starts a
/// stream from a paired host, <c>quit &lt;host&gt;</c> quits the app on the host and so ends the stream.
/// </summary>
public static class MoonlightCommandLine
{
    /// <summary>Sunshine's app that streams the host's whole screen.</summary>
    public const string DesktopApp = "Desktop";

    public static IReadOnlyList<string> Stream(string host, string app) =>
        ["stream", host, app, "--display-mode", "fullscreen", "--keep-awake"];

    public static IReadOnlyList<string> Quit(string host) => ["quit", host];

    /// <summary>
    /// The host of a running <c>Moonlight.exe stream &lt;host&gt; …</c> process from its arguments (the program path
    /// first), or <c>null</c> for Moonlight's regular window.
    /// </summary>
    public static string? StreamingHost(IReadOnlyList<string> argv)
    {
        for (var i = 1; i < argv.Count - 1; i++)
            if (argv[i].Equals("stream", StringComparison.OrdinalIgnoreCase)) return argv[i + 1];
        return null;
    }

    /// <summary>The requested host (by name, any case), or the first paired one when none is requested.</summary>
    public static MoonlightHost? ChooseHost(IReadOnlyList<MoonlightHost> hosts, string? requested) =>
        string.IsNullOrWhiteSpace(requested)
            ? (hosts.Count > 0 ? hosts[0] : null)
            : hosts.FirstOrDefault(h => h.Name.Equals(requested, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The requested app when the host offers it, otherwise <see cref="DesktopApp"/>; Moonlight may not know the host's
    /// apps yet (never opened), then the request or "Desktop" is passed as is.
    /// </summary>
    public static string ChooseApp(MoonlightHost host, string? requested)
    {
        if (host.Apps.Count == 0) return string.IsNullOrWhiteSpace(requested) ? DesktopApp : requested;
        return host.Apps.FirstOrDefault(a => a.Equals(requested, StringComparison.OrdinalIgnoreCase))
            ?? host.Apps.FirstOrDefault(a => a.Equals(DesktopApp, StringComparison.OrdinalIgnoreCase))
            ?? host.Apps[0];
    }
}
