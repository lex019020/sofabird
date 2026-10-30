using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sofabird.Core.Protocol;
using Sofabird.Core.Vlc;

namespace Sofabird.Core.Session;

/// <summary>
/// One phone session over any <see cref="ILineTransport"/>: the handshake
/// <c>hello</c> → <c>welcome</c>/<c>err</c>, then request dispatching. Outgoing
/// messages (<c>state</c>, <c>event</c>) are sent via <see cref="SendAsync"/> from any thread.
/// </summary>
public sealed class Session : IAsyncDisposable
{
    private readonly ILineTransport _transport;
    private readonly ISessionHost _host;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private int _closed;

    public Session(ILineTransport transport, ISessionHost host, ILogger? logger = null)
    {
        _transport = transport;
        _host = host;
        _log = logger ?? NullLogger.Instance;
    }

    public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public string Description => _transport.Description;
    public bool IsAuthenticated { get; private set; }
    public DeviceInfo? Device { get; private set; }

    /// <summary>The session has completed the handshake — <c>state</c> can be sent.</summary>
    public event Action<Session>? Authenticated;
    public event Action<Session>? Closed;

    /// <summary>Main loop; ends when the transport closes or the handshake fails.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var token = linked.Token;
        try
        {
            if (!await HandshakeAsync(token).ConfigureAwait(false))
                return;

            while (!token.IsCancellationRequested)
            {
                var line = await _transport.ReadLineAsync(token).ConfigureAwait(false);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                await HandleLineAsync(line, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Session {Desc} failed", Description);
        }
        finally
        {
            await CloseAsync("bye").ConfigureAwait(false);
        }
    }

    private async Task<bool> HandshakeAsync(CancellationToken ct)
    {
        using var helloCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        helloCts.CancelAfter(HelloTimeout);
        string? line;
        try
        {
            do
            {
                line = await _transport.ReadLineAsync(helloCts.Token).ConfigureAwait(false);
                if (line is null) return false;
            } while (string.IsNullOrWhiteSpace(line));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await SendAsync(new ErrMessage { Code = ErrorCodes.Unauthorized, Msg = "hello timeout" }, CancellationToken.None).ConfigureAwait(false);
            return false;
        }

        Message? msg;
        try
        {
            msg = ProtocolJson.Deserialize(line);
        }
        catch (JsonException ex)
        {
            await SendAsync(new ErrMessage { Code = ErrorCodes.BadRequest, Msg = "invalid JSON: " + ex.Message }, ct).ConfigureAwait(false);
            return false;
        }

        if (msg is not HelloMessage hello)
        {
            await SendAsync(new ErrMessage { Id = msg?.Id, Code = ErrorCodes.Unauthorized, Msg = "hello expected" }, ct).ConfigureAwait(false);
            return false;
        }

        if (hello.V != ProtocolConstants.Version)
        {
            await SendAsync(new ErrMessage { Id = hello.Id, Code = ErrorCodes.Unsupported, Msg = $"protocol version {hello.V} is not supported (expected {ProtocolConstants.Version})" }, ct).ConfigureAwait(false);
            return false;
        }

        Device = hello.Device;
        Culture = Localization.LocalizableText.CultureFromTag(hello.Device?.Locale);
        string? newToken = null;
        var pairing = _host.Pairing;
        if (!string.IsNullOrEmpty(hello.PairCode))
        {
            var result = pairing.TryPair(hello.PairCode, hello.Device?.Name, hello.Device?.Id);
            if (!result.Success)
            {
                _log.LogWarning("Pairing failed for {Device}: {Reason}", hello.Device?.Name, result.Reason);
                // The code has expired or the attempts are used up — show the window with a new code (rate limited).
                if (!pairing.IsPairingActive) RequestPairing(hello.Device);
                await SendAsync(new ErrMessage { Id = hello.Id, Code = ErrorCodes.Unauthorized, Msg = result.Reason ?? "pairing failed" }, ct).ConfigureAwait(false);
                return false;
            }
            newToken = result.Token;
            _log.LogInformation("Paired new device {Device} ({Id})", hello.Device?.Name, hello.Device?.Id);
        }
        else if (!pairing.ValidateToken(hello.Token))
        {
            _log.LogInformation("Device {Device} is not paired (token {TokenState}); asking the host to show the pairing code",
                hello.Device?.Name ?? "?", string.IsNullOrEmpty(hello.Token) ? "missing" : "unknown");
            RequestPairing(hello.Device);
            await SendAsync(new ErrMessage { Id = hello.Id, Code = ErrorCodes.PairingRequired, Msg = "pairing required" }, ct).ConfigureAwait(false);
            return false;
        }

        IsAuthenticated = true;
        await SendAsync(new WelcomeMessage
        {
            Id = _host.InstallationId,
            Name = _host.MachineName,
            Caps = _host.Caps,
            Token = newToken,
            Vlc = _host.VlcInfo,
            Bt = _host.BluetoothAddress,
            Macs = _host.WakeMacs,
            Tls = _host.Tls,
        }, ct).ConfigureAwait(false);
        _log.LogInformation("Session {Desc} authenticated as {Device}", Description, hello.Device?.Name ?? "?");
        try { Authenticated?.Invoke(this); }
        catch (Exception ex) { _log.LogWarning(ex, "Authenticated handler failed"); }
        return true;
    }

    private void RequestPairing(DeviceInfo? device)
    {
        try { _host.RequestPairing(device); }
        catch (Exception ex) { _log.LogWarning(ex, "RequestPairing failed"); }
    }

    private async Task HandleLineAsync(string line, CancellationToken ct)
    {
        Message? msg;
        try
        {
            msg = ProtocolJson.Deserialize(line);
        }
        catch (JsonException ex)
        {
            await SendAsync(new ErrMessage { Code = ErrorCodes.BadRequest, Msg = "invalid message: " + ex.Message }, ct).ConfigureAwait(false);
            return;
        }
        if (msg is null) return;
        if (msg is SysMessage { Name: var name } && ConcurrentSys.Contains(name))
        {
            _ = Task.Run(() => DispatchBesideAsync(msg, ct), CancellationToken.None);
            return;
        }
        await DispatchAsync(msg, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>sys</c> requests that only read and may take long (<c>moonlightHosts</c> asks each Moonlight computer for its
    /// apps, up to ~15 s): they run beside the session's other messages, so that pings, input and commands do not wait
    /// for them (a late <c>pong</c> would make the phone reconnect). Their reply comes whenever they are done.
    /// </summary>
    private static readonly HashSet<string> ConcurrentSys = new(StringComparer.Ordinal) { "moonlightHosts" };

    private async Task DispatchBesideAsync(Message msg, CancellationToken ct)
    {
        try
        {
            await DispatchAsync(msg, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The session ended meanwhile: nobody to answer.
        }
    }

    /// <summary>One request: its reply, or an <c>err</c> with its id.</summary>
    private async Task DispatchAsync(Message msg, CancellationToken ct)
    {
        try
        {
            Message? reply = msg switch
            {
                PingMessage p => new PongMessage { Id = p.Id },
                PongMessage => null,
                InputMessage i => Input(i),
                GetStateMessage g => (await _host.GetStateAsync(ct).ConfigureAwait(false)).WithId(g.Id),
                CmdMessage c => await RunCommandAsync(c, ct).ConfigureAwait(false),
                SysMessage s => await RunSysAsync(s, ct).ConfigureAwait(false),
                BrowseMessage b => new EntriesMessage
                {
                    Id = b.Id,
                    Uri = b.Uri,
                    Items = await _host.BrowseAsync(string.IsNullOrEmpty(b.Uri) ? "file:///" : b.Uri, ct).ConfigureAwait(false),
                },
                GetPlaylistMessage gp => new PlaylistMessage { Id = gp.Id, Items = await _host.GetPlaylistAsync(ct).ConfigureAwait(false) },
                GetTracksMessage gt => TracksMessage.From(await _host.GetTracksAsync(ct).ConfigureAwait(false), gt.Id),
                GetAudioDevicesMessage ga => new AudioDevicesMessage { Id = ga.Id, Items = await _host.GetAudioDevicesAsync(ct).ConfigureAwait(false) },
                GetHistoryMessage gh => await GetHistoryAsync(gh, ct).ConfigureAwait(false),
                HistoryUpdateMessage hu => await UpdateHistoryAsync(hu, ct).ConfigureAwait(false),
                HistoryClearMessage hc => await ClearHistoryAsync(hc, ct).ConfigureAwait(false),
                GetLibraryMessage gl => WithId(await _host.GetLibraryAsync(ct).ConfigureAwait(false), gl.Id),
                GetShowMessage gs => await GetShowAsync(gs, ct).ConfigureAwait(false),
                HelloMessage h => new ErrMessage { Id = h.Id, Code = ErrorCodes.BadRequest, Msg = "already authenticated" },
                _ => new ErrMessage { Id = msg.Id, Code = ErrorCodes.BadRequest, Msg = $"unexpected message type for this direction" },
            };
            if (reply is not null)
                await SendAsync(reply, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var err = MapError(ex, msg.Id, Culture);
            if (err.Code == ErrorCodes.Internal)
                _log.LogError(ex, "Request {Type} failed", msg.GetType().Name);
            else
                _log.LogDebug("Request {Type} → {Code}: {Msg}", msg.GetType().Name, err.Code, err.Msg);
            await SendAsync(err, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Touchpad and keyboard: no reply (dozens a second); a failure only goes to the log.</summary>
    private Message? Input(InputMessage input)
    {
        try
        {
            _host.Input(input);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Input {Kind} failed", input.Kind);
        }
        return null;
    }

    private async Task<Message> RunCommandAsync(CmdMessage c, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(c.Name)) throw ProtocolException.BadRequest("cmd.name is required");
        await _host.ExecuteCommandAsync(c.Name, new CommandArgs(c.Args), ct).ConfigureAwait(false);
        return new OkMessage { Id = c.Id };
    }

    private async Task<Message> RunSysAsync(SysMessage s, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.Name)) throw ProtocolException.BadRequest("sys.name is required");
        var reply = await _host.ExecuteSysAsync(s.Name, new CommandArgs(s.Args), ct).ConfigureAwait(false);
        if (reply is null) return new OkMessage { Id = s.Id };
        if (reply is DisplaysMessage displays) displays.Localize(Culture);
        reply.Id = s.Id;
        return reply;
    }

    /// <summary>Default and maximum page size of <c>getHistory</c>.</summary>
    public const int DefaultHistoryLimit = 50;
    public const int MaxHistoryLimit = 500;

    private async Task<Message> GetHistoryAsync(GetHistoryMessage m, CancellationToken ct)
    {
        var filter = string.IsNullOrEmpty(m.Filter) ? "all" : m.Filter;
        if (filter is not ("continue" or "all" or "youtube")) throw ProtocolException.BadRequest("getHistory.filter must be continue|all|youtube");
        var limit = Math.Clamp(m.Limit ?? DefaultHistoryLimit, 0, MaxHistoryLimit);
        var offset = Math.Max(0, m.Offset ?? 0);
        var reply = await _host.GetHistoryAsync(filter, limit, offset, ct).ConfigureAwait(false);
        reply.Id = m.Id;
        return reply;
    }

    private async Task<Message> UpdateHistoryAsync(HistoryUpdateMessage m, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(m.Uri)) throw ProtocolException.BadRequest("historyUpdate.uri is required");
        var remove = m.Remove == true;
        if (!remove && m.Finished is null) throw ProtocolException.BadRequest("historyUpdate requires 'finished' or 'remove'");
        await _host.UpdateHistoryAsync(m.Uri, m.Finished, remove, ct).ConfigureAwait(false);
        return new OkMessage { Id = m.Id };
    }

    private async Task<Message> GetShowAsync(GetShowMessage m, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(m.Key)) throw ProtocolException.BadRequest("getShow.key is required");
        var reply = await _host.GetShowAsync(m.Key, ct).ConfigureAwait(false)
            ?? throw ProtocolException.BadRequest($"no series {m.Key} in the library");
        return WithId(reply, m.Id);
    }

    private static Message WithId(Message reply, string? id)
    {
        reply.Id = id;
        return reply;
    }

    private async Task<Message> ClearHistoryAsync(HistoryClearMessage m, CancellationToken ct)
    {
        await _host.ClearHistoryAsync(ct).ConfigureAwait(false);
        return new OkMessage { Id = m.Id };
    }

    /// <summary>Language of the phone (<c>hello.device.locale</c>) for <c>err.msg</c>; null = English.</summary>
    public System.Globalization.CultureInfo? Culture { get; private set; }

    public static ErrMessage MapError(Exception ex, string? id, System.Globalization.CultureInfo? culture = null) => ex switch
    {
        ProtocolException pe => new ErrMessage { Id = id, Code = pe.Code, Msg = pe.MessageFor(culture) },
        VlcUnavailableException ve => new ErrMessage { Id = id, Code = ErrorCodes.VlcNotRunning, Msg = ve.Message },
        VlcException ve => new ErrMessage { Id = id, Code = ErrorCodes.VlcError, Msg = ve.Message },
        ArgumentException ae => new ErrMessage { Id = id, Code = ErrorCodes.BadRequest, Msg = ae.Message },
        NotSupportedException ns => new ErrMessage { Id = id, Code = ErrorCodes.Unsupported, Msg = ns.Message },
        TimeoutException te => new ErrMessage { Id = id, Code = ErrorCodes.VlcError, Msg = te.Message },
        _ => new ErrMessage { Id = id, Code = ErrorCodes.Internal, Msg = ex.Message },
    };

    /// <summary>Send a message (thread-safe). A write error closes the session.</summary>
    public async Task SendAsync(Message message, CancellationToken ct = default)
    {
        if (Volatile.Read(ref _closed) != 0) return;
        var line = ProtocolJson.Serialize(message);
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _transport.WriteLineAsync(line, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Send to {Desc} failed; closing", Description);
            _cts.Cancel();
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async Task CloseAsync(string? reason = null)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _cts.Cancel();
        try { await _transport.CloseAsync(reason, CancellationToken.None).ConfigureAwait(false); }
        catch { /* ignore */ }
        try { Closed?.Invoke(this); }
        catch (Exception ex) { _log.LogWarning(ex, "Closed handler failed"); }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
        _sendGate.Dispose();
    }
}
