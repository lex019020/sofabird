using Sofabird.Core.Streaming;
using Xunit;

namespace Sofabird.Core.Tests;

public class MoonlightCommandLineTests
{
    private static readonly MoonlightHost Gaming = new("GAMING-PC", ["Desktop", "Steam Big Picture"]);
    private static readonly MoonlightHost Office = new("office-pc", []);

    [Fact]
    public void Stream_and_quit_arguments()
    {
        Assert.Equal(["stream", "GAMING-PC", "Desktop", "--display-mode", "borderless", "--keep-awake", "--background-gamepad"],
            MoonlightCommandLine.Stream("GAMING-PC", "Desktop"));
        Assert.Equal(["quit", "GAMING-PC"], MoonlightCommandLine.Quit("GAMING-PC"));
    }

    [Fact]
    public void List_arguments() => Assert.Equal(["list", "GAMING-PC", "--csv"], MoonlightCommandLine.List("GAMING-PC"));

    /// <summary>What Moonlight-qt prints for <c>list &lt;host&gt; --csv</c>, with a log line in between.</summary>
    private const string ListOutput = """
        Name, ID, HDR Support, App Collection Game, Hidden, Direct Launch, Path to Boxart
        "Desktop", 881448767, false, false, false, false, C:/Users/me/AppData/Local/Moonlight Game Streaming Project/Moonlight/boxart/1/881448767.png
        00:00:01 - Qt Info: Server GPU: NVIDIA GeForce RTX 4070
        "Steam Big Picture", 1093255277, true, false, false, false, C:/boxart/1093255277.png
        "Halo: The Master Chief Collection, Remastered", 12, true, false, false, false,
        "Secret Game", 13, false, false, true, false, C:/boxart/13.png
        "Отчёт "Игра"", 14, false, false, false, false, C:/boxart/14.png
        """;

    [Fact]
    public void App_list_comes_from_the_quoted_names_without_hidden_ones()
    {
        Assert.Equal(
            ["Desktop", "Steam Big Picture", "Halo: The Master Chief Collection, Remastered", "Отчёт \"Игра\""],
            MoonlightCommandLine.ParseAppList(ListOutput.ReplaceLineEndings("\r\n")));
    }

    [Fact]
    public void An_app_list_needs_its_header_or_quoted_lines()
    {
        // A host with no apps: the header alone is a valid (empty) list.
        var none = MoonlightCommandLine.ParseAppList("Name, ID, HDR Support, App Collection Game, Hidden, Direct Launch, Path to Boxart\n");
        Assert.NotNull(none);
        Assert.Empty(none);
        // Without a header the quoted lines still count (and nothing is taken for hidden).
        Assert.Equal(["Desktop"], MoonlightCommandLine.ParseAppList("\"Desktop\", 1, false, false, true, false, x.png"));
        // An error message, nothing at all, or the plain (non-CSV) list of an older Moonlight: not a list.
        Assert.Null(MoonlightCommandLine.ParseAppList("Failed to connect to GAMING-PC\n"));
        Assert.Null(MoonlightCommandLine.ParseAppList(""));
        Assert.Null(MoonlightCommandLine.ParseAppList("Desktop\nSteam Big Picture\n"));
    }

    [Fact]
    public void An_app_list_with_mangled_names_is_not_trusted() =>
        Assert.Null(MoonlightCommandLine.ParseAppList("Name, ID, Hidden\n\"Desktop\", 1, false\n\"\uFFFD\uFFFD\", 2, false\n"));

    [Fact]
    public void Desktop_comes_first_then_the_hosts_order_without_repeats()
    {
        Assert.Equal(["desktop", "Steam Big Picture", "Game"],
            MoonlightCommandLine.DesktopFirst(["Steam Big Picture", "desktop", "Game", "game", "Desktop"]));
        Assert.Equal(["Game"], MoonlightCommandLine.DesktopFirst(["Game"]));
        Assert.Empty(MoonlightCommandLine.DesktopFirst([]));
    }

    [Fact]
    public void Registry_flags_are_qt_booleans()
    {
        Assert.True(MoonlightCommandLine.IsTrue("true"));
        Assert.True(MoonlightCommandLine.IsTrue("True"));
        Assert.True(MoonlightCommandLine.IsTrue(1));
        Assert.False(MoonlightCommandLine.IsTrue("false"));
        Assert.False(MoonlightCommandLine.IsTrue(null));
        Assert.False(MoonlightCommandLine.IsTrue(0));
    }

    [Fact]
    public void Streaming_host_comes_from_a_stream_command_line_only()
    {
        Assert.Equal("GAMING-PC", MoonlightCommandLine.StreamingHost(
            [@"C:\Program Files\Moonlight Game Streaming\Moonlight.exe", "stream", "GAMING-PC", "Desktop", "--keep-awake"]));
        Assert.Null(MoonlightCommandLine.StreamingHost([@"C:\Program Files\Moonlight Game Streaming\Moonlight.exe"]));
        Assert.Null(MoonlightCommandLine.StreamingHost(["Moonlight.exe", "stream"])); // no host
        // The program path itself is never taken for the verb.
        Assert.Null(MoonlightCommandLine.StreamingHost(["stream", "list"]));
    }

    [Fact]
    public void The_requested_host_or_the_first_paired_one()
    {
        Assert.Equal(Gaming, MoonlightCommandLine.ChooseHost([Gaming, Office], null));
        Assert.Equal(Office, MoonlightCommandLine.ChooseHost([Gaming, Office], "OFFICE-PC"));
        Assert.Null(MoonlightCommandLine.ChooseHost([Gaming], "unknown"));
        Assert.Null(MoonlightCommandLine.ChooseHost([], null));
    }

    [Fact]
    public void Desktop_unless_another_offered_app_is_requested()
    {
        Assert.Equal("Desktop", MoonlightCommandLine.ChooseApp(Gaming, null));
        Assert.Equal("Steam Big Picture", MoonlightCommandLine.ChooseApp(Gaming, "steam big picture"));
        Assert.Equal("Desktop", MoonlightCommandLine.ChooseApp(Gaming, "Not offered"));
        Assert.Equal("Steam Big Picture", MoonlightCommandLine.ChooseApp(new MoonlightHost("pc", ["Steam Big Picture"]), null));
        // Apps not known yet: the request or Desktop goes to Moonlight as is.
        Assert.Equal("Desktop", MoonlightCommandLine.ChooseApp(Office, null));
        Assert.Equal("Game", MoonlightCommandLine.ChooseApp(Office, "Game"));
    }
}
