using Sofabird.Companion.Tests.Fake;
using Sofabird.Companion.Tests.Support;
using Sofabird.Core.History;
using Sofabird.Core.Protocol;
using Sofabird.Core.Session;
using Sofabird.Core.Vlc;
using Xunit;
using Xunit.Abstractions;

namespace Sofabird.Companion.Tests;

/// <summary>
/// The companion's <see cref="Vlc.VlcManager"/> against a fake VLC: what the phones see (states, events) and what VLC
/// is asked to do, from VLC appearing to VLC closing.
/// </summary>
public sealed class VlcManagerScenarioTests(ITestOutputHelper output)
{
    private static readonly FakeMedia Movie = FakeMedia.Movie("file:///D:/Video/Movies/Arrival.2016.mkv", "Arrival", 6960);

    private static readonly FakeMedia Episode1 = new("file:///D:/Video/Show/Show.S01E01.mkv", "Show.S01E01.mkv", 2700,
    [
        FakeTrack.Video(0),
        FakeTrack.Audio(1, "English", "Original / AC3 5.1 @ 448 kbps"),
        FakeTrack.Audio(2, "Russian", "Dub / AC3 5.1 @ 448 kbps"),
        FakeTrack.Subtitle(3, "English", "Full / SRT"),
        FakeTrack.Subtitle(4, "Russian", "Full / SRT"),
    ]);

    /// <summary>The next episode: the same languages, other track ids and an extra commentary track.</summary>
    private static readonly FakeMedia Episode2 = new("file:///D:/Video/Show/Show.S01E02.mkv", "Show.S01E02.mkv", 2640,
    [
        FakeTrack.Video(0),
        FakeTrack.Audio(1, "English", "Original / AC3 5.1 @ 448 kbps"),
        FakeTrack.Audio(2, "English", "Commentary / AC3 2.0 @ 192 kbps"),
        FakeTrack.Audio(3, "Russian", "Dub / AC3 5.1 @ 448 kbps"),
        FakeTrack.Subtitle(4, "Russian", "Full / SRT"),
        FakeTrack.Subtitle(5, "English", "Full / SRT"),
    ]);

    private Scenario NewScenario() => new(output);

    [Fact]
    public async Task Vlc_appears_starting_then_running_with_the_item_and_playback()
    {
        await using var s = NewScenario();
        Assert.Equal(VlcStatusNames.NotRunning, (await s.PollAsync()).Vlc.Status);

        // A movie is double-clicked: vlc.exe runs, its web interface is not listening yet, then answers empty.
        var pid = s.Vlc.StartProcess(Scenario.Password, null, Movie);
        s.Vlc.Http = FakeHttp.Refused;
        var starting = await s.PollAsync();
        Assert.Equal(VlcStatusNames.Starting, starting.Vlc.Status);
        Assert.False(starting.Vlc.Running);
        s.Vlc.Http = FakeHttp.Starting;
        Assert.Equal(VlcStatusNames.Starting, (await s.PollAsync()).Vlc.Status);
        Assert.Empty(s.EventNames);

        s.Vlc.Http = FakeHttp.Up;
        s.Elapse(12);
        var running = await s.PollAsync();

        Assert.Equal(VlcStatusNames.Running, running.Vlc.Status);
        Assert.True(running.Vlc.Running);
        Assert.Equal("3.0.23", running.Vlc.Version);
        Assert.Equal(3, running.Vlc.ApiVersion);
        Assert.Equal(Movie.Uri, running.Item!.Uri);
        Assert.Equal("Arrival", running.Item.Title);
        Assert.Equal("playing", running.Playback.State);
        Assert.Equal(12, running.Playback.Time);
        Assert.Equal(6960, running.Playback.Length);
        Assert.Equal(256, running.Volume.Value);
        Assert.Equal([1, 2], running.Tracks!.Audio.Select(t => t.Id));
        Assert.Equal(["English", "Russian"], running.Tracks.Audio.Select(t => t.Lang));
        Assert.Equal([3, 4], running.Tracks.Subtitle.Select(t => t.Id));
        Assert.Equal(1, running.Tracks.CurrentAudio);
        Assert.Equal(-1, running.Tracks.CurrentSubtitle);

        Assert.Equal([EventNames.VlcStarted], s.EventNames);
        var appeared = Assert.Single(s.Appeared);
        Assert.Equal(pid, appeared.Pid);
        Assert.False(appeared.Owned);
        Assert.Equal([VlcStatusNames.NotRunning, VlcStatusNames.Starting, VlcStatusNames.Running],
            s.States.Select(x => x.Vlc.Status).Distinct());
        Assert.Equal(0, s.ItemEnded);
    }

