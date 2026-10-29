using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Sofabird.Core.Bluetooth;
using Sofabird.Core.Protocol;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Radios;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using Timer = System.Threading.Timer;

namespace Sofabird.Companion.Net;

public enum BluetoothServerStatus
{
    /// <summary><c>bluetooth: false</c> in the settings.</summary>
    Disabled,
    Starting,
    /// <summary>The RFCOMM service is published in SDP and accepts connections.</summary>
    Advertising,
    /// <summary>The adapter exists but its radio is off (airplane mode, Bluetooth toggle).</summary>
    RadioOff,
    /// <summary>No Bluetooth Classic adapter (or the Bluetooth service is not running).</summary>
    NoAdapter,
    /// <summary>Creating the service failed; retried with backoff.</summary>
    Failed,
}

/// <summary>
/// Bluetooth Classic RFCOMM server (protocol/README.md, "Transport"): publishes service
/// <see cref="ProtocolConstants.BluetoothServiceUuid"/> named "Sofabird" in SDP and turns every accepted socket
/// into a regular <see cref="SessionManager"/> session over NDJSON (<see cref="RfcommLineTransport"/>).
/// </summary>
/// <remarks>
/// <para>
/// Works from the unpackaged, non-elevated companion (verified, docs/research/transport-and-companion.md §1a).
/// The PC is not made discoverable: an already paired phone connects by address.
/// </para>
/// <para>
/// Never throws to the caller: no adapter, radio off or a WinRT failure only change <see cref="Status"/>. The provider
/// and listener are re-created after resume from sleep (<see cref="SystemEvents.PowerModeChanged"/>) and when the radio
/// is turned back on (<see cref="Radio.StateChanged"/>); failures are retried with backoff (2 s … 5 min).
/// </para>
/// </remarks>
public sealed class BluetoothRfcommServer : IAsyncDisposable
{
    private const int MaxConnections = 4;
    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5),
    ];
    /// <summary>Poll interval while there is no adapter or the radio is off (in case an event is missed).</summary>
    private static readonly TimeSpan IdleRecheck = TimeSpan.FromMinutes(1);

    private readonly Settings _settings;
    private readonly SessionManager _sessions;
    private readonly ILogger _log;
    private readonly ILoggerFactory _lf;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<Task, byte> _connections = new();
    private Timer? _timer;
    private RfcommServiceProvider? _provider;
    private StreamSocketListener? _listener;
    private Radio? _radio;
    private int _failures;
    private int _active;
    private bool _started;
    private bool _disposed;

    public BluetoothRfcommServer(Settings settings, SessionManager sessions, ILoggerFactory lf)
    {
        _settings = settings;
        _sessions = sessions;
        _lf = lf;
        _log = lf.CreateLogger<BluetoothRfcommServer>();
        Status = settings.Bluetooth ? BluetoothServerStatus.Starting : BluetoothServerStatus.Disabled;
    }

    public BluetoothServerStatus Status { get; private set; }

    /// <summary><c>AA:BB:CC:DD:EE:FF</c> of the default adapter while its radio is on; otherwise <c>null</c>.</summary>
    public string? AdapterAddress { get; private set; }

    public bool IsAdvertising => Status == BluetoothServerStatus.Advertising;

    /// <summary>Raised from a background thread.</summary>
    public event Action? StatusChanged;

    /// <summary>Starts in the background; returns immediately. Call from the UI thread (SystemEvents needs a message loop).</summary>
    public void Start()
    {
        if (!_settings.Bluetooth)
        {
            _log.LogInformation("Bluetooth server disabled in settings");
            return;
        }
        lock (_lock)
        {
            if (_started || _disposed) return;
            _started = true;
        }
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Schedule(TimeSpan.Zero, "startup");
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        // The radio needs a moment after resume; the old SDP record / listener may be dead (Modern Standby).
        Interlocked.Exchange(ref _failures, 0);
        Schedule(TimeSpan.FromSeconds(5), "resume from sleep", forceRecreate: true);
    }

    private void OnRadioStateChanged(Radio sender, object args)
    {
        var state = SafeRadioState(sender);
        _log.LogInformation("Bluetooth radio state changed: {State}", state);
        var advertising = Status == BluetoothServerStatus.Advertising;
        if (state == RadioState.On && advertising) return; // spurious event, keep the working listener
        Interlocked.Exchange(ref _failures, 0);
        Schedule(TimeSpan.FromSeconds(state == RadioState.On ? 2 : 0), $"radio {state}", forceRecreate: true);
    }

    private static RadioState SafeRadioState(Radio radio)
    {
        try { return radio.State; }
        catch { return RadioState.Unknown; }
    }

    /// <summary>Debounced (re)start: a newer request replaces a pending one.</summary>
    private void Schedule(TimeSpan delay, string reason, bool forceRecreate = false)
    {
        lock (_lock)
        {
            if (_disposed) return;
            _timer?.Dispose();
            _timer = new Timer(_ => _ = RefreshAsync(reason, forceRecreate), null, delay, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task RefreshAsync(string reason, bool forceRecreate)
    {
        var ct = _cts.Token;
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        try
        {
            if (ct.IsCancellationRequested) return;
            if (!forceRecreate && Status == BluetoothServerStatus.Advertising) return;
            _log.LogDebug("Bluetooth refresh ({Reason})", reason);
            StopCore();
            await StartCoreAsync(reason, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            StopCore();
            var attempt = Interlocked.Increment(ref _failures);
            var delay = Backoff[Math.Min(attempt - 1, Backoff.Length - 1)];
            _log.LogWarning("Bluetooth RFCOMM service failed to start ({Reason}, attempt {Attempt}): {Error} (0x{HResult:X8}); retry in {Delay}",
                reason, attempt, ex.Message, ex.HResult, delay);
            SetStatus(BluetoothServerStatus.Failed, AdapterAddress);
            Schedule(delay, "retry");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StartCoreAsync(string reason, CancellationToken ct)
    {
        BluetoothAdapter? adapter;
        try
        {
            adapter = await BluetoothAdapter.GetDefaultAsync().AsTask(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // E.g. the Bluetooth Support Service is stopped.
            _log.LogDebug("BluetoothAdapter.GetDefaultAsync failed: {Error}", ex.Message);
            adapter = null;
        }
        if (adapter is null || !adapter.IsClassicSupported)
        {
            if (Status != BluetoothServerStatus.NoAdapter)
                _log.LogInformation("No Bluetooth Classic adapter; running without Bluetooth");
            SetStatus(BluetoothServerStatus.NoAdapter, null);
            Schedule(IdleRecheck, "adapter recheck");
            return;
        }

        var radio = await adapter.GetRadioAsync().AsTask(ct).ConfigureAwait(false);
        WatchRadio(radio);
        var state = radio is null ? RadioState.Unknown : SafeRadioState(radio);
        var address = BluetoothFormat.FormatAddress(adapter.BluetoothAddress);
        if (state != RadioState.On)
        {
            if (Status != BluetoothServerStatus.RadioOff)
                _log.LogInformation("Bluetooth radio of adapter {Address} is {State}; waiting for it to turn on", address, state);
            SetStatus(BluetoothServerStatus.RadioOff, null);
            Schedule(IdleRecheck, "radio recheck");
            return;
        }

        var protection = _settings.BluetoothRequireAuthentication
            ? SocketProtectionLevel.BluetoothEncryptionWithAuthentication
            : SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication;
        var provider = await RfcommServiceProvider.CreateAsync(RfcommServiceId.FromUuid(ProtocolConstants.BluetoothServiceUuid))
            .AsTask(ct).ConfigureAwait(false);
        var listener = new StreamSocketListener();
        try
        {
            listener.ConnectionReceived += OnConnectionReceived;
            await listener.BindServiceNameAsync(provider.ServiceId.AsString(), protection).AsTask(ct).ConfigureAwait(false);

            using (var writer = new DataWriter())
            {
                writer.WriteBytes(BluetoothFormat.EncodeSdpText(ProtocolConstants.BluetoothServiceName));
                provider.SdpRawAttributes.Add(BluetoothFormat.SdpServiceNameAttributeId, writer.DetachBuffer());
            }
            // radioDiscoverable: false — the phone is already paired and connects by address.
            provider.StartAdvertising(listener, radioDiscoverable: false);
        }
        catch
        {
            listener.ConnectionReceived -= OnConnectionReceived;
            listener.Dispose();
            try { provider.StopAdvertising(); } catch { /* ignore */ }
            throw;
        }
        _provider = provider;
        _listener = listener;
        Interlocked.Exchange(ref _failures, 0);
        _log.LogInformation("Bluetooth RFCOMM service {Uuid} \"{Name}\" advertising on adapter {Address} (protection {Protection}, {Reason})",
            ProtocolConstants.BluetoothServiceUuid, ProtocolConstants.BluetoothServiceName, address, protection, reason);
        SetStatus(BluetoothServerStatus.Advertising, address);
    }

    private void WatchRadio(Radio? radio)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_radio, radio)) return;
            if (_radio is not null) _radio.StateChanged -= OnRadioStateChanged;
            _radio = radio;
            if (_radio is not null && !_disposed) _radio.StateChanged += OnRadioStateChanged;
        }
    }

    private void StopCore()
    {
        var provider = _provider;
        var listener = _listener;
        _provider = null;
        _listener = null;
        if (provider is not null)
        {
            try { provider.StopAdvertising(); }
            catch (Exception ex) { _log.LogDebug("StopAdvertising failed: {Error}", ex.Message); }
        }
        if (listener is not null)
        {
            listener.ConnectionReceived -= OnConnectionReceived;
            try { listener.Dispose(); }
            catch (Exception ex) { _log.LogDebug("Listener dispose failed: {Error}", ex.Message); }
        }
        // Accepted sockets live on: their sessions end by themselves if the link is gone.
    }

    private void SetStatus(BluetoothServerStatus status, string? address)
    {
        var changed = Status != status || AdapterAddress != address;
        Status = status;
        AdapterAddress = address;
        if (!changed) return;
        try { StatusChanged?.Invoke(); }
        catch (Exception ex) { _log.LogWarning(ex, "StatusChanged handler failed"); }
    }

    private void OnConnectionReceived(StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
    {
        var socket = args.Socket;
        if (_cts.IsCancellationRequested)
        {
            socket.Dispose();
            return;
        }
        if (Interlocked.Increment(ref _active) > MaxConnections)
        {
            Interlocked.Decrement(ref _active);
            _log.LogWarning("Bluetooth connection from {Remote} rejected: {Max} connections already open",
                RemoteAddress(socket), MaxConnections);
            socket.Dispose();
            return;
        }
        var task = Task.Run(() => RunConnectionAsync(socket, _cts.Token));
        _connections[task] = 0;
        task.ContinueWith(t => _connections.TryRemove(t, out _), TaskScheduler.Default);
    }

    private async Task RunConnectionAsync(StreamSocket socket, CancellationToken ct)
    {
        var desc = "bt " + RemoteAddress(socket);
        try
        {
            desc = await DescribeAsync(socket).ConfigureAwait(false);
            _log.LogInformation("Bluetooth connected: {Desc}", desc);
            var transport = RfcommLineTransport.Create(socket, desc, _lf.CreateLogger(typeof(RfcommLineTransport)));
            await _sessions.RunSessionAsync(transport, ct).ConfigureAwait(false);
            _log.LogInformation("Bluetooth closed: {Desc}", desc);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Bluetooth connection {Desc} failed", desc);
            try { socket.Dispose(); } catch { /* ignore */ }
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    /// <summary><c>bt Pixel 8 (AA:BB:CC:DD:EE:FF)</c>; the name lookup is best effort.</summary>
    private async Task<string> DescribeAsync(StreamSocket socket)
    {
        var address = RemoteAddress(socket);
        try
        {
            var host = socket.Information.RemoteHostName;
            using var device = await BluetoothDevice.FromHostNameAsync(host).AsTask().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(device?.Name))
            {
                if (device.BluetoothAddress != 0) address = BluetoothFormat.FormatAddress(device.BluetoothAddress);
                return $"bt {device.Name} ({address})";
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug("Bluetooth device name lookup for {Address} failed: {Error}", address, ex.Message);
        }
        return "bt " + address;
    }

    private static string RemoteAddress(StreamSocket socket)
    {
        try
        {
            // Bluetooth host names look like "(02:1a:2b:3c:4d:60)".
            var raw = socket.Information.RemoteHostName?.RawName ?? socket.Information.RemoteAddress?.RawName ?? "?";
            return raw.Trim('(', ')', '{', '}').ToUpperInvariant();
        }
        catch
        {
            return "?";
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
            if (_radio is not null) _radio.StateChanged -= OnRadioStateChanged;
            _radio = null;
        }
        if (!_started) return;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _cts.Cancel();

        var gotGate = await _gate.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        try
        {
            StopCore();
        }
        finally
        {
            if (gotGate) _gate.Release();
        }
        if (Status != BluetoothServerStatus.Disabled) SetStatus(BluetoothServerStatus.Disabled, null);

        var pending = _connections.Keys.ToArray();
        if (pending.Length > 0)
        {
            try { await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch { /* sessions are cancelled; do not block shutdown */ }
        }
        _log.LogInformation("Bluetooth server stopped");
    }
}
