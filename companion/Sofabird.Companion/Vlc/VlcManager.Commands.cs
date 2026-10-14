using Microsoft.Extensions.Logging;
using Sofabird.Core.Protocol;
using Sofabird.Core.Session;
using Sofabird.Core.Vlc;
using Sofabird.Core.Localization;

namespace Sofabird.Companion.Vlc;

// Requests from the phone: data (state, playlist, browse, tracks, art) and commands.
public sealed partial class VlcManager
{
    // ------------------------------------------------------------------ data requests

    private void EnsureRunning()
    {
        if (RunState != VlcRunState.Running)
            throw ProtocolException.VlcNotRunning(RunState switch
            {
                VlcRunState.NotInstalled => ProtocolText.VlcNotInstalled,
                VlcRunState.RunningNoApi => ProtocolText.VlcNoControl,
                VlcRunState.Starting => ProtocolText.VlcStarting,
                _ => ProtocolText.VlcNotRunning,
            });
    }

    public async Task<StateMessage> GetStateAsync(CancellationToken ct)
    {
        await PollOnceAsync(ct).ConfigureAwait(false);
        return LastState ?? BuildState(null);
    }

    public async Task<IReadOnlyList<PlaylistEntry>> GetPlaylistAsync(CancellationToken ct)
    {
        EnsureRunning();
        var items = await _http.GetPlaylistAsync(ct).ConfigureAwait(false);
        _playlist = items;
        _playlistDirty = false;
        return items.Select(i => new PlaylistEntry
        {
            PlId = i.PlId, Name = i.Name, Uri = i.Uri, DurationSec = i.Duration, Current = i.Current,
        }).ToList();
    }

    /// <summary>
    /// File browsing is done by the companion itself (without VLC): the library is available even when VLC is closed or open
    /// without control — the movie is chosen on the phone, and <c>open</c> starts VLC.
    /// </summary>
    public async Task<IReadOnlyList<BrowseEntry>> BrowseAsync(string uri, CancellationToken ct)
    {
        var browser = new LocalFileBrowser(() => _settings.MediaRoots);
        try
        {
            return await Task.Run(() =>
            {
                var items = browser.Browse(uri);
                _history?.Store.Annotate(items);
                return items;
            }, ct).ConfigureAwait(false);
        }
        catch (DirectoryNotFoundException)
        {
            throw ProtocolException.BadRequest(ProtocolText.FolderNotFound(LocalFileBrowser.ToLocalPath(uri)));
        }
        catch (IOException ex)
        {
            throw ProtocolException.BadRequest(ProtocolText.FolderUnavailable(ex.Message));
        }
    }

    public async Task<TracksInfo> GetTracksAsync(CancellationToken ct)
    {
        EnsureRunning();
        _rcTracksDirty = true;
        var state = await GetStateAsync(ct).ConfigureAwait(false);
        return state.Tracks ?? new TracksInfo();
    }

    public Task<VlcArt?> GetArtAsync(int? plId, CancellationToken ct)
    {
        EnsureRunning();
        return _http.GetArtAsync(plId, ct);
    }

    // ------------------------------------------------------------------ commands

    public async Task ExecuteCommandAsync(string name, CommandArgs args, CancellationToken ct)
    {
        switch (name)
        {
            case "open" when RunState != VlcRunState.Running && !(args.GetBool("enqueue") ?? false):
                // VLC is closed / starting / without control: start (or restart) it right away with this file.
                await LaunchAsync(args.RequireString("uri"), args.GetInt("display"), ct).ConfigureAwait(false);
                return;
            case "launch":
                await LaunchAsync(args.GetString("uri"), args.GetInt("display"), ct).ConfigureAwait(false);
                return;
            case "quit":
                if (!IsProcessRunning) return;
                await QuitAsync(ct).ConfigureAwait(false);
                return;
            case "dismissNext":
                _history?.DismissNext();
                Poke();
                return;
            case "resume":
            {
                var choice = args.RequireString("choice");
                if (_history is null) throw ProtocolException.Unsupported("watch history is off");
                var actions = _history.Answer(choice);
                if (actions.Count > 0)
                {
                    EnsureRunning();
                    await RunHistoryActionsAsync(actions, ct).ConfigureAwait(false);
                }
                Poke();
                return;
            }
        }

        EnsureRunning();
        VlcStatus? result;
        if (VlcCommandMap.Map(name, args) is { } http)
        {
            result = await Cmd(http.Command, ct, http.Args).ConfigureAwait(false);
            AfterMappedCommand(name, args);
        }
        else
        {
            result = await ExecuteStatefulCommandAsync(name, args, ct).ConfigureAwait(false);
        }

        if (result is not null) _lastStatus = result;
        Poke();
    }