    [Fact]
    public async Task Playing_to_the_end_raises_endReached_and_ItemEnded_once()
    {
        await using var s = NewScenario();
        s.Vlc.StartProcess(Scenario.Password, null, Movie);
        await s.PollAsync();
        await s.RcSyncAsync();
        s.Elapse(6958);
        Assert.Equal(6958, (await s.PollAsync()).Playback.Time);
        s.ClearEvents();

        // The movie ends and VLC stops: status.json is "stopped" with currentplid -1, as real VLC reports it.
        s.Elapse(5);
        var stopped = await s.PollAsync();

        Assert.Equal("stopped", stopped.Playback.State);
        Assert.Null(stopped.Item);
        Assert.Equal(VlcStatusNames.Running, stopped.Vlc.Status);
        Assert.Equal([EventNames.EndReached], s.EventNames);
        Assert.Equal(1, s.ItemEnded);
        Assert.True(s.Store.Find(Movie.Uri)!.Finished);

        await s.PollAsync();
        Assert.Equal(1, s.ItemEnded);
        Assert.Equal([EventNames.EndReached], s.EventNames);
    }

    [Fact]
    public async Task A_file_that_ends_early_is_reported_ended_by_vlcs_end_state()
    {
        await using var s = NewScenario();
        // A cut download: VLC reports 90 minutes, but playback stops after 2000 s.
        var cut = FakeMedia.Movie("file:///D:/Video/Movies/Cut.mkv", "Cut", 5400) with { EndsAt = 2000 };
        s.Vlc.StartProcess(Scenario.Password, null, cut);
        await s.PollAsync();
        await s.RcSyncAsync();
        s.Elapse(1990);
        await s.PollAsync();
        s.ClearEvents();

        s.Elapse(15);
        await s.RcSyncAsync(); // the companion has read "status change: ( play state: 4 ): End"
        var stopped = await s.PollAsync();

        Assert.Equal("stopped", stopped.Playback.State);
        Assert.Equal([EventNames.EndReached], s.EventNames);
        Assert.Equal(1, s.ItemEnded);
        Assert.True(s.Store.Find(cut.Uri)!.Finished);
    }

    [Fact]
    public async Task Stopping_in_the_middle_is_not_the_end_of_the_item()
    {
        await using var s = NewScenario();
        s.Vlc.StartProcess(Scenario.Password, null, Movie);
        await s.PollAsync();
        await s.RcSyncAsync();
        s.Elapse(1500);
        await s.PollAsync();
        s.ClearEvents();

        await s.CmdAsync("stop");
        await s.RcSyncAsync();
        var stopped = await s.PollAsync();

        Assert.Equal("stopped", stopped.Playback.State);
        Assert.Empty(s.EventNames);
        Assert.Equal(0, s.ItemEnded);
        var entry = s.Store.Find(Movie.Uri)!;
        Assert.False(entry.Finished);
        Assert.Equal(1500, entry.Position);
    }

    [Fact]
    public async Task Stop_pressed_in_vlc_itself_is_not_the_end_either()
    {
        await using var s = NewScenario();
        s.Vlc.StartProcess(Scenario.Password, null, Movie);
        await s.PollAsync();
        await s.RcSyncAsync();
        s.Elapse(1500);
        await s.PollAsync();
        s.ClearEvents();

        s.Vlc.PressStop();
        await s.RcSyncAsync(); // the companion has read "status change: ( stop state: 0 )"
        var stopped = await s.PollAsync();

        Assert.Equal("stopped", stopped.Playback.State);
        Assert.Empty(s.EventNames);
        Assert.Equal(0, s.ItemEnded);
        var entry = s.Store.Find(Movie.Uri)!;
        Assert.False(entry.Finished);
        Assert.Equal(1500, entry.Position);
    }

