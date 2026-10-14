using Microsoft.Extensions.Time.Testing;
using Sofabird.Core.History;
using Sofabird.Core.Protocol;
using Xunit;

namespace Sofabird.Core.Tests;

public sealed class HistoryTrackerTests : IDisposable
{
    private const string Movie = "file:///D:/shared/The%20Movie.mkv";
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 13, 20, 0, 0, TimeSpan.Zero));
    private readonly FakeHistoryFileSystem _fs = new();
    private readonly HistoryStore _store;
    private readonly HistoryTracker _tracker;

    public HistoryTrackerTests()
    {
        _store = new HistoryStore(@"C:\data\history.json", _clock, _fs);
        _tracker = new HistoryTracker(_store);
    }

    public void Dispose() => _store.Dispose();

    private static PlaybackObservation Obs(string? uri, string state, long time, long length = 6000, TracksInfo? tracks = null, bool end = false) =>
        new(uri, "The Movie", state, time, length, tracks, end);

    private IReadOnlyList<HistoryAction> Step(PlaybackObservation obs, double seconds = 0.5)
    {
        _clock.Advance(TimeSpan.FromSeconds(seconds));
        return _tracker.Observe(obs);
    }

    private void Watched(string uri, long position, long length = 6000)
    {
        _store.BeginPlayback(uri, null, length, null);
        _store.UpdatePosition(uri, position, length, false, true);
    }

    [Fact]
    public void Resumable_file_is_paused_until_the_phone_answers_resume()
    {
        Watched(Movie, 1200);

        var actions = _tracker.Observe(Obs("file:///D:/shared/The Movie.mkv", "playing", 0));
        Assert.Equal(HistoryActionKind.Pause, actions[0].Kind);
        var ev = Assert.Single(actions, a => a.Kind == HistoryActionKind.Event).Event!;
        Assert.Equal(EventNames.ResumePrompt, ev.Name);
        Assert.Equal(1200, ev.Position);
        Assert.Equal(6000, ev.Length);
        Assert.True(ev.Pending);
        Assert.Equal("The Movie", ev.Title);
        Assert.True(_tracker.IsHolding);
        Assert.Equal(1200, _tracker.Resume!.Position);

        // VLC is still opening and ignored the pause: it is repeated, but not more than once per second.
        Assert.Empty(Step(Obs(Movie, "playing", 0)));
        Assert.Equal([HistoryAction.Pause], Step(Obs(Movie, "playing", 1), 0.6));
        Assert.Empty(Step(Obs(Movie, "paused", 1)));
        // Waiting paused indefinitely; the resume point is not touched.
        Assert.Empty(Step(Obs(Movie, "paused", 1), 3600));
        Assert.Equal(1200, _store.Find(Movie)!.Position);
        Assert.NotNull(_tracker.Resume);

        Assert.Equal([HistoryAction.SeekTo(1200), HistoryAction.Play], _tracker.Answer("resume"));
        Assert.Null(_tracker.Resume);
        Assert.False(_tracker.IsHolding);
        Assert.Empty(_tracker.Answer("restart")); // the first answer wins

        // A late poll still sees the pause: not a pause by the user, nothing saved.
        Step(Obs(Movie, "paused", 1));
        Assert.Equal(1200, _store.Find(Movie)!.Position);
        Step(Obs(Movie, "playing", 1201));
        Step(Obs(Movie, "playing", 1211), 10);
        Assert.Equal(1211, _store.Find(Movie)!.Position);
        Assert.Equal(2, _store.Find(Movie)!.PlayCount);
    }

    [Fact]
    public void Restart_plays_from_the_beginning()
    {
        Watched(Movie, 1200);
        _tracker.Observe(Obs(Movie, "playing", 0));
        Step(Obs(Movie, "paused", 0));
        Assert.Equal([HistoryAction.SeekTo(0), HistoryAction.Play], _tracker.Answer("restart"));
        Assert.Throws<ArgumentException>(() => _tracker.Answer("maybe"));
    }

    [Fact]
    public void Play_pressed_in_vlc_clears_the_question_and_playback_goes_on_from_the_start()
    {
        Watched(Movie, 1200);
        _tracker.Observe(Obs(Movie, "playing", 0));
        Step(Obs(Movie, "paused", 0));
        Assert.Empty(Step(Obs(Movie, "playing", 1)));
        Assert.Null(_tracker.Resume);
        Assert.False(_tracker.IsHolding);

        // A few seconds (then pause, stop) do not replace the resume point…
        Step(Obs(Movie, "playing", 12), 10);
        Step(Obs(Movie, "paused", 13), 1);
        Assert.Equal(1200, _store.Find(Movie)!.Position);
        _tracker.OnVlcExited();
        Assert.Equal(1200, _store.Find(Movie)!.Position);

        // …watching past the 5-minute mark does.
        _tracker.Observe(Obs(Movie, "playing", 0));
        Step(Obs(Movie, "paused", 0));
        Step(Obs(Movie, "playing", 1));
        Step(Obs(Movie, "playing", 290), 290);
        Assert.Equal(1200, _store.Find(Movie)!.Position);
        Step(Obs(Movie, "playing", 305), 15);
        Assert.Equal(305, _store.Find(Movie)!.Position);
    }

    [Fact]
    public void Vlc_closed_while_waiting_keeps_the_resume_point()
    {
        Watched(Movie, 1200);
        _tracker.Observe(Obs(Movie, "playing", 0));
        Step(Obs(Movie, "paused", 0));
        _tracker.OnVlcExited();
        Assert.Null(_tracker.Resume);
        Assert.Equal(1200, _store.Find(Movie)!.Position);

        // Another file opened while waiting also keeps it.
        _tracker.Observe(Obs(Movie, "playing", 0));
        Assert.NotNull(_tracker.Resume);
        Step(Obs("file:///D:/other.mkv", "playing", 0));
        Assert.Null(_tracker.Resume);
        Assert.Equal(1200, _store.Find(Movie)!.Position);
    }

    [Theory]
    [InlineData(299, false, 0)] // watched less than 5 minutes
    [InlineData(1200, true, 0)] // finished
    [InlineData(1200, false, 2500)] // already further: the companion restarted during the movie
    public void No_question(long position, bool finished, long time)
    {
        Watched(Movie, position);
        if (finished) _store.SetFinished(Movie, true);
        var actions = _tracker.Observe(Obs(Movie, "playing", time));
        Assert.DoesNotContain(actions, a => a.Kind is HistoryActionKind.Pause or HistoryActionKind.Event);
        Assert.Null(_tracker.Resume);
    }

    [Fact]
    public void Position_is_saved_every_10_s_and_on_pause_stop_and_input_change()
    {
        _tracker.Observe(Obs(Movie, "playing", 0));
        var e = _store.Find(Movie)!;
        Assert.Equal(1, e.PlayCount);
        Assert.Equal("The Movie", e.Title);

        Step(Obs(Movie, "playing", 5), 5);
        Assert.Equal(0, _store.Find(Movie)!.Position);
        Step(Obs(Movie, "playing", 10), 5);
        Assert.Equal(10, _store.Find(Movie)!.Position);
        var rev = _store.Revision;
        Step(Obs(Movie, "playing", 15), 5);
        Step(Obs(Movie, "paused", 16), 1);
        Assert.Equal(16, _store.Find(Movie)!.Position);
        Assert.True(_store.Revision > rev); // pause is visible to the phones
        Step(Obs(Movie, "playing", 17), 1);
        Step(Obs(Movie, "playing", 250), 2); // seek
        Step(Obs(null, "stopped", 0), 1);
        Assert.Equal(250, _store.Find(Movie)!.Position); // under 5 minutes: no question next time

        _tracker.Observe(Obs(Movie, "playing", 0));
        Assert.Equal(2, _store.Find(Movie)!.PlayCount);
        Step(Obs(Movie, "playing", 500), 2);
        Step(Obs("file:///D:/next.mkv", "playing", 0), 1);
        Assert.Equal(500, _store.Find(Movie)!.Position);
        Step(Obs("file:///D:/next.mkv", "playing", 30), 2);
        _tracker.OnVlcExited();
        Assert.Equal(30, _store.Find("file:///D:/next.mkv")!.Position);

        // Streams are not recorded.
        _tracker.Observe(Obs("http://example.com/live.ts", "playing", 0));
        Assert.Equal(2, _store.Count);
    }

    [Fact]
    public void Finished_episode_offers_the_next_one_until_another_input_starts()
    {
        _fs.Folders[@"D:\Show"] = ["E1.mkv", "E2.mkv", "E3.mkv", "E1.srt"];
        _tracker.Observe(Obs("file:///D:/Show/E1.mkv", "playing", 1400, 1500));
        Assert.Null(_tracker.Next);
        var actions = Step(Obs("file:///D:/Show/E1.mkv", "playing", 1430, 1500), 10); // 70 s left < 75 s
        var ev = Assert.Single(actions).Event!;
        Assert.Equal(EventNames.NextEpisode, ev.Name);
        Assert.Equal("file:///d:/Show/E2.mkv", ev.Uri);
        Assert.Equal("E2", ev.Title);
        Assert.Equal("file:///d:/Show/E2.mkv", _tracker.Next!.Uri);
        Assert.True(_store.Find("file:///D:/Show/E1.mkv")!.Finished);
        Assert.Empty(Step(Obs("file:///D:/Show/E1.mkv", "playing", 1440, 1500), 10)); // offered once

        _tracker.DismissNext();
        Assert.Null(_tracker.Next);

        // The end of the next file (VLC stops): offered at the end, even below the 95% mark.
        Step(Obs("file:///D:/Show/E2.mkv", "playing", 0, 1500));
        Step(Obs("file:///D:/Show/E2.mkv", "playing", 1300, 1500), 2);
        actions = Step(Obs(null, "stopped", 0, 0, end: true));
        Assert.Equal("file:///d:/Show/E3.mkv", Assert.Single(actions).Event!.Uri);
        Assert.True(_store.Find("file:///D:/Show/E2.mkv")!.Finished);
        Assert.NotNull(_tracker.Next);

        // Another input starts: the offer is gone.
        Step(Obs("file:///D:/Show/E3.mkv", "playing", 0, 1500));
        Assert.Null(_tracker.Next);
    }

    [Fact]
    public void Vlc_moving_to_the_next_playlist_item_does_not_offer_it()
    {
        _fs.Folders[@"D:\Show"] = ["E1.mkv", "E2.mkv"];
        _tracker.Observe(Obs("file:///D:/Show/E1.mkv", "playing", 1490, 1500));
        var actions = Step(Obs("file:///D:/Show/E2.mkv", "playing", 0, 1500), 1);
        Assert.DoesNotContain(actions, a => a.Kind == HistoryActionKind.Event);
        Assert.True(_store.Find("file:///D:/Show/E1.mkv")!.Finished);
        Assert.Null(_tracker.Next);
    }

    private static TracksInfo Tracks(int audio, int subtitle) => new()
    {
        Audio =
        [
            new() { Id = 1, Name = "Original", Lang = "English", Codec = "a52" },
            new() { Id = 2, Name = "Dub", Lang = "Russian", Codec = "a52" },
        ],
        Subtitle = [new() { Id = 5, Name = "Full", Lang = "Russian", Codec = "subt" }],
        CurrentAudio = audio,
        CurrentSubtitle = subtitle,
    };

    [Fact]
    public void Folder_track_choices_are_applied_to_new_files_even_while_waiting_for_the_answer()
    {
        _store.SetAudioPreference("file:///D:/Show/", new TrackPreference { Lang = "Russian", Name = "Dub", Codec = "a52" });
        _store.SetSubtitlePreference("file:///D:/Show/", SubtitleChoice.Disabled);
        Watched("file:///D:/Show/E2.mkv", 600, 1500);

        // Streams are not known in the first poll yet.
        var first = _tracker.Observe(Obs("file:///D:/Show/E2.mkv", "playing", 0, 1500));
        Assert.Equal(HistoryActionKind.Pause, first[0].Kind);
        Assert.DoesNotContain(first, a => a.Kind == HistoryActionKind.SelectTrack);
        var actions = Step(Obs("file:///D:/Show/E2.mkv", "paused", 0, 1500, Tracks(1, 5)));
        Assert.Equal([HistoryAction.Select("audio", 2), HistoryAction.Select("subtitle", -1)], actions);
        Assert.True(_tracker.IsHolding);
        // Applied once; VLC reporting our choice later is not recorded as the user's.
        Assert.Empty(Step(Obs("file:///D:/Show/E2.mkv", "paused", 0, 1500, Tracks(1, 5)), 2));
        Assert.Empty(Step(Obs("file:///D:/Show/E2.mkv", "paused", 0, 1500, Tracks(2, -1)), 5));
        Assert.Equal("Dub", _store.GetFolderPreferences("file:///D:/Show/")!.Audio!.Name);
    }

    [Fact]
    public void Explicit_tracks_of_open_win_over_folder_choices()
    {
        _store.SetAudioPreference("file:///D:/Show/", new TrackPreference { Lang = "Russian" });
        _tracker.ExpectExplicitTracks("file:///D:/Show/E3.mkv");
        Assert.Empty(_tracker.Observe(Obs("file:///D:/Show/E3.mkv", "playing", 0, 1500, Tracks(1, -1))));
    }

    [Fact]
    public void Track_changes_made_in_vlc_or_on_the_phone_are_remembered_for_the_folder()
    {
        _tracker.Observe(Obs("file:///D:/Show/E1.mkv", "playing", 0, 1500, Tracks(1, -1)));
        // Right after the start, changes are VLC's own (default track, subtitle autodetect).
        Step(Obs("file:///D:/Show/E1.mkv", "playing", 1, 1500, Tracks(2, -1)), 1);
        Assert.Null(_store.GetFolderPreferences("file:///D:/Show/"));

        // Later: the user switched the audio in VLC.
        Step(Obs("file:///D:/Show/E1.mkv", "playing", 10, 1500, Tracks(1, -1)), 5);
        var prefs = _store.GetFolderPreferences("file:///D:/Show/")!;
        Assert.Equal("English", prefs.Audio!.Lang);
        Assert.Equal("Original", prefs.Audio.Name);
        Assert.Equal("a52", prefs.Audio.Codec);
        Assert.Null(prefs.Subtitle);

        // The phone turns the subtitles on, then off.
        _tracker.OnTrackChosen("subtitle", 5, Tracks(1, -1));
        Assert.Equal("Full", _store.GetFolderPreferences("file:///D:/Show/")!.Subtitle!.Track!.Name);
        Step(Obs("file:///D:/Show/E1.mkv", "playing", 11, 1500, Tracks(1, 5)), 1); // VLC confirms: not a second record
        _tracker.OnTrackChosen("subtitle", -1, Tracks(1, 5));
        Assert.True(_store.GetFolderPreferences("file:///D:/Show/")!.Subtitle!.Off);

        // The next file of the folder gets them.
        var actions = Step(Obs("file:///D:/Show/E2.mkv", "playing", 0, 1500, Tracks(2, 5)), 5);
        Assert.Equal([HistoryAction.Select("audio", 1), HistoryAction.Select("subtitle", -1)], actions);
    }
}
