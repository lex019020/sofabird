using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VlcRemote.Core.Protocol;

namespace VlcRemote.Core.History;

/// <summary>What VLC is doing right now, as seen by one poll.</summary>
/// <param name="Uri">URI of the current item (VLC's spelling); <c>null</c> — nothing.</param>
/// <param name="State"><c>playing</c> | <c>paused</c> | <c>stopped</c>.</param>
/// <param name="Time">Seconds.</param>
/// <param name="Length">Seconds, 0 = unknown.</param>
/// <param name="Tracks">Tracks of the input (with the current ids), if known.</param>
/// <param name="EndReached">Playback stopped because the file ended.</param>
public sealed record PlaybackObservation(string? Uri, string? Title, string State, long Time, long Length, TracksInfo? Tracks, bool EndReached = false)
{
    public bool IsActive => State is "playing" or "paused";
}

public enum HistoryActionKind
{
    /// <summary><c>pl_forcepause</c>.</summary>
    Pause,
    /// <summary><c>pl_forceresume</c>.</summary>
    Play,
    /// <summary><c>seek</c> to <see cref="HistoryAction.Seconds"/>.</summary>
    Seek,
    /// <summary>Select <see cref="HistoryAction.TrackId"/> of <see cref="HistoryAction.TrackKind"/> (<c>-1</c> = off).</summary>
    SelectTrack,
    /// <summary>Send <see cref="HistoryAction.Event"/> to the phones.</summary>
    Event,
}

/// <summary>Something the VLC manager has to do for the history (in order).</summary>
public sealed record HistoryAction(HistoryActionKind Kind, long Seconds = 0, string? TrackKind = null, int TrackId = 0, EventMessage? Event = null)
{
    public static readonly HistoryAction Pause = new(HistoryActionKind.Pause);
    public static readonly HistoryAction Play = new(HistoryActionKind.Play);
    public static HistoryAction SeekTo(long seconds) => new(HistoryActionKind.Seek, Seconds: seconds);
    public static HistoryAction Select(string kind, int id) => new(HistoryActionKind.SelectTrack, TrackKind: kind, TrackId: id);
    public static HistoryAction Send(EventMessage ev) => new(HistoryActionKind.Event, Event: ev);
}

/// <summary>
/// Turns VLC polls into watch history (protocol/README.md, "Watch history and resume"): creates the entry when a file
/// starts, saves the position every <see cref="SaveInterval"/> while playing and on pause/stop/input change/VLC exit,
/// holds a resumable file paused until a phone answers (<see cref="Answer"/>) or Play is pressed in VLC, offers the next
/// episode when a file is finished, and applies/records the per-folder audio and subtitle choices.
/// Decisions only: it returns <see cref="HistoryAction"/>s that the caller performs on VLC. Thread-safe.
/// </summary>
public sealed class HistoryTracker
{
    public static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(10);
    /// <summary>Track changes right after the start are VLC's own choices, not the user's.</summary>
    public static readonly TimeSpan TrackSettleTime = TimeSpan.FromSeconds(3);
    /// <summary>VLC may ignore a pause while the file is still opening: repeat it this often…</summary>
    public static readonly TimeSpan PauseRetryInterval = TimeSpan.FromSeconds(1);
    /// <summary>…at most this many times.</summary>
    public const int MaxPauseAttempts = 10;
    /// <summary>
    /// The resume question is asked only for a file that has just started. One that is already further (the companion
    /// was restarted during a movie, VLC resumed by itself, <c>--start-time</c>) is left alone.
    /// </summary>
    public const long FreshStartSeconds = 30;

    private enum Hold { None, PausePending, Held }