    [Fact]
    public async Task The_playlist_going_on_to_the_next_episode_raises_inputChanged_and_ItemEnded_once()
    {
        await using var s = NewScenario();
        s.Vlc.StartProcess(Scenario.Password, null, Episode1, Episode2);
        await s.PollAsync();
        await s.RcSyncAsync();
        s.Elapse(2698);
        await s.PollAsync();
        s.ClearEvents();

        s.Elapse(5);
        var next = await s.PollAsync();

        Assert.Equal(Episode2.Uri, next.Item!.Uri);
        Assert.Equal("playing", next.Playback.State);
        Assert.Equal(0, next.Playback.Time);
        Assert.Equal(2640, next.Playback.Length);
        var changed = Assert.Single(s.Events);
        Assert.Equal(EventNames.InputChanged, changed.Name);
        Assert.Equal(Episode2.Uri, changed.Uri);
        Assert.Equal(1, s.ItemEnded);

        s.Elapse(1);
        await s.PollAsync();
        Assert.Equal(1, s.ItemEnded);
        Assert.Single(s.Events);
        Assert.True(s.Store.Find(Episode1.Uri)!.Finished);
        Assert.Equal(1, s.Store.Find(Episode2.Uri)!.PlayCount);
    }

    [Fact]
    public async Task Vlc_closing_while_playing_keeps_the_position_and_the_next_open_offers_to_resume()
    {
        await using var s = NewScenario();
        s.Vlc.StartProcess(Scenario.Password, null, Movie);
        await s.PollAsync();
        s.Elapse(1800);
        await s.PollAsync();
        s.Elapse(25);
        await s.PollAsync();
        s.ClearEvents();

        s.Vlc.Exit(); // the window is closed
        var closed = await s.PollAsync();

        Assert.Equal(VlcStatusNames.NotRunning, closed.Vlc.Status);
        Assert.False(closed.Vlc.Running);
        Assert.Equal("stopped", closed.Playback.State);
        Assert.Null(closed.Item);
        Assert.Null(closed.Tracks);
        Assert.Equal([EventNames.VlcExited], s.EventNames);
        Assert.Equal(0, s.ItemEnded);
        var entry = s.Store.Find(Movie.Uri)!;
        Assert.Equal(1825, entry.Position);
        Assert.False(entry.Finished);

        // The next evening the same movie is opened again.
        s.ClearEvents();
        s.Vlc.ClearLog();
        s.Clock.Advance(TimeSpan.FromHours(20));
        s.Vlc.StartProcess(Scenario.Password, null, Movie);
        var reopened = await s.PollAsync();

        Assert.Equal(EventNames.VlcStarted, s.EventNames[0]);
        var prompt = Assert.Single(s.Events, e => e.Name == EventNames.ResumePrompt);
        Assert.Equal(1825, prompt.Position);
        Assert.Equal(1825, reopened.Resume!.Position);
        Assert.Equal(["pl_forcepause"], s.Vlc.Commands);
        Assert.Equal("paused", s.Vlc.State);
    }

    [Fact]
    public async Task Vlc_without_the_companions_password_is_noControl_and_reported_once_per_process()
    {
        await using var s = NewScenario();
        // Opened from the Start menu an hour ago, with the user's own web interface password.
        var first = s.Vlc.StartProcess("users-own-password", DateTime.Now.AddHours(-1), Movie);

        var state = await s.PollAsync();
        Assert.Equal(VlcStatusNames.NoControl, state.Vlc.Status);
        Assert.False(state.Vlc.Running);
        Assert.Equal(VlcRunState.RunningNoApi, s.Manager.RunState);
        Assert.Equal([first], s.NoControl);

        await s.PollAsync();
        await s.PollAsync();
        Assert.Equal([first], s.NoControl);
        Assert.Empty(s.EventNames);
        var refused = await Assert.ThrowsAsync<ProtocolException>(() => s.CmdAsync("pause"));
        Assert.Equal(ErrorCodes.VlcNotRunning, refused.Code);
        Assert.Empty(s.Vlc.Commands);

        // Closed and opened again, now with no web password at all (403): told again for the new process.
        s.Vlc.Exit();
        Assert.Equal(VlcStatusNames.NotRunning, (await s.PollAsync()).Vlc.Status);
        var second = s.Vlc.StartProcess(null, DateTime.Now.AddMinutes(-5), Movie);
        Assert.Equal(VlcStatusNames.NoControl, (await s.PollAsync()).Vlc.Status);
        Assert.Equal([first, second], s.NoControl);
    }

