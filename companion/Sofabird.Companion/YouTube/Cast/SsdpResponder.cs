using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Sofabird.Companion.Net;
using Sofabird.Core.YouTube.Cast;

namespace Sofabird.Companion.YouTube.Cast;

/// <summary>
/// Answers the YouTube app's SSDP searches for a DIAL device (UDP 239.255.255.250:1900) with where this PC's device
/// description is. The port is shared with Windows' own SSDP Discovery service (address reuse); the multicast group
/// is joined on every LAN interface, again when the network changes.
/// </summary>
public sealed class SsdpResponder : IDisposable
{
    private static readonly IPAddress Group = IPAddress.Parse(Dial.MulticastAddress);

    private readonly Func<IPAddress, string> _location;
    private readonly string _uuid;
    private readonly ILogger _log;
    private readonly HashSet<IPAddress> _joined = new();
    /// <summary>Who searched before: browsers search every few seconds, the log notes each searcher once.</summary>
    private readonly HashSet<IPAddress> _searchers = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _stop;
    private Socket? _socket;

    /// <param name="location">The device description's URL as seen through a local address.</param>
    /// <param name="uuid">The device's UUID (its UDN).</param>
    public SsdpResponder(Func<IPAddress, string> location, string uuid, ILogger log)
    {
        _location = location;
        _uuid = uuid;
        _log = log;
    }

    public void Start()
    {
        if (_stop is not null) return;
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, Dial.SsdpPort));
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            _log.LogWarning("YouTube cast: SSDP port {Port} is not available ({Error}); phones will not find this PC", Dial.SsdpPort, ex.Message);
            return;
        }
        _socket = socket;
        _stop = new CancellationTokenSource();
        Join();
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
        var ct = _stop.Token;
        _ = Task.Run(() => LoopAsync(socket, ct), CancellationToken.None);
        _log.LogInformation("YouTube cast: answering DIAL searches on {Group}:{Port}", Group, Dial.SsdpPort);
    }

    private void OnAddressChanged(object? sender, EventArgs e) => Join();

    /// <summary>Joins the group on interfaces not joined yet (a dropped interface takes its membership with it).</summary>
    private void Join()
    {
        lock (_gate)
        {
            if (_socket is null) return;
            foreach (var ip in NetworkUtil.GetLanAddresses())
            {
                if (_joined.Contains(ip)) continue;
                try
                {
                    _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group, ip));
                    _joined.Add(ip);
                }
                catch (SocketException ex)
                {
                    _log.LogDebug("YouTube cast: joining SSDP on {Address} failed: {Error}", ip, ex.Message);
                }
            }
        }
    }

    private async Task LoopAsync(Socket socket, CancellationToken ct)
    {
        var buffer = new byte[2048];
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult r;
            try
            {
                r = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex)
            {
                if (ex.SocketErrorCode == SocketError.ConnectionReset) continue;
                _log.LogDebug("YouTube cast: SSDP receive failed: {Error}", ex.Message);
                await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
                continue;
            }
            if (r.RemoteEndPoint is not IPEndPoint remote) continue;
            var st = Dial.SearchTarget(Encoding.UTF8.GetString(buffer, 0, r.ReceivedBytes));
            if (st is null) continue;
            await ReplyAsync(remote, st).ConfigureAwait(false);
        }
    }

    private async Task ReplyAsync(IPEndPoint remote, string st)
    {
        try
        {
            var local = LocalAddressFor(remote.Address);
            var bytes = Encoding.UTF8.GetBytes(Dial.SearchResponse(st, _location(local), _uuid));
            using var reply = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            reply.Bind(new IPEndPoint(local, 0));
            await reply.SendToAsync(bytes, SocketFlags.None, remote).ConfigureAwait(false);
            bool first;
            lock (_gate) first = _searchers.Add(remote.Address);
            if (first) _log.LogDebug("YouTube cast: answering DIAL searches from {Remote}", remote.Address);
        }
        catch (SocketException ex)
        {
            _log.LogDebug("YouTube cast: answering {Remote} failed: {Error}", remote, ex.Message);
        }
    }

    /// <summary>The address of this PC on the way to <paramref name="remote"/> (a UDP "connect" sends nothing).</summary>
    private static IPAddress LocalAddressFor(IPAddress remote)
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Connect(remote, 9);
        return ((IPEndPoint)probe.LocalEndPoint!).Address;
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        _stop?.Cancel();
        lock (_gate)
        {
            _socket?.Dispose();
            _socket = null;
            _joined.Clear();
        }
        _stop?.Dispose();
        _stop = null;
    }
}
