using Microsoft.Extensions.Logging;
using Sofabird.Core.Protocol;
using Sofabird.Core.Session;
using Message = Sofabird.Core.Protocol.Message;
using Sofabird.Core.State;

namespace Sofabird.Companion;

/// <summary>Active phone sessions; broadcasting of <c>state</c> (≤ 4/s through the coalescer) and <c>event</c>.</summary>
public sealed class SessionManager : IDisposable
{
    private readonly ILogger _log;
    private readonly ILoggerFactory _lf;
    private readonly ISessionHost _host;
    private readonly HashSet<Session> _sessions = new();
    private readonly object _lock = new();
    private readonly StateCoalescer _coalescer;
    private StateMessage? _lastState;

    public SessionManager(ISessionHost host, ILoggerFactory lf)
    {
        _host = host;
        _lf = lf;
        _log = lf.CreateLogger<SessionManager>();
        _coalescer = new StateCoalescer(BroadcastAsync);
    }

    public int Count { get { lock (_lock) return _sessions.Count; } }

    public event Action? CountChanged;

    /// <summary>Serve the transport until it is closed.</summary>
    public async Task RunSessionAsync(ILineTransport transport, CancellationToken ct)
    {
        var session = new Session(transport, _host, _lf.CreateLogger<Session>());
        session.Authenticated += OnAuthenticated;
        session.Closed += OnClosed;
        try
        {
            await session.RunAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void OnAuthenticated(Session s)
    {
        lock (_lock) _sessions.Add(s);
        _log.LogInformation("Session {Desc} joined ({Count} total)", s.Description, Count);
        CountChanged?.Invoke();
        var state = _lastState;
        if (state is not null)
            _ = s.SendAsync(state);
    }

    private void OnClosed(Session s)
    {
        bool removed;
        lock (_lock) removed = _sessions.Remove(s);
        if (removed)
        {
            _log.LogInformation("Session {Desc} left ({Count} total)", s.Description, Count);
            CountChanged?.Invoke();
        }
    }

    public void PublishState(StateMessage state)
    {
        _lastState = state;
        _coalescer.Publish(state);
    }

    public void PublishEvent(EventMessage ev) => _ = BroadcastAsync(ev);

    private Task BroadcastAsync(Message message)
    {
        Session[] targets;
        lock (_lock) targets = _sessions.ToArray();
        if (targets.Length == 0) return Task.CompletedTask;
        return Task.WhenAll(targets.Select(s => s.SendAsync(message)));
    }

    public async Task CloseAllAsync()
    {
        Session[] targets;
        lock (_lock) targets = _sessions.ToArray();
        foreach (var s in targets)
            await s.CloseAsync("shutdown").ConfigureAwait(false);
    }

    public void Dispose() => _coalescer.Dispose();
}
