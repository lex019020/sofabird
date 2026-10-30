using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Sofabird.Companion.Vlc;
using Sofabird.Core.Vlc;

namespace Sofabird.Companion.Tests.Fake;

/// <summary>How the HTTP interface of a running fake VLC answers.</summary>
internal enum FakeHttp
{
    /// <summary>Normal answers (with the right password).</summary>
    Up,
    /// <summary>Connection refused: the web interface is off or not listening yet.</summary>
    Refused,
    /// <summary><c>200</c> with an empty body: VLC is still starting.</summary>
    Starting,
}

/// <summary>An HTTP request the fake VLC received.</summary>
/// <param name="Page"><c>status.json</c>, <c>playlist.json</c>, <c>tracks.json</c>, <c>art</c>…</param>
/// <param name="Command">The <c>command</c> of <c>status.json</c>; <c>null</c> — a plain read.</param>
/// <param name="Args">Decoded query parameters, in order.</param>
internal sealed record FakeRequest(string Page, string? Command, IReadOnlyList<KeyValuePair<string, string>> Args)
{
    public string? Arg(string name) => Args.FirstOrDefault(a => a.Key == name).Value;

    /// <summary><c>status.json</c> → <c>status.json</c>; a command → <c>seek val=+30</c>.</summary>
    public override string ToString() => Command is null
        ? Page
        : string.Join(' ', Args.Where(a => a.Key != "command").Select(a => $"{a.Key}={a.Value}").Prepend(Command));
}

/// <summary>
/// One PC with at most one <c>vlc.exe</c>, as the companion sees it — the process (<see cref="Processes"/>), VLC's HTTP
/// interface (<see cref="CreateHttpHandler"/>: <c>status.json</c> with its commands, <c>playlist.json</c> and our
/// <c>tracks.json</c>, in the shapes of the real VLC 3.0.23 samples in <c>docs/research/samples</c>) and its RC
/// interface (<see cref="ConnectRcAsync"/>: track lists, <c>adev</c>, <c>quit</c> and <c>status change:</c> lines).
/// Test helpers start and kill the process and move playback on; every request is recorded. Thread-safe.
/// </summary>
internal sealed class FakeVlc : IDisposable
{
    public const string Version = "3.0.23 Vetinari";
    /// <summary>A path that does not exist: nothing in a test could start a real VLC from it.</summary>
    public const string ExePath = @"X:\fake\VideoLAN\VLC\vlc.exe";

    private readonly object _gate = new();
    private readonly Dictionary<string, FakeMedia> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FakeRequest> _requests = [];
    private readonly List<string> _rcCommands = [];
    private readonly List<FakeRcConnection> _rc = [];
    private readonly List<Item> _playlist = [];
    private int _nextPid = 4100;
    private int _nextPlId = 3; // a fresh VLC 3 gives the first playlist item id 3

    private int? _pid;
    private DateTime _startTime;
    private string _commandLine = "";
    private string? _password;
    private FakeHttp _http;
    private bool _luaPages = true;
    private Func<FakeRequest, bool>? _crashOn;

    private Item? _current;
    private string _state = "stopped";
    private long _time;
    private int _volume = 256;
    private bool _fullscreen;
    private int _audio = -1;
    private int _spu = -1;
    private int _video = -1;
    private bool _random;
    private bool _loop;
    private bool _repeat;

    public FakeVlc()
    {
        Processes = new FakeProcesses(this);
    }

    private sealed record Item(int PlId, FakeMedia Media);

    /// <summary>The <c>vlc.exe</c> processes for <see cref="VlcBackend"/>.</summary>
    public IVlcProcesses Processes { get; }

    /// <summary>The HTTP transport for <see cref="VlcBackend"/>.</summary>
    public HttpMessageHandler CreateHttpHandler() => new Handler(this);

    // ------------------------------------------------------------------ what the tests see

    /// <summary>The running <c>vlc.exe</c>; <c>null</c> — none.</summary>
    public int? Pid { get { lock (_gate) return _pid; } }

