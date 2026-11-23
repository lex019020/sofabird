using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Sofabird.Core.YouTube.Cast;

namespace Sofabird.Companion.YouTube.Cast;

/// <summary>
/// The companion as a "screen" of YouTube's lounge service, the relay between the YouTube app on a phone and a TV: a
/// screen id (kept), its lounge token, a channel session and the long poll that streams what the phones send. Keeps
/// the session up — a new poll when one ends, a new session when YouTube forgets it, a new token when it is due,
/// retries further and further apart while the internet is down — and sends the screen's messages in order.
/// </summary>
public sealed partial class LoungeConnection : IAsyncDisposable
{
    /// <summary>YouTube sends a <c>noop</c> about every 30 s: a poll silent this long is dead.</summary>
    private static readonly TimeSpan Silence = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan[] Backoff =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30),
         TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)];
    /// <summary>The token says when to renew it (about 13 days); not later than this anyway.</summary>
    private static readonly TimeSpan MaxTokenAge = TimeSpan.FromDays(7);

    private readonly HttpClient _http;
    private readonly LoungeScreen _screen;
    private readonly Func<string?> _loadScreenId;
    private readonly Action<string?> _saveScreenId;
    private readonly Func<LoungeMessage, Task> _received;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private readonly LoungeChannel _channel;
    private readonly Channel<IReadOnlyList<LoungeCommand>> _outgoing =
        Channel.CreateUnbounded<IReadOnlyList<LoungeCommand>>(new UnboundedChannelOptions { SingleReader = true });
    private CancellationTokenSource? _stop;
    private CancellationTokenSource? _session;
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _loop;
    private Task? _sender;
    private string? _token;
    private DateTimeOffset _tokenDue;
    private bool _bound;

    /// <param name="loadScreenId">The screen id YouTube gave this PC before (<c>null</c> — none yet).</param>
    /// <param name="saveScreenId">Keeps a new screen id.</param>
    /// <param name="received">What the phones send (not the channel's own <c>c</c>/<c>S</c>/<c>noop</c>).</param>
    public LoungeConnection(HttpClient http, LoungeScreen screen, Func<string?> loadScreenId, Action<string?> saveScreenId,
        Func<LoungeMessage, Task> received, ILogger log)
    {
        _http = http;
        _screen = screen;
        _loadScreenId = loadScreenId;
        _saveScreenId = saveScreenId;
        _received = received;
        _log = log;
        _channel = new LoungeChannel(screen);
    }

    /// <summary>Connected to the lounge service: phones can cast now.</summary>
    public bool Bound
    {
        get { lock (_gate) return _bound; }
    }

    /// <summary><see cref="Bound"/> changed.</summary>
    public event Action? BoundChanged;

    /// <summary>A new channel session started (who is connected comes again with its <c>loungeStatus</c>).</summary>
    public event Action? SessionStarted;

    public void Start()
    {
        if (_stop is not null) return;
        _stop = new CancellationTokenSource();
        var ct = _stop.Token;
        _loop = Task.Run(() => RunAsync(ct), CancellationToken.None);
        _sender = Task.Run(() => SendLoopAsync(ct), CancellationToken.None);
    }

    /// <summary>Queues messages for the phones (dropped while not connected: they describe a state that will change).</summary>
    public void Send(IReadOnlyList<LoungeCommand> commands)
    {
        if (commands.Count > 0) _outgoing.Writer.TryWrite(commands);
    }

    /// <summary>A retry waiting for the internet to come back starts now (a phone is casting).</summary>
    public void Wake()
    {
        lock (_gate) _wake.TrySetResult();
    }

    /// <summary>
    /// Registers the pairing code a phone sent with its DIAL launch: the phone then joins this screen's lounge.
    /// </summary>
    public async Task RegisterPairingCodeAsync(string code, CancellationToken ct)
    {
        var screenId = await EnsureScreenIdAsync(ct).ConfigureAwait(false);
        var form = new Dictionary<string, string>
        {
            ["access_type"] = "permanent",
            ["app"] = LoungeScreen.App,
            ["pairing_code"] = code,
            ["screen_id"] = screenId,
            ["screen_name"] = _screen.Name,
            ["device_id"] = _screen.DeviceId,
        };
        using var response = await PostAsync(LoungeChannel.BaseUrl + "/pairing/register_pairing_code", form, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"register_pairing_code: {(int)response.StatusCode}", null, response.StatusCode);
        _log.LogInformation("YouTube cast: a phone's pairing code registered");
        Wake();
    }

    private async Task RunAsync(CancellationToken stop)
    {
        var failures = 0;
        while (!stop.IsCancellationRequested)
        {
            using var session = CancellationTokenSource.CreateLinkedTokenSource(stop);
            lock (_gate) _session = session;
            try
            {
                var token = await EnsureTokenAsync(stop).ConfigureAwait(false);
                await InitAsync(token, session.Token).ConfigureAwait(false);
                if (failures > 0) _log.LogInformation("YouTube cast: connected to YouTube again");
                failures = 0;
                SetBound(true);
                while (!session.IsCancellationRequested && DateTimeOffset.UtcNow < _tokenDue)
                {
                    var started = DateTimeOffset.UtcNow;
                    await PollAsync(token, session.Token).ConfigureAwait(false);
                    // A poll YouTube ends at once (it should hold it for minutes) is not repeated in a tight loop.
                    if (DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(2)) await Task.Delay(1000, session.Token).ConfigureAwait(false);
                }
                if (DateTimeOffset.UtcNow >= _tokenDue) _token = null; // renewed with the next session
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException) when (session.IsCancellationRequested)
            {
                // a send failed: a new session
            }
            catch (LoungeGoneException ex)
            {
                _log.LogDebug("YouTube cast: {Reason}; a new session", ex.Message);
                if (ex.TokenRejected) _token = null;
                if (++failures > 2)
                {
                    SetBound(false);
                    await WaitAsync(Backoff[Math.Min(failures - 3, Backoff.Length - 1)], stop).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or TaskCanceledException or InvalidDataException)
            {
                SetBound(false);
                var delay = Backoff[Math.Min(failures, Backoff.Length - 1)];
                if (failures == 0) _log.LogWarning("YouTube cast: no connection to YouTube ({Error}); retrying", ex.Message);
                else _log.LogDebug("YouTube cast: still no connection to YouTube ({Error}); next try in {Delay}", ex.Message, delay);
                failures++;
                await WaitAsync(delay, stop).ConfigureAwait(false);
            }
            finally
            {
                lock (_gate) _session = null;
            }
        }
        SetBound(false);
    }

    private async Task WaitAsync(TimeSpan delay, CancellationToken stop)
    {
        Task wake;
        lock (_gate)
        {
            if (_wake.Task.IsCompleted) _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            wake = _wake.Task;
        }
        try
        {
            await Task.WhenAny(Task.Delay(delay, stop), wake).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    private void SetBound(bool bound)
    {
        lock (_gate)
        {
            if (_bound == bound) return;
            _bound = bound;
        }
        try { BoundChanged?.Invoke(); }
        catch (Exception ex) { _log.LogWarning(ex, "YouTube cast: BoundChanged handler failed"); }
    }

    private async Task<string> EnsureScreenIdAsync(CancellationToken ct)
    {
        if (_loadScreenId() is { } known && ScreenIdPattern().IsMatch(known)) return known;
        using var timeout = Timeout(ct);
        var id = (await _http.GetStringAsync(LoungeChannel.BaseUrl + "/pairing/generate_screen_id", timeout.Token).ConfigureAwait(false)).Trim();
        if (!ScreenIdPattern().IsMatch(id)) throw new InvalidDataException("generate_screen_id: unexpected answer");
        _saveScreenId(id);
        _log.LogInformation("YouTube cast: this PC got its screen id");
        return id;
    }

    /// <summary>The lounge token of the screen; a screen id YouTube no longer knows is replaced once.</summary>
    private async Task<string> EnsureTokenAsync(CancellationToken ct)
    {
        if (_token is not null && DateTimeOffset.UtcNow < _tokenDue) return _token;
        for (var attempt = 0; ; attempt++)
        {
            var screenId = await EnsureScreenIdAsync(ct).ConfigureAwait(false);
            using var response = await PostAsync(LoungeChannel.BaseUrl + "/pairing/get_lounge_token_batch",
                new Dictionary<string, string> { ["screen_ids"] = screenId }, ct).ConfigureAwait(false);
            if ((int)response.StatusCode >= 500) throw new HttpRequestException($"get_lounge_token_batch: {(int)response.StatusCode}");
            var json = response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false) : "{}";
            if (ParseToken(json) is { } token)
            {
                _token = token.Token;
                _tokenDue = DateTimeOffset.UtcNow + (token.Refresh is { } r && r < MaxTokenAge ? r : MaxTokenAge);
                return token.Token;
            }
            if (attempt > 0) throw new InvalidDataException("get_lounge_token_batch: no token");
            _log.LogInformation("YouTube cast: the screen id was not accepted; asking for a new one");
            _saveScreenId(null);
        }
    }

    internal static (string Token, TimeSpan? Refresh)? ParseToken(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("screens", out var screens) || screens.ValueKind != JsonValueKind.Array
                || screens.GetArrayLength() == 0) return null;
            var s = screens[0];
            if (!s.TryGetProperty("loungeToken", out var t) || t.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(t.GetString())) return null;
            TimeSpan? refresh = s.TryGetProperty("refreshIntervalInMillis", out var r) && r.TryGetInt64(out var ms) && ms > 0
                ? TimeSpan.FromMilliseconds(ms) : null;
            return (t.GetString()!, refresh);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The first request of a session: its ids, and who is connected.</summary>
    private async Task InitAsync(string token, CancellationToken ct)
    {
        string url;
        lock (_gate)
        {
            _channel.Reset();
            url = _channel.InitUrl(token);
        }
        using var response = await PostAsync(url, new Dictionary<string, string> { ["count"] = "0" }, ct).ConfigureAwait(false);
        Check(response, "session");
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var messages = LoungeChunkReader.ParseAll(text);
        lock (_gate)
        {
            foreach (var m in messages) _channel.Observe(m);
            if (!_channel.Bound) throw new InvalidDataException("the session has no ids");
        }
        _log.LogDebug("YouTube cast: lounge session started");
        try { SessionStarted?.Invoke(); }
        catch (Exception ex) { _log.LogWarning(ex, "YouTube cast: SessionStarted handler failed"); }
        foreach (var m in messages) await DeliverAsync(m).ConfigureAwait(false);
    }

    /// <summary>One long poll: delivers what arrives until YouTube ends it (or it goes silent).</summary>
    private async Task PollAsync(string token, CancellationToken ct)
    {
        string url;
        lock (_gate) url = _channel.PollUrl(token);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var connect = Timeout(ct);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, connect.Token).ConfigureAwait(false);
        Check(response, "poll");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var chunks = new LoungeChunkReader();
        var buffer = new char[8192];
        while (true)
        {
            int n;
            using (var silence = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                silence.CancelAfter(Silence);
                try
                {
                    n = await reader.ReadAsync(buffer.AsMemory(), silence.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    _log.LogDebug("YouTube cast: the poll went silent; polling again");
                    return;
                }
            }
            if (n == 0) return;
            foreach (var m in chunks.Feed(new string(buffer, 0, n)))
            {
                lock (_gate) _channel.Observe(m);
                await DeliverAsync(m).ConfigureAwait(false);
            }
        }
    }

    private async Task DeliverAsync(LoungeMessage m)
    {
        if (m.Name is "c" or "S" or "noop") return;
        _log.LogDebug("YouTube cast: ← {Name}", m.Name);
        try
        {
            await _received(m).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "YouTube cast: handling {Name} failed", m.Name);
        }
    }

    private async Task SendLoopAsync(CancellationToken stop)
    {
        try
        {
            while (await _outgoing.Reader.WaitToReadAsync(stop).ConfigureAwait(false))
            {
                // Everything queued so far goes in one request, in order.
                var batch = new List<LoungeCommand>();
                while (batch.Count < 20 && _outgoing.Reader.TryRead(out var commands)) batch.AddRange(commands);
                if (!Bound || _token is not { } token) continue;
                try
                {
                    await SendAsync(token, batch, stop).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or LoungeGoneException
                    || (ex is TaskCanceledException && !stop.IsCancellationRequested))
                {
                    // The channel numbers what it sends: after a lost message only a new session is sure to work.
                    _log.LogDebug("YouTube cast: sending {Names} failed ({Error}); a new session", string.Join(", ", batch.Select(c => c.Name)), ex.Message);
                    lock (_gate) _session?.Cancel();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    private async Task SendAsync(string token, IReadOnlyList<LoungeCommand> commands, CancellationToken ct)
    {
        string url;
        IReadOnlyList<KeyValuePair<string, string>> form;
        lock (_gate)
        {
            if (!_channel.Bound) return;
            url = _channel.SendUrl(token);
            form = _channel.Form(commands);
        }
        using var response = await PostAsync(url, form, ct).ConfigureAwait(false);
        Check(response, "send");
        _log.LogDebug("YouTube cast: → {Names}", string.Join(", ", commands.Select(c => c.Name)));
    }

    private async Task<HttpResponseMessage> PostAsync(string url, IEnumerable<KeyValuePair<string, string>> form, CancellationToken ct)
    {
        using var timeout = Timeout(ct);
        using var content = new FormUrlEncodedContent(form);
        return await _http.PostAsync(url, content, timeout.Token).ConfigureAwait(false);
    }

    private static CancellationTokenSource Timeout(CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(RequestTimeout);
        return cts;
    }

    /// <summary>A refused request: the session (or the token) is gone, or YouTube is unwell (a retry later).</summary>
    private static void Check(HttpResponseMessage response, string what)
    {
        if (response.IsSuccessStatusCode) return;
        var code = (int)response.StatusCode;
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new LoungeGoneException($"{what}: {code}", tokenRejected: true);
        if (code is >= 400 and < 500) throw new LoungeGoneException($"{what}: {code}", tokenRejected: false);
        throw new HttpRequestException($"{what}: {code}", null, response.StatusCode);
    }

    public async ValueTask DisposeAsync()
    {
        if (_stop is null) return;
        // Phones still connected hear that the screen went away.
        if (Bound && _token is { } token)
        {
            try
            {
                using var bye = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await SendAsync(token, [new LoungeCommand("loungeScreenDisconnected")], bye.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or LoungeGoneException)
            {
                // best effort
            }
        }
        _stop.Cancel();
        _outgoing.Writer.TryComplete();
        foreach (var task in new[] { _loop, _sender })
        {
            if (task is null) continue;
            try { await task.ConfigureAwait(false); }
            catch (Exception ex) { _log.LogDebug(ex, "YouTube cast: stopping"); }
        }
        _stop.Dispose();
        _stop = null;
        SetBound(false);
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{10,128}$")]
    private static partial Regex ScreenIdPattern();

    private sealed class LoungeGoneException(string message, bool tokenRejected) : Exception(message)
    {
        public bool TokenRejected { get; } = tokenRejected;
    }
}
