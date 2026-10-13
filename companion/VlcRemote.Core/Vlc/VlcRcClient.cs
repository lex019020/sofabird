using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace VlcRemote.Core.Vlc;

/// <summary>RC response to a single command.</summary>
public sealed class RcResponse
{
    public string Command { get; init; } = "";
    public IReadOnlyList<string> Lines { get; init; } = [];
    public int? ReturnCode { get; init; }
    public string? ReturnMessage { get; init; }
    public bool PausedRefusal { get; init; }

    public bool IsError => ReturnCode is int c && c != 0;
}

/// <summary>
/// The single TCP connection to VLC's RC interface (<c>--rc-host 127.0.0.1:4212</c>).
/// Commands run strictly one at a time; the response is matched by the line
/// <c>cmd: returned N (…)</c>, asynchronous <c>status change:</c> lines are raised as an event.
/// The transport is abstracted through a <see cref="Stream"/> factory for tests.
/// </summary>
public sealed class VlcRcClient : IAsyncDisposable
{
    /// <summary>Commands after which oldrc prints <c>cmd: returned …</c> (RegisterCallbacks).</summary>
    private static readonly HashSet<string> EchoedCommands = new(StringComparer.Ordinal)
    {
        "quit", "intf", "add", "repeat", "loop", "random", "enqueue", "playlist", "sort", "play",
        "stop", "clear", "prev", "next", "goto", "status", "pause", "seek", "title", "title_n",
        "title_p", "chapter", "chapter_n", "chapter_p", "fastforward", "rewind", "faster", "slower",
        "normal", "frame", "atrack", "vtrack", "strack", "volume", "volup", "voldown", "adev",
        "achan", "vratio", "vcrop", "vzoom", "snapshot", "stats", "f", "fullscreen",
    };

    private readonly Func<CancellationToken, Task<Stream>> _connect;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly object _pendingLock = new();

    private Stream? _stream;
    private StreamWriter? _writer;
    private CancellationTokenSource? _readCts;
    private Task? _readLoop;
    private Pending? _pending;

    public VlcRcClient(string host, int port, ILogger? logger = null)
        : this(async ct =>
        {
            var tcp = new TcpClient { NoDelay = true };
            await tcp.ConnectAsync(host, port, ct).ConfigureAwait(false);
            return tcp.GetStream();
        }, logger)
    {
        Host = host;
        Port = port;
    }

    public VlcRcClient(Func<CancellationToken, Task<Stream>> connect, ILogger? logger = null)
    {
        _connect = connect;
        _log = logger ?? NullLogger.Instance;
    }

    public string Host { get; } = "";
    public int Port { get; }
    public bool IsConnected => _stream is not null;

    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(3);
    /// <summary>Silence pause after which the response of a command without an echo is considered complete.</summary>
    public TimeSpan IdleCompletion { get; set; } = TimeSpan.FromMilliseconds(300);