    /// <summary><c>playing</c> | <c>paused</c> | <c>stopped</c>.</summary>
    public string State { get { lock (_gate) return _state; } }

    public FakeHttp Http
    {
        get { lock (_gate) return _http; }
        set { lock (_gate) _http = value; }
    }

    /// <summary>Our Lua pages are installed in VLC (<c>tracks.json</c> answers); <c>false</c> — it is a 404.</summary>
    public bool LuaPages
    {
        get { lock (_gate) return _luaPages; }
        set { lock (_gate) _luaPages = value; }
    }

    /// <summary>VLC dies on the first request that matches (the request gets a dropped connection).</summary>
    public Func<FakeRequest, bool>? CrashOn
    {
        get { lock (_gate) return _crashOn; }
        set { lock (_gate) _crashOn = value; }
    }

    /// <summary>The <c>status.json?command=…</c> requests, as <see cref="FakeRequest.ToString"/> spells them.</summary>
    public IReadOnlyList<string> Commands
    {
        get { lock (_gate) return _requests.Where(r => r.Command is not null).Select(r => r.ToString()).ToList(); }
    }

    /// <summary>Command lines received over RC.</summary>
    public IReadOnlyList<string> RcCommands { get { lock (_gate) return _rcCommands.ToList(); } }

    /// <summary>The arguments of the last launch by the companion; <c>null</c> — VLC was never launched by it.</summary>
    public IReadOnlyList<string>? LaunchArgs { get; private set; }

    public void ClearLog()
    {
        lock (_gate)
        {
            _requests.Clear();
            _rcCommands.Clear();
        }
    }

    // ------------------------------------------------------------------ what happens on the PC

    /// <summary>A file VLC can open by URI (<c>in_play</c>, a launch with a file).</summary>
    public void AddFile(FakeMedia media)
    {
        lock (_gate) _files[media.Uri] = media;
    }

    /// <summary>
    /// <c>vlc.exe</c> starts (opened by hand, or by <see cref="IVlcProcesses.Start"/>): a fresh VLC with an empty playlist;
    /// <paramref name="media"/> become the playlist and the first one plays.
    /// </summary>
    /// <param name="password">The HTTP password VLC got (from <c>vlcrc</c> or the command line); <c>null</c> — none (403).</param>
    /// <param name="startedAt">Process start time (local); default — now.</param>
    public int StartProcess(string? password, DateTime? startedAt = null, params FakeMedia[] media)
    {
        lock (_gate)
        {
            if (_pid is not null) throw new InvalidOperationException("the fake runs one VLC at a time");
            _pid = _nextPid++;
            _startTime = startedAt ?? DateTime.Now;
            _commandLine = media.Length == 0 ? $"\"{ExePath}\"" : $"\"{ExePath}\" \"{media[0].Uri}\"";
            _password = password;
            _http = FakeHttp.Up;
            _playlist.Clear();
            _current = null;
            _state = "stopped";
            _time = 0;
            _volume = 256;
            _fullscreen = false;
            foreach (var m in media)
            {
                _files[m.Uri] = m;
                AddLocked(m);
            }
            if (_playlist.Count > 0) PlayLocked(_playlist[0]);
            return _pid.Value;
        }
    }

    /// <summary>VLC exits (closed by the user, crashed): the process, HTTP and RC are gone.</summary>
    public void Exit()
    {
        lock (_gate) ExitLocked();
    }

    /// <summary>Stop pressed in VLC's own window (or its keyboard shortcut), not through the companion.</summary>
    public void PressStop()
    {
        lock (_gate) StopLocked();
    }

    /// <summary>Playback moves on by <paramref name="seconds"/>; at the end VLC goes to the next item or stops.</summary>
    public void Advance(long seconds)
    {
        lock (_gate)
        {
            if (_state != "playing" || _current is null) return;
            _time += seconds;
            if (_time >= _current.Media.End)
            {
                _time = _current.Media.End;
                EndOfInputLocked();
            }
        }
    }

    // ------------------------------------------------------------------ playback model