    [Theory]
    [InlineData("resume", 2400)]
    [InlineData("restart", 0)]
    public async Task A_half_watched_file_waits_paused_for_the_answer_then_seeks_and_plays(string choice, long from)
    {
        await using var s = NewScenario();
        s.Store.BeginPlayback(Movie.Uri, "Arrival", 6960, null);
        s.Store.UpdatePosition(Movie.Uri, 2400, 6960, allowUnfinish: false, notify: true);
        s.Vlc.StartProcess(Scenario.Password, null, Movie);

        await s.PollAsync();
        var prompt = Assert.Single(s.Events, e => e.Name == EventNames.ResumePrompt);
        Assert.Equal(2400, prompt.Position);
        Assert.Equal(6960, prompt.Length);
        Assert.True(prompt.Pending);
        Assert.Equal(["pl_forcepause"], s.Vlc.Commands);
        Assert.True(s.Manager.PlaybackHeld);

        var held = await s.PollAsync();
        Assert.Equal("paused", held.Playback.State);
        Assert.Equal(2400, held.Resume!.Position);
        s.Elapse(600); // nobody answers for ten minutes: VLC stays paused, nothing is sent again
        await s.PollAsync();
        Assert.Equal(["pl_forcepause"], s.Vlc.Commands);

        await s.CmdAsync("resume", new { choice });

        Assert.Equal(["pl_forcepause", $"seek val={from}", "pl_forceresume"], s.Vlc.Commands);
        var playing = await s.PollAsync();
        Assert.Equal("playing", playing.Playback.State);
        Assert.Equal(from, playing.Playback.Time);
        Assert.Null(playing.Resume);
        Assert.False(s.Manager.PlaybackHeld);
    }

    [Fact]
    public async Task Tracks_chosen_for_an_episode_are_applied_to_the_next_one()
    {
        await using var s = NewScenario();
        s.Files.AddFolder(@"D:\Video\Show", "Show.S01E01.mkv", "Show.S01E02.mkv");
        s.Vlc.StartProcess(Scenario.Password, null, Episode1, Episode2);
        var first = await s.PollAsync();
        Assert.Equal(1, first.Tracks!.CurrentAudio);
        Assert.Equal(-1, first.Tracks.CurrentSubtitle);

        // On the phone: the Russian dub with English subtitles.
        await s.CmdAsync("track", new { kind = "audio", id = 2 });
        await s.CmdAsync("track", new { kind = "subtitle", id = 3 });
        Assert.Equal(["audio_track val=2", "subtitle_track val=3"], s.Vlc.Commands);
        var chosen = await s.PollAsync();
        Assert.Equal(2, chosen.Tracks!.CurrentAudio);
        Assert.Equal(3, chosen.Tracks.CurrentSubtitle);

        s.Elapse(2698);
        await s.PollAsync();
        Assert.Equal(MediaUri.Normalize(Episode2.Uri), Assert.Single(s.Events, e => e.Name == EventNames.NextEpisode).Uri);
        s.Vlc.ClearLog();

        // VLC goes on to the next episode, whose tracks are in another order: the same choices by language and name.
        s.Elapse(5);
        await s.PollAsync();
        Assert.Equal(["audio_track val=3", "subtitle_track val=5"], s.Vlc.Commands);
        var second = await s.PollAsync();
        Assert.Equal(Episode2.Uri, second.Item!.Uri);
        Assert.Equal(3, second.Tracks!.CurrentAudio);
        Assert.Equal(5, second.Tracks.CurrentSubtitle);
        Assert.Null(second.Next);
    }

