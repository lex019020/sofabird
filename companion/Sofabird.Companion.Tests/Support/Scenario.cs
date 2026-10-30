using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Sofabird.Companion.Tests.Fake;
using Sofabird.Companion.Vlc;
using Sofabird.Core.History;
using Sofabird.Core.Protocol;
using Sofabird.Core.Session;
using Xunit.Abstractions;

namespace Sofabird.Companion.Tests.Support;

/// <summary>
/// A real <see cref="VlcManager"/> (with the watch history, as in the companion) wired to a <see cref="FakeVlc"/>, and
/// a record of what the phones would get: published states, events, <see cref="VlcManager.ItemEnded"/>,
/// <see cref="VlcManager.NoControlDetected"/>. The poll loop is not started: tests poll explicitly
/// (<see cref="PollAsync"/>), so every step is deterministic. Nothing touches the real VLC, <c>%APPDATA%</c> or the
/// network.
/// </summary>
internal sealed class Scenario : IAsyncDisposable
{
    /// <summary>The companion's VLC HTTP password (<c>settings.json</c>, written into vlcrc).</summary>
    public const string Password = "companion-secret";

    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(60));
    private readonly List<StateMessage> _states = [];
    private readonly List<EventMessage> _events = [];
    private readonly List<int> _noControl = [];
    private readonly List<VlcProcessAppeared> _appeared = [];
    private int _itemEnded;

    public Scenario(ITestOutputHelper output)
    {
        Files = new FakeHistoryFileSystem();
        Store = new HistoryStore(@"X:\sofabird-tests\history.json", Clock, Files);
        var settings = new Settings
        {
            VlcHttpPassword = Password,
            LaunchFullscreen = false,
            // Never write the real %APPDATA%\vlc: no vlcrc changes, no Lua pages; the path is a fresh temp one anyway.
            ConfigureVlc = false,
            AutoInstallLuaPages = false,
            VlcrcPath = Path.Combine(Path.GetTempPath(), "sofabird-tests", Guid.NewGuid().ToString("N"), "vlcrc"),
        };
        Manager = new VlcManager(settings, new VlcInstallation(FakeVlc.ExePath, Path.GetDirectoryName(FakeVlc.ExePath)!, "3.0.23"),
            lua: null, new TestLoggerFactory(output), new HistoryTracker(Store),
            new VlcBackend(Vlc.Processes, Vlc.CreateHttpHandler(), Vlc.ConnectRcAsync));
        Manager.StateChanged += s => { lock (_states) _states.Add(s); };
        Manager.Event += e => { lock (_events) _events.Add(e); };
        Manager.ItemEnded += () => Interlocked.Increment(ref _itemEnded);
        Manager.NoControlDetected += pid => { lock (_noControl) _noControl.Add(pid); };
        Manager.ProcessAppeared += p => { lock (_appeared) _appeared.Add(p); };
    }

    public FakeVlc Vlc { get; } = new();
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 30, 20, 0, 0, TimeSpan.Zero));
    public FakeHistoryFileSystem Files { get; }
    public HistoryStore Store { get; }
    public VlcManager Manager { get; }
    public CancellationToken Ct => _timeout.Token;

    public IReadOnlyList<StateMessage> States { get { lock (_states) return _states.ToList(); } }
    public IReadOnlyList<EventMessage> Events { get { lock (_events) return _events.ToList(); } }
    public IReadOnlyList<string> EventNames => Events.Select(e => e.Name).ToList();
    public IReadOnlyList<int> NoControl { get { lock (_noControl) return _noControl.ToList(); } }
    public IReadOnlyList<VlcProcessAppeared> Appeared { get { lock (_appeared) return _appeared.ToList(); } }
    public int ItemEnded => Volatile.Read(ref _itemEnded);

    /// <summary>One poll of the manager (as its loop does every 0.5–2 s); returns the state the phones have now.</summary>
    public async Task<StateMessage> PollAsync()
    {
        await Manager.PollOnceAsync(Ct);
        return Manager.LastState ?? throw new InvalidOperationException("nothing was published");
    }

    /// <summary>A phone's <c>cmd</c> (<paramref name="args"/> as its JSON object).</summary>
    public Task CmdAsync(string name, object? args = null) =>
        Manager.ExecuteCommandAsync(name, new CommandArgs(JsonSerializer.SerializeToElement(args ?? new { })), Ct);

    /// <summary>Time passes: VLC plays on and the history's clock moves with it.</summary>
    public void Elapse(long seconds)
    {
        Vlc.Advance(seconds);
        Clock.Advance(TimeSpan.FromSeconds(seconds));
    }

    /// <summary>
    /// Waits until the RC connection is up and every <c>status change:</c> line VLC printed so far has been handled:
    /// an <c>adev</c> round trip, whose answer comes after them on the same connection.
    /// </summary>
    public Task RcSyncAsync() => Manager.GetAudioDevicesAsync(Ct);

    /// <summary>Forget the events seen so far.</summary>
    public void ClearEvents()
    {
        lock (_events) _events.Clear();
        Interlocked.Exchange(ref _itemEnded, 0);
    }

    public async ValueTask DisposeAsync()
    {
        await Manager.DisposeAsync();
        Store.Dispose();
        Vlc.Dispose();
        _timeout.Dispose();
    }
}