    /// <summary>Bookkeeping after a stateless command (<see cref="VlcCommandMap"/>).</summary>
    private void AfterMappedCommand(string name, CommandArgs args)
    {
        switch (name)
        {
            case "track":
            {
                _rcTracksDirty = true;
                var kind = args.RequireString("kind");
                if (kind is "audio" or "subtitle") _history?.OnTrackChosen(kind, args.RequireInt("id"), LastState?.Tracks);
                break;
            }
            case "playlistClear":
                _playlistDirty = true;
                break;
        }
    }

    /// <summary>Commands that depend on VLC's current status, the playlist or the remembered volume before mute.</summary>
    private async Task<VlcStatus?> ExecuteStatefulCommandAsync(string name, CommandArgs args, CancellationToken ct)
    {
        VlcStatus? result = null;
        switch (name)
        {
            case "play":
            {
                var plId = args.GetInt("plId");
                if (plId is int id) result = await Cmd("pl_play", ct, ("id", Str(id))).ConfigureAwait(false);
                else if (_lastStatus?.State == "paused") result = await Cmd("pl_forceresume", ct).ConfigureAwait(false);
                else if (_lastStatus?.IsStopped != false) result = await Cmd("pl_play", ct).ConfigureAwait(false);
                break;
            }
            case "volume":
            {
                if (args.GetInt("value") is int v)
                {
                    v = Math.Clamp(v, 0, 512);
                    result = await Cmd("volume", ct, ("val", Str(v))).ConfigureAwait(false);
                    if (v > 0) _muted = false;
                }
                else if (args.GetInt("delta") is int d)
                {
                    result = await Cmd("volume", ct, ("val", Signed(d))).ConfigureAwait(false);
                    if (result.Volume > 0) _muted = false;
                }
                else throw ProtocolException.BadRequest("volume requires 'value' or 'delta'");
                break;
            }
            case "mute":
            {
                var on = args.RequireBool("on");
                if (on && !_muted)
                {
                    var cur = _lastStatus?.Volume ?? 256;
                    if (cur > 0) _savedVolume = cur;
                    result = await Cmd("volume", ct, ("val", "0")).ConfigureAwait(false);
                    _muted = true;
                }
                else if (!on && _muted)
                {
                    result = await Cmd("volume", ct, ("val", Str(_savedVolume <= 0 ? 256 : _savedVolume))).ConfigureAwait(false);
                    _muted = false;
                }
                break;
            }
            case "chapter":
            {
                // "index" is stateless (VlcCommandMap); "delta" needs the current chapter.
                if (args.GetInt("delta") is not int delta) throw ProtocolException.BadRequest("chapter requires 'index' or 'delta'");
                var fresh = await _http.GetStatusAsync(ct).ConfigureAwait(false);
                var info = fresh.Information ?? throw ProtocolException.VlcError("nothing is playing");
                var target = Math.Clamp(info.Chapter + delta, 0, Math.Max(0, info.ChapterCount - 1));
                result = await Cmd("chapter", ct, ("val", Str(target))).ConfigureAwait(false);
                break;
            }
            case "fullscreen":
            {
                var on = args.GetBool("on");
                if (on is null) result = await Cmd("fullscreen", ct).ConfigureAwait(false);
                else
                {
                    var fresh = await _http.GetStatusAsync(ct).ConfigureAwait(false);
                    if (fresh.Fullscreen != on.Value) result = await Cmd("fullscreen", ct).ConfigureAwait(false);
                }
                break;
            }
            case "open":
                await OpenAsync(args.RequireString("uri"), args.GetBool("enqueue") ?? false, args.GetString("subtitle"),
                    args.GetInt("audioTrack"), args.GetInt("subtitleTrack"), ct).ConfigureAwait(false);
                break;
            case "playlistRemove":
                await RemoveFromPlaylistAsync(args.RequireInt("plId"), ct).ConfigureAwait(false);
                break;
            case "random":
            case "loop":
            case "repeat":
            {
                var on = args.RequireBool("on");
                var fresh = await _http.GetStatusAsync(ct).ConfigureAwait(false);
                var current = name switch { "random" => fresh.Random, "loop" => fresh.Loop, _ => fresh.Repeat };
                if (current != on) result = await Cmd("pl_" + name, ct).ConfigureAwait(false);
                break;
            }
            default:
                throw ProtocolException.Unsupported($"unknown command '{name}'");
        }
        return result;
    }