    public event Action<RcStatusChange>? StatusChanged;
    public event Action<Exception?>? Disconnected;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_stream is not null) return;
        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_stream is not null) return;
            Stream stream;
            try
            {
                stream = await _connect(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                throw new VlcUnavailableException($"RC {Host}:{Port} unavailable: {ex.Message}", ex);
            }
            _stream = stream;
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            _readCts = new CancellationTokenSource();
            _readLoop = Task.Run(() => ReadLoopAsync(stream, _readCts.Token));
            _log.LogInformation("RC connected to {Host}:{Port}", Host, Port);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        Stream? stream;
        CancellationTokenSource? cts;
        Task? loop;
        lock (_pendingLock)
        {
            stream = _stream;
            cts = _readCts;
            loop = _readLoop;
            _stream = null;
            _writer = null;
            _readCts = null;
            _readLoop = null;
        }
        if (stream is null) return;
        cts?.Cancel();
        try { stream.Dispose(); } catch { /* ignore */ }
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); } catch { /* ignore */ }
        }
        cts?.Dispose();
    }

    /// <summary>Send a command and wait for the response. The command name must be exact (any command starting with "f" = fullscreen).</summary>
    public async Task<RcResponse> SendAsync(string command, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("empty command", nameof(command));
        command = command.Trim();
        if (command.Contains('\n') || command.Contains('\r')) throw new ArgumentException("multi-line command", nameof(command));
        var word = command.Split(' ', 2)[0];
        var expectEcho = EchoedCommands.Contains(word);

        await _commandGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ConnectAsync(ct).ConfigureAwait(false);
            var writer = _writer ?? throw new VlcUnavailableException("RC not connected");
            var pending = new Pending(word, expectEcho, IdleCompletion);
            lock (_pendingLock) _pending = pending;
            try
            {
                try
                {
                    await writer.WriteLineAsync(command.AsMemory(), ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    await DisconnectAsync().ConfigureAwait(false);
                    throw new VlcUnavailableException($"RC write failed: {ex.Message}", ex);
                }

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(timeout ?? DefaultTimeout);
                var completed = await Task.WhenAny(pending.Task, Task.Delay(Timeout.Infinite, timeoutCts.Token)).ConfigureAwait(false);
                if (completed != pending.Task)
                {
                    ct.ThrowIfCancellationRequested();
                    if (pending.Lines.Count > 0 || !expectEcho)
                    {
                        // A response without an echo or without a terminator — return what we have collected.
                        return pending.Snapshot();
                    }
                    throw new VlcRcException($"RC: no response to '{word}' within timeout");
                }
                return await pending.Task.ConfigureAwait(false);
            }
            finally
            {
                lock (_pendingLock) if (ReferenceEquals(_pending, pending)) _pending = null;
                pending.Dispose();
            }
        }
        finally
        {
            _commandGate.Release();
        }
    }

    public async Task<IReadOnlyList<RcListEntry>> ListAudioDevicesAsync(CancellationToken ct = default)
    {
        var r = await SendAsync("adev", ct).ConfigureAwait(false);
        return RcParser.ParseAudioDevices(r.Lines);
    }

    public async Task SetAudioDeviceAsync(string deviceId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(deviceId))
            throw new ArgumentException("RC adev cannot select the default device by empty id", nameof(deviceId));
        var r = await SendAsync("adev " + deviceId, ct).ConfigureAwait(false);
        if (r.IsError) throw new VlcRcException($"adev failed: {r.ReturnMessage}");
    }

    /// <summary>Track list with the current one marked; <c>null</c> if VLC is paused and RC refused.</summary>
    public async Task<IReadOnlyList<RcTrackEntry>?> ListTracksAsync(VlcTrackKind kind, CancellationToken ct = default)
    {
        var cmd = kind switch
        {
            VlcTrackKind.Audio => "atrack",
            VlcTrackKind.Subtitle => "strack",
            VlcTrackKind.Video => "vtrack",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var r = await SendAsync(cmd, ct).ConfigureAwait(false);
        if (r.PausedRefusal) return null;
        return RcParser.ParseTracks(r.Lines);
    }

    /// <summary>Close VLC. The echo may not arrive — the connection simply drops.</summary>
    public async Task QuitAsync(CancellationToken ct = default)
    {
        try
        {
            await SendAsync("quit", ct, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (VlcRcException)
        {
            // VLC closed without answering — that is normal.
        }
        catch (VlcUnavailableException)
        {
        }
    }

    private async Task ReadLoopAsync(Stream stream, CancellationToken ct)
    {
        Exception? error = null;
        try
        {
            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;
                line = line.TrimEnd('\r');
                if (line.Length == 0) continue;
                HandleLine(line);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            error = ex;
            _log.LogWarning(ex, "RC read loop ended");
        }

        Pending? pending;
        lock (_pendingLock)
        {
            pending = _pending;
            _pending = null;
            if (ReferenceEquals(_stream, stream))
            {
                _stream = null;
                _writer = null;
            }
        }
        pending?.Fail(new VlcUnavailableException("RC connection closed", error));
        if (!ct.IsCancellationRequested)
        {
            _log.LogInformation("RC disconnected");
            Disconnected?.Invoke(error);
        }
    }

    private void HandleLine(string line)
    {
        if (RcParser.IsStatusChange(line))
        {
            if (RcParser.TryParseStatusChange(line, out var change))
            {
                try { StatusChanged?.Invoke(change); }
                catch (Exception ex) { _log.LogWarning(ex, "RC StatusChanged handler failed"); }
            }
            return;
        }

        Pending? pending;
        lock (_pendingLock) pending = _pending;
        if (pending is null)
        {
            _log.LogDebug("RC stray line: {Line}", line);
            return;
        }
        pending.Feed(line);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _commandGate.Dispose();
        _connectGate.Dispose();
    }

    private sealed class Pending : IDisposable
    {
        private readonly TaskCompletionSource<RcResponse> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string _word;
        private readonly bool _expectEcho;
        private readonly TimeSpan _idle;
        private readonly Timer? _idleTimer;
        private readonly object _lock = new();
        private bool _paused;
        private int? _code;
        private string? _msg;

        public Pending(string word, bool expectEcho, TimeSpan idle)
        {
            _word = word;
            _expectEcho = expectEcho;
            _idle = idle;
            if (!expectEcho)
                _idleTimer = new Timer(_ => Complete(), null, Timeout.Infinite, Timeout.Infinite);
        }

        public List<string> Lines { get; } = new();
        public Task<RcResponse> Task => _tcs.Task;

        public void Feed(string line)
        {
            lock (_lock)
            {
                if (_tcs.Task.IsCompleted) return;
                if (RcParser.TryParseReturned(line, out var cmd, out var code, out var msg))
                {
                    if (string.Equals(cmd, _word, StringComparison.Ordinal) || !_expectEcho)
                    {
                        _code = code;
                        _msg = msg;
                        Complete();
                        return;
                    }
                }
                if (line.StartsWith("Press pause to continue", StringComparison.Ordinal) ||
                    line.StartsWith("Type 'pause' to continue", StringComparison.Ordinal))
                    _paused = true;
                if (line.StartsWith("Unknown command", StringComparison.Ordinal))
                {
                    Lines.Add(line);
                    _tcs.TrySetException(new VlcRcException(line));
                    return;
                }
                Lines.Add(line);
                if (!_expectEcho)
                    _idleTimer?.Change(_idle, Timeout.InfiniteTimeSpan);
            }
        }

        public RcResponse Snapshot()
        {
            lock (_lock)
                return new RcResponse
                {
                    Command = _word,
                    Lines = Lines.ToArray(),
                    ReturnCode = _code,
                    ReturnMessage = _msg,
                    PausedRefusal = _paused,
                };
        }

        private void Complete() => _tcs.TrySetResult(Snapshot());

        public void Fail(Exception ex) => _tcs.TrySetException(ex);

        public void Dispose() => _idleTimer?.Dispose();
    }
}