    private Item AddLocked(FakeMedia media)
    {
        var item = new Item(_nextPlId++, media);
        _playlist.Add(item);
        return item;
    }

    private void PlayLocked(Item? item)
    {
        if (item is null) return;
        _current = item;
        _time = 0;
        _state = "playing";
        var tracks = item.Media.Tracks;
        _audio = tracks.FirstOrDefault(t => t.Type == "Audio")?.Id ?? -1;
        _video = tracks.FirstOrDefault(t => t.Type == "Video")?.Id ?? -1;
        _spu = item.Media.DefaultSubtitle;
        EmitLocked($"status change: ( new input: {item.Media.Uri} )", "status change: ( play state: 3 )");
    }

    private void SetStateLocked(string state)
    {
        if (_state == state || _current is null) return;
        _state = state;
        EmitLocked(state == "paused" ? "status change: ( pause state: 4 )" : "status change: ( play state: 3 )");
    }

    /// <summary>
    /// The input reached its end: VLC prints its input state "End" at once (twice, as VLC 3.0.23 does), then the playlist
    /// goes on or stops.
    /// </summary>
    private void EndOfInputLocked()
    {
        EmitLocked("status change: ( play state: 4 ): End", "status change: ( play state: 4 ): End");
        var index = _current is null ? -1 : _playlist.IndexOf(_current);
        Item? next = _repeat ? _current
            : index >= 0 && index + 1 < _playlist.Count ? _playlist[index + 1]
            : _loop && _playlist.Count > 0 ? _playlist[0]
            : null;
        if (next is not null) PlayLocked(next);
        else StopLocked();
    }

    private void StopLocked()
    {
        if (_state == "stopped") return;
        _state = "stopped";
        _time = 0;
        // Real VLC prints this for any stop, the end of a file included (RC's loop prints it when it next reads a
        // command; here at once — the worst case for telling a stop from the end).
        EmitLocked("status change: ( stop state: 0 )");
    }

    private void ExitLocked()
    {
        _pid = null;
        foreach (var c in _rc) c.Close();
        _rc.Clear();
        _playlist.Clear();
        _current = null;
        _state = "stopped";
        _time = 0;
    }

    private Item? NeighbourLocked(int step)
    {
        if (_current is null) return _playlist.FirstOrDefault();
        var index = _playlist.IndexOf(_current) + step;
        return index >= 0 && index < _playlist.Count ? _playlist[index] : null;
    }

    private FakeMedia MediaFor(string uri) =>
        _files.TryGetValue(uri, out var m) ? m
            : FakeMedia.Movie(uri, Uri.UnescapeDataString(uri[(uri.LastIndexOf('/') + 1)..]), 3600);

