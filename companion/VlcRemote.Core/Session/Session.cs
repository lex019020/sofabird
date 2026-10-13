using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VlcRemote.Core.Protocol;
using VlcRemote.Core.Vlc;

namespace VlcRemote.Core.Session;

/// <summary>
/// Одна сессия телефона поверх любого <see cref="ILineTransport"/>: рукопожатие
/// <c>hello</c> → <c>welcome</c>/<c>err</c>, затем диспетчеризация запросов. Исходящие
/// сообщения (<c>state</c>, <c>event</c>) шлются через <see cref="SendAsync"/> из любого потока.
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
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>Сессия прошла рукопожатие — можно слать <c>state</c>.</summary>
    public event Action<Session>? Authenticated;
    public event Action<Session>? Closed;

    /// <summary>Главный цикл; завершается при закрытии транспорта или ошибке рукопожатия.</summary>
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
                // Код истёк или попытки исчерпаны — показать окно с новым кодом (ограничено по частоте).
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

        try
        {
            Message? reply = msg switch
            {
                PingMessage p => new PongMessage { Id = p.Id },
                PongMessage => null,
                GetStateMessage g => WithId(await _host.GetStateAsync(ct).ConfigureAwait(false), g.Id),
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

    private static StateMessage WithId(StateMessage state, string? id)
    {
        state.Id = id;
        return state;
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
        reply.Id = s.Id;
        return reply;
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

    /// <summary>Отправить сообщение (потокобезопасно). Ошибка записи закрывает сессию.</summary>
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
