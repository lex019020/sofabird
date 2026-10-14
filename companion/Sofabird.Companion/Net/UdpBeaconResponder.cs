using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Sofabird.Core.Protocol;

namespace Sofabird.Companion.Net;

/// <summary>
/// UDP beacon: the phone broadcasts <c>SOFABIRD_DISCOVER 1</c> to 47556, the companion answers
/// unicast JSON <c>{"t":"beacon",…}</c>.
/// </summary>
public sealed class UdpBeaconResponder : IAsyncDisposable
{
    private readonly IPAddress _bind;
    private readonly int _port;
    private readonly Func<BeaconMessage> _beacon;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _cts = new();
    private UdpClient? _udp;
    private Task? _loop;

    public UdpBeaconResponder(IPAddress bind, int port, Func<BeaconMessage> beacon, ILogger<UdpBeaconResponder> log)
    {
        _bind = bind;
        _port = port;
        _beacon = beacon;
        _log = log;
    }


    public void Start()
    {
        try
        {
            _udp = new UdpClient(AddressFamily.InterNetwork);
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.EnableBroadcast = true;
            _udp.Client.Bind(new IPEndPoint(_bind, _port));
            _loop = Task.Run(() => LoopAsync(_cts.Token));
            _log.LogInformation("UDP beacon listening on {Bind}:{Port}", _bind, _port);
        }
        catch (SocketException ex)
        {
            _log.LogWarning("UDP beacon failed to bind {Bind}:{Port}: {Error}", _bind, _port, ex.Message);
            _udp?.Dispose();
            _udp = null;
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var udp = _udp!;
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try
            {
                r = await udp.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException ex)
            {
                // An ICMP "port unreachable" for a previous reply arrives as a receive error — ignore it.
                if (ex.SocketErrorCode == SocketError.ConnectionReset) continue;
                _log.LogWarning("UDP beacon receive failed: {Error}", ex.Message);
                await Task.Delay(500, ct).ConfigureAwait(false);
                continue;
            }
            catch (ObjectDisposedException) { break; }

            string text;
            try { text = Encoding.UTF8.GetString(r.Buffer).Trim(); }
            catch { continue; }
            if (!text.StartsWith("SOFABIRD_DISCOVER", StringComparison.Ordinal)) continue;

            try
            {
                var json = ProtocolJson.Serialize(_beacon());
                var bytes = Encoding.UTF8.GetBytes(json);
                await udp.SendAsync(bytes, r.RemoteEndPoint, ct).ConfigureAwait(false);
                _log.LogDebug("Beacon reply → {Remote}", r.RemoteEndPoint);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogDebug(ex, "Beacon reply failed");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _udp?.Dispose();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { }
        }
        _cts.Dispose();
    }
}