    /// <summary>Applies <c>status.json?command=…</c>; returns <c>true</c> when VLC quits after answering.</summary>
    private bool ApplyLocked(FakeRequest r)
    {
        var val = r.Arg("val");
        switch (r.Command)
        {
            case "pl_play":
                if (int.TryParse(r.Arg("id"), CultureInfo.InvariantCulture, out var id)) PlayLocked(_playlist.Find(i => i.PlId == id));
                else if (_state == "paused") SetStateLocked("playing");
                else if (_state == "stopped") PlayLocked(_current ?? _playlist.FirstOrDefault());
                break;
            case "pl_pause":
                if (_state == "playing") SetStateLocked("paused");
                else if (_state == "paused") SetStateLocked("playing");
                else PlayLocked(_current ?? _playlist.FirstOrDefault());
                break;
            case "pl_forcepause":
                if (_state == "playing") SetStateLocked("paused");
                break;
            case "pl_forceresume":
                if (_state == "paused") SetStateLocked("playing");
                break;
            case "pl_stop":
                StopLocked();
                break;
            case "pl_next":
                if (NeighbourLocked(+1) is { } next) PlayLocked(next);
                else StopLocked();
                break;
            case "pl_previous":
                PlayLocked(NeighbourLocked(-1) ?? _current);
                break;
            case "pl_empty":
                StopLocked();
                _playlist.Clear();
                _current = null;
                break;
            case "pl_delete":
                if (int.TryParse(r.Arg("id"), CultureInfo.InvariantCulture, out var del)) _playlist.RemoveAll(i => i.PlId == del);
                break;
            case "pl_random": _random = !_random; break;
            case "pl_loop": _loop = !_loop; break;
            case "pl_repeat": _repeat = !_repeat; break;
            case "in_play" when r.Arg("input") == "vlc://quit":
                return true;
            case "in_play" when r.Arg("input") is { } input:
                PlayLocked(AddLocked(MediaFor(input)));
                break;
            case "in_enqueue" when r.Arg("input") is { } input:
                AddLocked(MediaFor(input));
                break;
            case "seek" when _current is not null && _state != "stopped" && Relative(val, _time) is long to:
                _time = Math.Clamp(to, 0, _current.Media.Length);
                break;
            case "volume" when Relative(val, _volume) is long volume:
                _volume = (int)Math.Clamp(volume, 0, 512);
                EmitLocked($"status change: ( audio volume: {_volume.ToString(CultureInfo.InvariantCulture)} )");
                break;
            case "audio_track" when HasTrack("Audio", val, out var a):
                _audio = a;
                break;
            case "subtitle_track" when HasTrack("Subtitle", val, out var s):
                _spu = s;
                break;
            case "video_track" when HasTrack("Video", val, out var v):
                _video = v;
                break;
            case "fullscreen" when _state != "stopped":
                _fullscreen = !_fullscreen;
                break;
        }
        return false; // unknown commands are ignored, as VLC does
    }

    private bool HasTrack(string type, string? val, out int id)
    {
        id = 0;
        if (_current is null || !int.TryParse(val, CultureInfo.InvariantCulture, out id)) return false;
        var wanted = id;
        return wanted == -1 || _current.Media.Tracks.Any(t => t.Type == type && t.Id == wanted);
    }

    /// <summary><c>+30</c> / <c>-10</c> are relative, <c>600</c> absolute; anything else (<c>50%</c>) is ignored.</summary>
    private static long? Relative(string? val, long current)
    {
        if (string.IsNullOrEmpty(val) || !long.TryParse(val.TrimStart('+'), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n))
            return null;
        return val[0] is '+' or '-' ? current + n : n;
    }

    // ------------------------------------------------------------------ HTTP

