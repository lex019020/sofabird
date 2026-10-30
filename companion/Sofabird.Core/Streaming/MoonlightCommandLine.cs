using System.Text.RegularExpressions;

namespace Sofabird.Core.Streaming;

/// <summary>A computer paired in Moonlight on this PC and the apps it offers (Sunshine's "Desktop", games…).</summary>
public sealed record MoonlightHost(string Name, IReadOnlyList<string> Apps);

/// <summary>
/// The command line of Moonlight-qt (installed separately; GPL-3.0): <c>stream &lt;host&gt; &lt;app&gt;</c> starts a
/// stream from a paired host, <c>quit &lt;host&gt;</c> quits the app on the host and so ends the stream,
/// <c>list &lt;host&gt; --csv</c> asks the host for its apps.
/// </summary>
public static partial class MoonlightCommandLine
{
    /// <summary>Sunshine's app that streams the host's whole screen.</summary>
    public const string DesktopApp = "Desktop";

    /// <summary>
    /// The host's apps as Moonlight-qt fetches them from it now: printed as CSV, without a window (the app exits when
    /// done; a host that is off or asleep makes it wait and fail after a while).
    /// </summary>
    public static IReadOnlyList<string> List(string host) => ["list", host, "--csv"];

    /// <summary>
    /// The app names in the output of <c>Moonlight.exe list &lt;host&gt; --csv</c>, in the host's order and without
    /// the ones hidden in Moonlight, or <c>null</c> when the output is not such a list. Moonlight-qt prints a header
    /// (<c>Name, ID, HDR Support, App Collection Game, Hidden, Direct Launch, Path to Boxart</c>) and a line per app
    /// with its name in quotes (<c>"Steam Big Picture", 2, false, false, false, false, C:/…/2.png</c>); anything else
    /// (log lines) is skipped. A name that did not come through as text (U+FFFD, a codepage mix-up) makes the whole
    /// output untrusted: the phone would send that name back and Moonlight would not know it.
    /// </summary>
    public static IReadOnlyList<string>? ParseAppList(string output)
    {
        var apps = new List<string>();
        var header = false;
        var hiddenColumn = -1;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (!header && line.StartsWith("Name,", StringComparison.OrdinalIgnoreCase))
            {
                header = true;
                hiddenColumn = Array.FindIndex(line.Split(','), c => c.Trim().Equals("Hidden", StringComparison.OrdinalIgnoreCase));
                continue;
            }
            var match = AppLine().Match(line);
            if (!match.Success) continue;
            var name = match.Groups["name"].Value;
            if (name.Contains('\uFFFD', StringComparison.Ordinal)) return null;
            // The columns as the header numbers them: the name, the id, then the rest (the box art path is last).
            List<string> columns =
                [name, match.Groups["id"].Value, .. match.Groups["rest"].Value.Split(',').Skip(1).Select(c => c.Trim())];
            var hidden = hiddenColumn > 1 && hiddenColumn < columns.Count
                && columns[hiddenColumn].Equals("true", StringComparison.OrdinalIgnoreCase);
            if (name.Trim().Length > 0 && !hidden) apps.Add(name);
        }
        return header || apps.Count > 0 ? apps : null;
    }

    [GeneratedRegex("""^"(?<name>.*?)",\s*(?<id>-?\d+)(?<rest>.*)$""")]
    private static partial Regex AppLine();

    /// <summary>The apps for the phone: <see cref="DesktopApp"/> first (the usual choice), then the host's order; no repeats.</summary>
    public static IReadOnlyList<string> DesktopFirst(IEnumerable<string> apps)
    {
        var distinct = apps.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return [.. distinct.Where(IsDesktop), .. distinct.Where(a => !IsDesktop(a))];
    }

    private static bool IsDesktop(string app) => app.Equals(DesktopApp, StringComparison.OrdinalIgnoreCase);

    /// <summary>A flag of Moonlight's settings in the registry (Qt writes booleans as <c>"true"</c>/<c>"false"</c>).</summary>
    public static bool IsTrue(object? value) => value switch
    {
        string s => s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1",
        int i => i != 0,
        _ => false,
    };

    /// <summary>
    /// Borderless rather than exclusive fullscreen: it covers the screen just the same, but is not minimised when it loses
    /// focus (Moonlight is started from the background, behind VLC). The gamepad works without focus too: on a TV PC any
    /// window (a console, a notification) may take it, and Moonlight would otherwise drop the controller's input.
    /// </summary>
    public static IReadOnlyList<string> Stream(string host, string app) =>
        ["stream", host, app, "--display-mode", "borderless", "--keep-awake", "--background-gamepad"];

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
