using Sofabird.Core.Protocol;

namespace Sofabird.Core.Streaming;

/// <summary>
/// The computers paired in Moonlight on this PC and their apps for the phone (<c>sys moonlightHosts</c>). Moonlight
/// keeps every computer's app list from the last time it saw it (<paramref name="paired"/>, its settings); a refresh
/// asks each computer again with <c>Moonlight.exe list</c> (<paramref name="listApps"/>: its output, or <c>null</c>
/// when it failed), all at once and each for at most <see cref="ListTimeout"/>. A computer that does not answer (off,
/// asleep) keeps the list Moonlight remembers and is marked stale; one that does keeps its fresh list until the
/// companion restarts, also for <c>moonlightStart</c> (Moonlight may not save it). Lists are "Desktop" first.
/// </summary>
public sealed class MoonlightCatalog(
    Func<IReadOnlyList<MoonlightHost>> paired,
    Func<string, CancellationToken, Task<string?>> listApps,
    TimeSpan? timeout = null)
{
    /// <summary>How long one computer may take to list its apps (Moonlight seeks an absent one for ~10 s).</summary>
    public static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(15);

    private readonly TimeSpan _timeout = timeout ?? ListTimeout;
    private readonly object _gate = new();
    private readonly Dictionary<string, IReadOnlyList<string>> _fetched = new(StringComparer.OrdinalIgnoreCase);
    private Task<IReadOnlyList<MoonlightHostEntry>>? _refresh;

    /// <summary>The paired computers with their apps: fetched by a refresh, else as Moonlight remembers them; Desktop first.</summary>
    public IReadOnlyList<MoonlightHost> Hosts()
    {
        var hosts = paired();
        lock (_gate)
        {
            return hosts.Select(h => new MoonlightHost(h.Name,
                MoonlightCommandLine.DesktopFirst(_fetched.TryGetValue(h.Name, out var apps) ? apps : h.Apps))).ToList();
        }
    }

    /// <summary>
    /// The computers for the phone; with <paramref name="refresh"/> their apps are asked for first (one refresh at a time:
    /// a second request waits for the running one; <paramref name="ct"/> only stops the waiting).
    /// </summary>
    public Task<IReadOnlyList<MoonlightHostEntry>> ListAsync(bool refresh, CancellationToken ct)
    {
        if (!refresh) return Task.FromResult<IReadOnlyList<MoonlightHostEntry>>(Hosts().Select(h => Entry(h, stale: false)).ToList());
        Task<IReadOnlyList<MoonlightHostEntry>> running;
        lock (_gate)
        {
            if (_refresh is null || _refresh.IsCompleted) _refresh = Task.Run(() => RefreshAsync(), CancellationToken.None);
            running = _refresh;
        }
        return running.WaitAsync(ct);
    }

    private async Task<IReadOnlyList<MoonlightHostEntry>> RefreshAsync()
    {
        var hosts = paired();
        var lists = await Task.WhenAll(hosts.Select(h => FetchAsync(h.Name))).ConfigureAwait(false);
        var entries = new List<MoonlightHostEntry>();
        lock (_gate)
        {
            for (var i = 0; i < hosts.Count; i++)
            {
                if (lists[i] is { } apps) _fetched[hosts[i].Name] = apps;
                var known = _fetched.TryGetValue(hosts[i].Name, out var fetched) ? fetched : hosts[i].Apps;
                entries.Add(Entry(new MoonlightHost(hosts[i].Name, MoonlightCommandLine.DesktopFirst(known)), stale: lists[i] is null));
            }
        }
        return entries;
    }

    /// <summary>One computer's apps from <c>Moonlight.exe list</c>, or <c>null</c> when it failed, timed out or printed no list.</summary>
    private async Task<IReadOnlyList<string>?> FetchAsync(string host)
    {
        using var cts = new CancellationTokenSource(_timeout);
        try
        {
            var output = await listApps(host, cts.Token).WaitAsync(cts.Token).ConfigureAwait(false);
            return output is null ? null : MoonlightCommandLine.ParseAppList(output);
        }
        catch (Exception)
        {
            return null; // timed out or failed (the runner logs why): the list Moonlight remembers stays
        }
    }

    private static MoonlightHostEntry Entry(MoonlightHost host, bool stale) => new()
    {
        Name = host.Name,
        Apps = host.Apps,
        Stale = stale ? true : null,
    };
}