    [Fact]
    public async Task Without_the_tracks_page_the_current_tracks_come_from_rc()
    {
        await using var s = NewScenario();
        s.Vlc.LuaPages = false; // tracks.json is a 404: our Lua pages are not in VLC
        s.Vlc.StartProcess(Scenario.Password, null, Movie with { DefaultSubtitle = 4 });

        var first = await s.PollAsync();

        Assert.False(s.Manager.TracksPageAvailable);
        Assert.Equal([1, 2], first.Tracks!.Audio.Select(t => t.Id)); // the list itself is from status.json
        Assert.Equal(1, first.Tracks.CurrentAudio);
        Assert.Equal(4, first.Tracks.CurrentSubtitle);
        Assert.Equal(0, first.Tracks.CurrentVideo);
        Assert.Contains("atrack", s.Vlc.RcCommands);
        Assert.Contains("strack", s.Vlc.RcCommands);
        Assert.Contains("vtrack", s.Vlc.RcCommands);

        await s.CmdAsync("track", new { kind = "audio", id = 2 });
        await s.CmdAsync("track", new { kind = "subtitle", id = -1 });
        var after = await s.PollAsync();
        Assert.Equal(2, after.Tracks!.CurrentAudio);
        Assert.Equal(-1, after.Tracks.CurrentSubtitle);
    }

    [Fact]
    public async Task Phone_commands_reach_vlc_and_the_next_state_shows_them()
    {
        await using var s = NewScenario();
        s.Vlc.StartProcess(Scenario.Password, null, Episode1, Episode2);
        await s.PollAsync();
        s.ClearEvents();

        await s.CmdAsync("pause");
        Assert.Equal("paused", (await s.PollAsync()).Playback.State);
        await s.CmdAsync("play");
        Assert.Equal("playing", (await s.PollAsync()).Playback.State);
        await s.CmdAsync("togglePause");
        Assert.Equal("paused", (await s.PollAsync()).Playback.State);
        await s.CmdAsync("togglePause");

        await s.CmdAsync("seek", new { seconds = 600 });
        Assert.Equal(600, (await s.PollAsync()).Playback.Time);
        await s.CmdAsync("seek", new { delta = 30 });
        await s.CmdAsync("seek", new { delta = -10 });
        Assert.Equal(620, (await s.PollAsync()).Playback.Time);

        await s.CmdAsync("volume", new { value = 300 });
        Assert.Equal(300, (await s.PollAsync()).Volume.Value);
        await s.CmdAsync("volume", new { delta = -44 });
        Assert.Equal(256, (await s.PollAsync()).Volume.Value);
        await s.CmdAsync("mute", new { on = true });
        var muted = await s.PollAsync();
        Assert.Equal(0, muted.Volume.Value);
        Assert.True(muted.Volume.Muted);
        await s.CmdAsync("mute", new { on = false });
        var unmuted = await s.PollAsync();
        Assert.Equal(256, unmuted.Volume.Value);
        Assert.False(unmuted.Volume.Muted);

        await s.CmdAsync("fullscreen", new { on = true });
        await s.CmdAsync("fullscreen", new { on = true }); // already fullscreen: not toggled back
        Assert.True((await s.PollAsync()).Video.Fullscreen);

        await s.CmdAsync("next");
        var next = await s.PollAsync();
        Assert.Equal(Episode2.Uri, next.Item!.Uri);
        Assert.Equal([1, 2, 3], next.Tracks!.Audio.Select(t => t.Id));
        Assert.Equal(0, s.ItemEnded); // skipped, not finished

        await s.CmdAsync("stop");
        Assert.Equal("stopped", (await s.PollAsync()).Playback.State);
        await s.CmdAsync("play");
        var again = await s.PollAsync();
        Assert.Equal("playing", again.Playback.State);
        Assert.Equal(Episode2.Uri, again.Item!.Uri);

        Assert.Equal(
        [
            "pl_forcepause", "pl_forceresume", "pl_pause", "pl_pause",
            "seek val=600", "seek val=+30", "seek val=-10",
            "volume val=300", "volume val=-44", "volume val=0", "volume val=256",
            "fullscreen",
            "pl_next", "pl_stop", "pl_play",
        ], s.Vlc.Commands);
        Assert.DoesNotContain(EventNames.EndReached, s.EventNames);
    }