    private sealed class Input
    {
        public required string Uri { get; init; }
        public required string FolderUri { get; init; }
        public bool FinishedAtStart { get; init; }
        public bool ExplicitTracks { get; init; }
        public string? Title { get; set; }
        public long Length { get; set; }
        public long LastTime { get; set; }
        public bool Dirty { get; set; }
        public DateTimeOffset LastSavedAt { get; set; }
        public string State { get; set; } = "playing";
        public bool PrefsPending { get; set; } = true;
        public bool NextOffered { get; set; }
        /// <summary>Play was pressed without answering: the old resume point stays until this playback reaches the threshold.</summary>
        public bool KeepResumePoint { get; set; }
        public int? AppliedAudio { get; set; }
        public int? AppliedSubtitle { get; set; }
        public int? BaseAudio { get; set; }
        public int? BaseSubtitle { get; set; }
        public string? BaseSignature { get; set; }
        public DateTimeOffset BaseAt { get; set; }
    }

    private readonly object _gate = new();
    private readonly HistoryStore _store;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;
    private Input? _input;
    private ResumeInfo? _resume;
    private Hold _hold;
    private DateTimeOffset _pauseSentAt;
    private int _pauseAttempts;
    private NextInfo? _next;
    private string? _explicitTracksUri;

    public HistoryTracker(HistoryStore store, ILogger? logger = null)
    {
        _store = store;
        _clock = store.Clock;
        _log = logger ?? NullLogger.Instance;
    }

    public HistoryStore Store => _store;

    /// <summary>The resume question waiting for an answer (a copy), or <c>null</c>.</summary>
    public ResumeInfo? Resume
    {
        get
        {
            lock (_gate)
                return _resume is null ? null : new ResumeInfo { Uri = _resume.Uri, Title = _resume.Title, Position = _resume.Position, Length = _resume.Length, Pending = true };
        }
    }

    /// <summary>The offered next episode (a copy), or <c>null</c>.</summary>
    public NextInfo? Next
    {
        get { lock (_gate) return _next is null ? null : new NextInfo { Uri = _next.Uri, Title = _next.Title }; }
    }

    /// <summary>VLC is kept paused for the resume question (fullscreen and other automation should wait).</summary>
    public bool IsHolding { get { lock (_gate) return _hold != Hold.None; } }

    /// <summary>The next <c>open</c> of <paramref name="uri"/> selects tracks itself: do not apply the folder choices.</summary>
    public void ExpectExplicitTracks(string uri)
    {
        lock (_gate) _explicitTracksUri = MediaUri.Normalize(uri);
    }

    // ------------------------------------------------------------------ polls

    public IReadOnlyList<HistoryAction> Observe(PlaybackObservation obs)
    {
        var actions = new List<HistoryAction>();
        lock (_gate)
        {
            var key = obs.IsActive ? MediaUri.Normalize(obs.Uri) : null;
            if (_input is not null && (key is null || !string.Equals(key, _input.Uri, StringComparison.OrdinalIgnoreCase)))
            {
                // Stopped / VLC moved on: offer the next episode only when nothing else started.
                FinishInputLocked(actions, endReached: obs.EndReached && !obs.IsActive, offerNext: key is null);
            }
            if (key is null) return actions;

            if (_input is null) StartInputLocked(obs, key, actions);
            if (_input is not null) UpdateInputLocked(obs, actions);
        }
        return actions;
    }

    /// <summary>VLC exited (or the companion is shutting down): save the position, drop the question.</summary>
    public IReadOnlyList<HistoryAction> OnVlcExited()
    {
        var actions = new List<HistoryAction>();
        lock (_gate)
        {
            if (_input is not null) FinishInputLocked(actions, endReached: false, offerNext: true);
            ClearResumeLocked();
        }
        return actions;
    }