    private sealed class Handler(FakeVlc vlc) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                return Task.FromResult(vlc.HandleHttp(request));
            }
            catch (Exception ex)
            {
                return Task.FromException<HttpResponseMessage>(ex);
            }
        }
    }

    private HttpResponseMessage HandleHttp(HttpRequestMessage request)
    {
        var uri = request.RequestUri ?? throw new ArgumentException("no URI", nameof(request));
        var page = uri.AbsolutePath.TrimStart('/');
        if (page.StartsWith("requests/", StringComparison.Ordinal)) page = page["requests/".Length..];
        var args = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Select(kv => KeyValuePair.Create(Uri.UnescapeDataString(kv[0]), kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : ""))
            .ToList();
        var req = new FakeRequest(page, page == "status.json" ? args.FirstOrDefault(a => a.Key == "command").Value : null, args);

        lock (_gate)
        {
            if (_pid is null || _http == FakeHttp.Refused)
                throw new HttpRequestException("No connection could be made because the target machine actively refused it. (127.0.0.1:8080)",
                    new SocketException((int)SocketError.ConnectionRefused));
            _requests.Add(req);
            if (_crashOn?.Invoke(req) == true)
            {
                ExitLocked();
                throw new HttpRequestException("An error occurred while sending the request.",
                    new IOException("An existing connection was forcibly closed by the remote host."));
            }
            if (string.IsNullOrEmpty(_password))
                return Text(HttpStatusCode.Forbidden, "<html><head><title>Error 403</title></head><body>Forbidden</body></html>");
            var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + _password));
            if (request.Headers.Authorization is not { Scheme: "Basic" } auth || auth.Parameter != expected)
            {
                var denied = Text(HttpStatusCode.Unauthorized, "<html><head><title>Error 401</title></head><body>Unauthorized</body></html>");
                denied.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("Basic", "realm=\"VLC stream\""));
                return denied;
            }
            if (_http == FakeHttp.Starting) return Text(HttpStatusCode.OK, "");

            switch (page)
            {
                case "status.json":
                    var quit = req.Command is not null && ApplyLocked(req);
                    var status = StatusJsonLocked();
                    if (quit) ExitLocked();
                    return Text(HttpStatusCode.OK, status);
                case "playlist.json":
                    return Text(HttpStatusCode.OK, PlaylistJsonLocked());
                case "tracks.json" when _luaPages:
                    return Text(HttpStatusCode.OK, TracksJsonLocked());
                default:
                    return Text(HttpStatusCode.NotFound, "<html><head><title>Error 404</title></head><body>Not found</body></html>");
            }
        }
    }

    private static HttpResponseMessage Text(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };

    private bool StoppedLocked => _state == "stopped" || _current is null;

    /// <summary><c>status.json</c> with the fields (and their order and types) of <c>status-playing.json</c> / <c>status-stopped.json</c>.</summary>
    private string StatusJsonLocked()
    {
        var stopped = StoppedLocked;
        var length = stopped ? 0 : _current!.Media.Length;
        var time = stopped ? 0 : _time;
        var o = new JsonObject
        {
            // a boolean while there is a video output, 0 when stopped
            ["fullscreen"] = stopped ? JsonValue.Create(0) : JsonValue.Create(_fullscreen),
        };
        if (!stopped)
        {
            o["stats"] = new JsonObject { ["inputbitrate"] = 7.4, ["readbytes"] = 49508141, ["decodedvideo"] = 263, ["decodedaudio"] = 1049 };
            o["aspectratio"] = "default";
        }
        o["seek_sec"] = 10;
        o["apiversion"] = 3;
        o["currentplid"] = stopped ? -1 : _current!.PlId;
        o["time"] = time;
        o["volume"] = _volume;
        o["length"] = length;
        o["random"] = _random;
        o["audiofilters"] = new JsonObject { ["filter_0"] = "" };
        if (!stopped) o["information"] = InformationLocked(_current!.Media);
        o["rate"] = 1;
        o["videoeffects"] = new JsonObject { ["hue"] = 0, ["saturation"] = 1, ["contrast"] = 1, ["brightness"] = 1, ["gamma"] = 1 };
        o["state"] = _state;
        o["loop"] = _loop;
        o["version"] = Version;
        o["position"] = length > 0 ? (double)time / length : 0;
        o["audiodelay"] = 0;
        o["repeat"] = _repeat;
        o["subtitledelay"] = 0;
        o["equalizer"] = new JsonArray();
        return o.ToJsonString();
    }

    private static JsonObject InformationLocked(FakeMedia media)
    {
        var meta = new JsonObject { ["filename"] = media.FileName };
        if (media.Title != media.FileName) meta["title"] = media.Title;
        var category = new JsonObject { ["meta"] = meta };
        foreach (var t in media.Tracks)
        {
            var stream = new JsonObject { ["Codec"] = t.Codec, ["Description"] = t.Description, ["Type"] = t.Type };
            if (t.Language is not null) stream["Language"] = t.Language;
            category[$"Stream {t.Id.ToString(CultureInfo.InvariantCulture)}"] = stream;
        }
        return new JsonObject
        {
            ["chapter"] = 0,
            ["chapters"] = new JsonArray(),
            ["title"] = 0,
            ["category"] = category,
            ["titles"] = new JsonArray(JsonValue.Create(0)),
        };
    }

    /// <summary><c>playlist.json</c> as in <c>playlist.json</c> of the samples: root → Playlist / Media Library.</summary>
    private string PlaylistJsonLocked()
    {
        var leaves = new JsonArray();
        foreach (var i in _playlist)
        {
            var leaf = new JsonObject
            {
                ["ro"] = "rw",
                ["type"] = "leaf",
                ["name"] = i.Media.Title,
                ["id"] = i.PlId.ToString(CultureInfo.InvariantCulture),
                ["duration"] = i.Media.Length,
                ["uri"] = i.Media.Uri,
            };
            if (!StoppedLocked && ReferenceEquals(i, _current)) leaf["current"] = "current";
            leaves.Add(leaf);
        }
        return new JsonObject
        {
            ["ro"] = "rw",
            ["type"] = "node",
            ["name"] = "",
            ["id"] = "0",
            ["children"] = new JsonArray(
                new JsonObject { ["ro"] = "ro", ["type"] = "node", ["name"] = "Playlist", ["id"] = "1", ["children"] = leaves },
                new JsonObject { ["ro"] = "ro", ["type"] = "node", ["name"] = "Media Library", ["id"] = "2", ["children"] = new JsonArray() }),
        }.ToJsonString();
    }

    /// <summary>Our Lua page (<c>companion/lua/requests/tracks.json</c>): only the marker while nothing plays.</summary>
    private string TracksJsonLocked()
    {
        var o = new JsonObject { ["sofabird"] = 1 };
        if (!StoppedLocked)
        {
            var media = _current!.Media;
            o["audio"] = VarList(_audio, TrackItems(media, "Audio"));
            o["spu"] = VarList(_spu, TrackItems(media, "Subtitle"));
            o["video"] = VarList(_video, TrackItems(media, "Video"));
            o["chapter"] = VarList(0, []);
            o["title"] = VarList(0, [(0, "Segment 0")]);
            o["stereo_mode"] = VarList(0, [(0, "Original"), (1, "Stereo")]);
            o["aspect"] = new JsonObject { ["current"] = "", ["items"] = new JsonArray(Named("", "Default"), Named("16:9", "16:9")) };
            o["crop"] = new JsonObject { ["current"] = "", ["items"] = new JsonArray(Named("", "Default"), Named("16:9", "16:9")) };
            o["zoom"] = VarList(1, [(1, "1:1 Original")]);
            o["deinterlace"] = VarList(-1, [(0, "Off"), (-1, "Automatic"), (1, "On")]);
        }
        return o.ToJsonString();

        static JsonObject Named(string id, string name) => new() { ["id"] = id, ["name"] = name };
    }

    private static List<(int Id, string Name)> TrackItems(FakeMedia media, string type) =>
        media.Tracks.Where(t => t.Type == type).Select(t => (Id: t.Id, Name: t.ListName)).Prepend((-1, "Disable")).ToList();

    private static JsonObject VarList(int current, IEnumerable<(int Id, string Name)> items)
    {
        var arr = new JsonArray();
        foreach (var (id, name) in items) arr.Add(new JsonObject { ["id"] = id, ["name"] = name });
        return new JsonObject { ["current"] = current, ["items"] = arr };
    }

    // ------------------------------------------------------------------ RC

    /// <summary>Opens an RC connection (<see cref="VlcBackend.RcConnect"/>); refused while VLC is not running.</summary>
    public Task<Stream> ConnectRcAsync(CancellationToken ct)
    {
        FakeRcConnection connection;
        lock (_gate)
        {
            if (_pid is null) throw new SocketException((int)SocketError.ConnectionRefused);
            connection = new FakeRcConnection();
            _rc.Add(connection);
            // On connect VLC prints the current input and state (see rc-adev.txt).
            if (!StoppedLocked)
                connection.Write($"status change: ( new input: {_current!.Media.Uri} )",
                    _state == "playing" ? "status change: ( play state: 3 )" : "status change: ( pause state: 4 )");
        }
        _ = Task.Run(() => ServeRcAsync(connection), CancellationToken.None);
        return Task.FromResult(connection.ClientStream);
    }

    private async Task ServeRcAsync(FakeRcConnection connection)
    {
        using var _ = connection;
        while (await connection.ReadCommandAsync() is { } line)
        {
            lock (_gate)
            {
                _rcCommands.Add(line);
                var word = line.Split(' ', 2)[0];
                switch (word)
                {
                    case "atrack":
                        connection.Write(TrackListLocked(word, "Audio", "Audio Track", _audio));
                        break;
                    case "strack":
                        connection.Write(TrackListLocked(word, "Subtitle", "Subtitle Track", _spu));
                        break;
                    case "vtrack":
                        connection.Write(TrackListLocked(word, "Video", "Video Track", _video));
                        break;
                    case "adev" when line.Length > "adev".Length:
                        connection.Write("adev: returned 0 (no error)");
                        break;
                    case "adev":
                        connection.Write("+----[ adev ]", "|  - Default *", "| {0.0.0.00000000}.{00000000-0000-0000-0000-000000000001} - TV (HDMI Audio)",
                            "+----[ end of adev ]", "adev: returned 0 (no error)");
                        break;
                    case "quit":
                        connection.Write("status change: ( quit )");
                        ExitLocked();
                        break;
                    default:
                        connection.Write($"Unknown command `{word}'. Type `help' for help.");
                        break;
                }
            }
        }
        lock (_gate) _rc.Remove(connection);
    }

    private string[] TrackListLocked(string command, string type, string title, int current)
    {
        if (StoppedLocked) return [$"{command}: returned -1 (generic error)"];
        if (_state == "paused") return ["Press pause to continue.", $"{command}: returned -1 (generic error)"];
        var lines = new List<string> { $"+----[ {title} ]" };
        foreach (var (id, name) in TrackItems(_current!.Media, type))
            lines.Add($"| {id.ToString(CultureInfo.InvariantCulture)} - {name}{(id == current ? " *" : "")}");
        lines.Add($"+----[ end of {title} ]");
        lines.Add($"{command}: returned 0 (no error)");
        return [.. lines];
    }

    private void EmitLocked(params string[] lines)
    {
        foreach (var c in _rc) c.Write(lines);
    }

    // ------------------------------------------------------------------ the process

    private FakeProcess? RunningLocked() => _pid is int id ? new FakeProcess(this, id, _startTime, _commandLine) : null;

    /// <summary>The companion starts <c>vlc.exe</c>: VLC gets the password and the file of its command line.</summary>
    private FakeProcess Launch(ProcessStartInfo startInfo)
    {
        var args = startInfo.ArgumentList.ToList();
        LaunchArgs = args;
        var password = args.SkipWhile(a => a != "--http-password").Skip(1).FirstOrDefault();
        var media = VlcCommandLine.ExtractMedia([startInfo.FileName, .. args]);
        lock (_gate)
        {
            FakeMedia[] files = media is null ? [] : [MediaFor(media)];
            StartProcess(password, DateTime.Now, files);
            _commandLine = string.Join(' ', args.Prepend(startInfo.FileName).Select(a => $"\"{a}\""));
            return RunningLocked()!;
        }
    }

    private void ExitIf(int pid)
    {
        lock (_gate)
            if (_pid == pid) ExitLocked();
    }

    private sealed class FakeProcesses(FakeVlc vlc) : IVlcProcesses
    {
        public IReadOnlyList<IVlcProcess> Running()
        {
            lock (vlc._gate) return vlc.RunningLocked() is { } p ? [p] : [];
        }

        public IVlcProcess? Start(ProcessStartInfo startInfo) => vlc.Launch(startInfo);
    }

    private sealed class FakeProcess(FakeVlc vlc, int id, DateTime startTime, string commandLine) : IVlcProcess
    {
        public int Id => id;
        public DateTime StartTime => startTime;
        public bool HasExited => vlc.Pid != id;
        public int ExitCode => 0;
        public void CloseMainWindow() => vlc.ExitIf(id);
        public void Kill() => vlc.ExitIf(id);
        public string? CommandLine() => commandLine;
        public void Dispose() { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var c in _rc) c.Close();
            _rc.Clear();
        }
    }
}