    /// <summary>VLC sometimes ignores the first <c>pl_delete</c> right after a playlist change: one retry, then an error.</summary>
    private async Task RemoveFromPlaylistAsync(int plId, CancellationToken ct)
    {
        await Cmd("pl_delete", ct, ("id", Str(plId))).ConfigureAwait(false);
        var list = await _http.GetPlaylistAsync(ct).ConfigureAwait(false);
        if (list.Any(i => i.PlId == plId))
        {
            await Task.Delay(200, ct).ConfigureAwait(false);
            await Cmd("pl_delete", ct, ("id", Str(plId))).ConfigureAwait(false);
            list = await _http.GetPlaylistAsync(ct).ConfigureAwait(false);
            if (list.Any(i => i.PlId == plId))
                throw ProtocolException.VlcError($"VLC did not remove playlist item {plId}");
        }
        _playlist = list;
    }

    /// <summary>Fullscreen state of the video output; <c>null</c> — VLC is not under control.</summary>
    public async Task<bool?> GetFullscreenAsync(CancellationToken ct)
    {
        if (RunState != VlcRunState.Running) return null;
        var status = await _http.GetStatusAsync(ct).ConfigureAwait(false);
        return status.Fullscreen;
    }

    public async Task SetFullscreenAsync(bool on, CancellationToken ct)
    {
        if (RunState != VlcRunState.Running) return;
        var fresh = await _http.GetStatusAsync(ct).ConfigureAwait(false);
        if (fresh.Fullscreen != on) _lastStatus = await Cmd("fullscreen", ct).ConfigureAwait(false);
        Poke();
    }

    private async Task OpenAsync(string uri, bool enqueue, string? subtitle, int? audioTrack, int? subtitleTrack, CancellationToken ct)
    {
        var before = _lastStatus?.CurrentPlId ?? -1;
        // The phone picked tracks for this file: they win over the folder's remembered choices.
        if (!enqueue && (subtitle is not null || audioTrack is not null || subtitleTrack is not null))
            _history?.ExpectExplicitTracks(uri);
        var argList = new List<(string, string)> { ("input", uri) };
        await Cmd(enqueue ? "in_enqueue" : "in_play", ct, argList.ToArray()).ConfigureAwait(false);
        _playlistDirty = true;
        if (enqueue) return;

        if (subtitle is null && audioTrack is null && subtitleTrack is null) return;

        // Tracks are selected in the background after the start: wait until the new input and information appear.
        _postOpenCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _postOpenCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
                VlcStatus? st = null;
                while (DateTimeOffset.UtcNow < deadline && !cts.IsCancellationRequested)
                {
                    await Task.Delay(400, cts.Token).ConfigureAwait(false);
                    st = await _http.GetStatusAsync(cts.Token).ConfigureAwait(false);
                    if (st.Information is not null && st.Information.Streams.Count > 0 && (st.CurrentPlId != before || before < 0) && !st.IsStopped)
                        break;
                    st = null;
                }
                if (st is null)
                {
                    _log.LogWarning("open: input did not start within 15 s; skipping track selection");
                    return;
                }
                if (!string.IsNullOrEmpty(subtitle))
                {
                    await Cmd("addsubtitle", cts.Token, ("val", subtitle)).ConfigureAwait(false);
                    await Task.Delay(500, cts.Token).ConfigureAwait(false);
                }
                if (audioTrack is int a)
                    await Cmd("audio_track", cts.Token, ("val", Str(a))).ConfigureAwait(false);
                if (subtitleTrack is int s)
                    await Cmd("subtitle_track", cts.Token, ("val", Str(s))).ConfigureAwait(false);
                _rcTracksDirty = true;
                Poke();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "open: post-start track selection failed");
            }
        }, CancellationToken.None);
    }

    private async Task<VlcStatus> Cmd(string command, CancellationToken ct, params (string Name, string Value)[] args)
    {
        try
        {
            return await _http.CommandAsync(command, ct, args).ConfigureAwait(false);
        }
        catch (VlcUnavailableException)
        {
            Poke();
            throw;
        }
    }

    private static string Str(int v) => VlcCommandMap.Number(v);
    private static string Signed(int v) => VlcCommandMap.Relative(v);
}