    private void StartInputLocked(PlaybackObservation obs, string key, List<HistoryAction> actions)
    {
        if (_next is not null)
        {
            _log.LogDebug("Next-episode offer cleared: another input started");
            _next = null;
        }
        ClearResumeLocked();

        long? size = MediaUri.ToLocalPath(key) is { } path ? _store.FileSystem.TryGetFileSize(path) : null;
        var start = _store.BeginPlayback(key, obs.Title, obs.Length, size);
        if (start is null) return;
        var now = _clock.GetUtcNow();
        var explicitTracks = _explicitTracksUri is not null && string.Equals(_explicitTracksUri, key, StringComparison.OrdinalIgnoreCase);
        _explicitTracksUri = null;
        _input = new Input
        {
            Uri = start.After.Uri,
            FolderUri = start.After.FolderUri,
            FinishedAtStart = start.Before?.Finished == true,
            ExplicitTracks = explicitTracks,
            Title = obs.Title,
            Length = obs.Length,
            LastTime = obs.Time,
            LastSavedAt = now,
            State = obs.State,
        };
        _log.LogDebug("History: started {Uri} (played {Count} times)", start.After.Uri, start.After.PlayCount);

        if (HistoryRules.ShouldOfferResume(start.Before) && obs.Time <= FreshStartSeconds)
        {
            var before = start.Before!;
            _resume = new ResumeInfo
            {
                Uri = obs.Uri ?? key,
                Title = start.After.Title,
                Position = before.Position,
                Length = start.After.Length > 0 ? start.After.Length : before.Length,
                Pending = true,
            };
            _hold = Hold.PausePending;
            _pauseSentAt = now;
            _pauseAttempts = 1;
            actions.Add(HistoryAction.Pause);
            actions.Add(HistoryAction.Send(new EventMessage
            {
                Name = EventNames.ResumePrompt,
                Uri = _resume.Uri,
                Title = _resume.Title,
                Position = _resume.Position,
                Length = _resume.Length,
                Pending = true,
            }));
            _log.LogInformation("Resume prompt: {Title} was left at {Position} s of {Length} s; VLC is paused until a phone answers",
                _resume.Title, _resume.Position, _resume.Length);
        }
    }

    private void UpdateInputLocked(PlaybackObservation obs, List<HistoryAction> actions)
    {
        var input = _input!;
        var now = _clock.GetUtcNow();

        var titleChanged = !string.IsNullOrWhiteSpace(obs.Title) && obs.Title != input.Title;
        var lengthChanged = obs.Length > 0 && obs.Length != input.Length;
        if (titleChanged || lengthChanged)
        {
            if (titleChanged) input.Title = obs.Title;
            if (lengthChanged) input.Length = obs.Length;
            _store.UpdateMeta(input.Uri, input.Title, input.Length);
            if (_resume is not null && lengthChanged && _resume.Length <= 0) _resume.Length = obs.Length;
        }

        switch (_hold)
        {
            case Hold.PausePending when obs.State == "paused":
                _hold = Hold.Held;
                break;
            case Hold.PausePending when obs.State == "playing" && now - _pauseSentAt >= PauseRetryInterval:
                if (_pauseAttempts < MaxPauseAttempts)
                {
                    _pauseAttempts++;
                    _pauseSentAt = now;
                    actions.Add(HistoryAction.Pause);
                }
                else
                {
                    _log.LogWarning("Resume prompt dropped: VLC did not pause {Title}", _resume?.Title);
                    ClearResumeLocked();
                }
                break;
            case Hold.Held when obs.State == "playing":
                _log.LogInformation("Resume prompt cleared: playback was resumed without an answer (from {Time} s)", obs.Time);
                ClearResumeLocked();
                input.LastSavedAt = now;
                input.KeepResumePoint = true;
                break;
        }

        if (input.KeepResumePoint && obs.Time >= HistoryRules.ResumeThresholdSeconds) input.KeepResumePoint = false;
        if (_hold == Hold.None && input.KeepResumePoint)
        {
            // A few accidental seconds from the start do not replace the resume point.
            input.LastTime = obs.Time;
            input.Dirty = false;
        }
        else if (_hold == Hold.None)
        {
            if (obs.Time != input.LastTime)
            {
                input.LastTime = obs.Time;
                input.Dirty = true;
            }
            if (obs.State == "playing" && input.Dirty && now - input.LastSavedAt >= SaveInterval)
                SaveLocked(input, actions, notify: false);
            else if (obs.State == "paused" && input.State == "playing")
                SaveLocked(input, actions, notify: true);
        }
        input.State = obs.State;

        if (obs.Tracks is { } tracks && (tracks.Audio.Count > 0 || tracks.Subtitle.Count > 0))
            TrackChoicesLocked(input, tracks, actions, now);
    }

