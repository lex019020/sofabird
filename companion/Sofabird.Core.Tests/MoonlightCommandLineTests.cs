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
        Assert.Equal(["stream", "GAMING-PC", "Desktop", "--display-mode", "fullscreen", "--keep-awake"],
            MoonlightCommandLine.Stream("GAMING-PC", "Desktop"));
        Assert.Equal(["quit", "GAMING-PC"], MoonlightCommandLine.Quit("GAMING-PC"));
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