    [Fact]
    public async Task Vlc_closing_during_a_seek_fails_the_command_and_ends_notRunning()
    {
        await using var s = NewScenario();
        s.Vlc.StartProcess(Scenario.Password, null, Movie);
        await s.PollAsync();
        s.Elapse(1200);
        await s.PollAsync();
        s.ClearEvents();

        s.Vlc.CrashOn = r => r.Command == "seek";
        // The phone gets err vlc_not_running for it (Session maps VlcUnavailableException).
        await Assert.ThrowsAsync<VlcUnavailableException>(() => s.CmdAsync("seek", new { delta = 30 }));
        var after = await s.PollAsync();

        Assert.Equal(VlcStatusNames.NotRunning, after.Vlc.Status);
        Assert.Equal(VlcRunState.NotRunning, s.Manager.RunState);
        Assert.Equal([EventNames.VlcExited], s.EventNames);
        Assert.Equal(1200, s.Store.Find(Movie.Uri)!.Position);
        await Assert.ThrowsAsync<ProtocolException>(() => s.CmdAsync("seek", new { delta = 30 }));
    }

    /// <summary>
    /// VLC dies at one of the requests of a poll in which the next episode starts (status, playlist, tracks, the pause
    /// for the resume question): the poll does not throw, the next one finds VLC gone.
    /// </summary>
    [Theory]
    [InlineData("status.json", null)]
    [InlineData("playlist.json", null)]
    [InlineData("tracks.json", null)]
    [InlineData("status.json", "pl_forcepause")]
    public async Task Vlc_dying_in_the_middle_of_a_poll_does_not_break_the_manager(string page, string? command)
    {
        await using var s = NewScenario();
        s.Store.BeginPlayback(Episode2.Uri, null, 2640, null);
        s.Store.UpdatePosition(Episode2.Uri, 900, 2640, allowUnfinish: false, notify: true);
        s.Vlc.StartProcess(Scenario.Password, null, Episode1, Episode2);
        await s.PollAsync();
        s.Elapse(2698);
        await s.PollAsync();
        s.ClearEvents();

        s.Vlc.CrashOn = r => r.Page == page && r.Command == command;
        s.Elapse(5); // Episode 2 starts
        await s.PollAsync();
        Assert.Null(s.Vlc.Pid);
        var after = await s.PollAsync();

        Assert.Equal(VlcStatusNames.NotRunning, after.Vlc.Status);
        Assert.Null(after.Item);
        Assert.Null(after.Resume);
        Assert.Single(s.Events, e => e.Name == EventNames.VlcExited);
        Assert.Equal(900, s.Store.Find(Episode2.Uri)!.Position);
        Assert.True(s.Store.Find(Episode1.Uri)!.Finished);
    }

    [Fact]
    public async Task Quit_closes_vlc_over_rc()
    {
        await using var s = NewScenario();
        s.Vlc.StartProcess(Scenario.Password, null, Movie);
        await s.PollAsync();
        s.Elapse(300);
        await s.PollAsync();
        s.ClearEvents();

        await s.CmdAsync("quit");

        Assert.Equal(["quit"], s.Vlc.RcCommands);
        Assert.Null(s.Vlc.Pid);
        var after = await s.PollAsync();
        Assert.Equal(VlcStatusNames.NotRunning, after.Vlc.Status);
        Assert.Equal([EventNames.VlcExited], s.EventNames);
        Assert.Equal(300, s.Store.Find(Movie.Uri)!.Position);
    }

    [Fact]
    public async Task Open_while_vlc_is_closed_launches_it_with_the_companions_interfaces_and_the_file()
    {
        await using var s = NewScenario();
        s.Vlc.AddFile(Movie);
        s.Manager.Start(); // the poll loop, as in the companion: the launch waits for it to see the HTTP interface

        await s.CmdAsync("open", new { uri = Movie.Uri });

        Assert.Equal(VlcRunState.Running, s.Manager.RunState);
        var args = s.Vlc.LaunchArgs!.ToList();
        Assert.Equal("http:rc", args[args.IndexOf("--extraintf") + 1]);
        Assert.Equal(Scenario.Password, args[args.IndexOf("--http-password") + 1]);
        Assert.Equal("127.0.0.1:4212", args[args.IndexOf("--rc-host") + 1]);
        Assert.Equal(Movie.Uri, args[^1]);
        var state = await s.PollAsync();
        Assert.Equal(Movie.Uri, state.Item!.Uri);
        Assert.Equal("playing", state.Playback.State);
        Assert.True(Assert.Single(s.Appeared).Owned);
        Assert.Contains(EventNames.VlcStarted, s.EventNames);
    }
}