    private void SaveLocked(Input input, List<HistoryAction> actions, bool notify)
    {
        var r = _store.UpdatePosition(input.Uri, input.LastTime, input.Length, allowUnfinish: input.FinishedAtStart, notify);
        input.Dirty = false;
        input.LastSavedAt = _clock.GetUtcNow();
        if (r.BecameFinished) OfferNextLocked(input, actions);
    }

    private void FinishInputLocked(List<HistoryAction> actions, bool endReached, bool offerNext)
    {
        var input = _input!;
        _input = null;
        if (_hold != Hold.None)
        {
            // Nothing was watched while the question was open: the resume point stays as it was.
            _log.LogInformation("Resume prompt cleared: {Title} is no longer playing", _resume?.Title);
            ClearResumeLocked();
            return;
        }
        if (input.KeepResumePoint && !endReached) return;
        PositionUpdate r;
        if (endReached && input.Length > 0)
            r = _store.UpdatePosition(input.Uri, input.Length, input.Length, allowUnfinish: true, notify: true);
        else if (endReached)
            r = new PositionUpdate(true, _store.SetFinished(input.Uri, true), false);
        else
            r = _store.UpdatePosition(input.Uri, input.LastTime, input.Length, allowUnfinish: input.FinishedAtStart, notify: true);
        if (offerNext && !input.NextOffered && (r.BecameFinished || endReached))
            OfferNextLocked(input, actions);
    }

    private void OfferNextLocked(Input input, List<HistoryAction> actions)
    {
        input.NextOffered = true;
        NextInfo? next;
        try { next = _store.FindNextEpisode(input.Uri); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Looking up the next episode of {Uri} failed", input.Uri);
            return;
        }
        if (next is null) return;
        _next = next;
        actions.Add(HistoryAction.Send(new EventMessage { Name = EventNames.NextEpisode, Uri = next.Uri, Title = next.Title }));
        _log.LogInformation("Next episode offered: {Title} ({Uri})", next.Title, next.Uri);
    }

    private void ClearResumeLocked()
    {
        _resume = null;
        _hold = Hold.None;
        _pauseAttempts = 0;
    }

    // ------------------------------------------------------------------ track choices

    private void TrackChoicesLocked(Input input, TracksInfo tracks, List<HistoryAction> actions, DateTimeOffset now)
    {
        var signature = string.Join(",", tracks.Audio.Select(t => t.Id)) + "|" + string.Join(",", tracks.Subtitle.Select(t => t.Id));
        if (input.PrefsPending)
        {
            input.PrefsPending = false;
            int? audio = tracks.CurrentAudio, subtitle = tracks.CurrentSubtitle;
            if (!input.ExplicitTracks)
            {
                var plan = HistoryRules.PlanTrackSelection(_store.GetFolderPreferences(input.FolderUri), tracks);
                foreach (var (kind, id) in plan)
                {
                    actions.Add(HistoryAction.Select(kind, id));
                    if (kind == "audio") input.AppliedAudio = audio = id;
                    else input.AppliedSubtitle = subtitle = id;
                }
                if (plan.Count > 0)
                    _log.LogInformation("Applied the folder's track choices to {Title}: {Plan}", input.Title,
                        string.Join(", ", plan.Select(p => $"{p.Kind} {p.Id}")));
            }
            input.BaseAudio = audio;
            input.BaseSubtitle = subtitle;
            input.BaseSignature = signature;
            input.BaseAt = now;
            return;
        }

        if (input.BaseSignature != signature)
        {
            // Tracks were added (an external subtitle file…): start over.
            input.BaseSignature = signature;
            input.BaseAt = now;
            input.BaseAudio = tracks.CurrentAudio;
            input.BaseSubtitle = tracks.CurrentSubtitle;
            return;
        }
        var settled = now - input.BaseAt >= TrackSettleTime;
        if (tracks.CurrentAudio is int a && a != input.BaseAudio)
        {
            if (settled && input.BaseAudio is not null && a != input.AppliedAudio) RecordLocked(input, "audio", a, tracks, "VLC");
            if (settled) input.AppliedAudio = null; // only the first echo of our own choice is ignored
            input.BaseAudio = a;
        }
        if (tracks.CurrentSubtitle is int s && s != input.BaseSubtitle)
        {
            if (settled && input.BaseSubtitle is not null && s != input.AppliedSubtitle) RecordLocked(input, "subtitle", s, tracks, "VLC");
            if (settled) input.AppliedSubtitle = null;
            input.BaseSubtitle = s;
        }
    }

    /// <summary>A phone picked a track (<c>cmd track</c>): remember it for the folder of the current file.</summary>
    public void OnTrackChosen(string kind, int id, TracksInfo? tracks)
    {
        lock (_gate)
        {
            if (_input is not { } input) return;
            RecordLocked(input, kind, id, tracks, "phone");
            if (kind == "audio") input.BaseAudio = id;
            else if (kind == "subtitle") input.BaseSubtitle = id;
        }
    }

    private void RecordLocked(Input input, string kind, int id, TracksInfo? tracks, string source)
    {
        switch (kind)
        {
            case "audio" when tracks?.Audio.FirstOrDefault(t => t.Id == id) is { } track:
                _store.SetAudioPreference(input.FolderUri, HistoryRules.ToPreference(track));
                _log.LogInformation("Audio choice for {Folder} ({Source}): {Lang} / {Name}", input.FolderUri, source, track.Lang, track.Name);
                break;
            case "subtitle" when id < 0:
                _store.SetSubtitlePreference(input.FolderUri, SubtitleChoice.Disabled);
                _log.LogInformation("Subtitle choice for {Folder} ({Source}): off", input.FolderUri, source);
                break;
            case "subtitle" when tracks?.Subtitle.FirstOrDefault(t => t.Id == id) is { } track:
                _store.SetSubtitlePreference(input.FolderUri, SubtitleChoice.Of(HistoryRules.ToPreference(track)));
                _log.LogInformation("Subtitle choice for {Folder} ({Source}): {Lang} / {Name}", input.FolderUri, source, track.Lang, track.Name);
                break;
        }
    }

    // ------------------------------------------------------------------ phone answers

    /// <summary>
    /// <c>cmd resume</c>: <paramref name="choice"/> <c>resume</c> (seek to the saved point and play) or <c>restart</c>
    /// (play from the beginning). The first answer wins; later ones do nothing.
    /// </summary>
    public IReadOnlyList<HistoryAction> Answer(string choice)
    {
        if (choice is not ("resume" or "restart"))
            throw new ArgumentException("choice must be 'resume' or 'restart'", nameof(choice));
        lock (_gate)
        {
            if (_resume is null)
            {
                _log.LogDebug("Resume answer {Choice} ignored: nothing is pending", choice);
                return [];
            }
            var target = choice == "resume" ? _resume.Position : 0;
            _log.LogInformation("Resume prompt answered: {Choice} ({Title} from {Target} s)", choice, _resume.Title, target);
            ClearResumeLocked();
            if (_input is { } input)
            {
                input.LastTime = target;
                input.Dirty = false;
                input.LastSavedAt = _clock.GetUtcNow();
                // input.State stays as observed: a poll that still sees "paused" is not a pause by the user.
            }
            return [HistoryAction.SeekTo(target), HistoryAction.Play];
        }
    }

    /// <summary><c>cmd dismissNext</c>.</summary>
    public void DismissNext()
    {
        lock (_gate)
        {
            if (_next is not null) _log.LogInformation("Next-episode offer dismissed: {Title}", _next.Title);
            _next = null;
        }
    }
}
