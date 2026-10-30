using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Sofabird.Companion.Interop;
using Sofabird.Core.Localization;
using Sofabird.Core.Protocol;
using Sofabird.Core.Session;
using Sofabird.Core.Streaming;

namespace Sofabird.Companion.Services;

/// <summary>
/// Shows another PC's screen on the TV through Moonlight (installed and paired with a Sunshine host by the user):
/// <c>Moonlight.exe stream &lt;host&gt; &lt;app&gt;</c> fullscreen, ended by <c>Moonlight.exe quit &lt;host&gt;</c>.
/// The paired computers and their apps come from Moonlight's settings, refreshed on request with
/// <c>Moonlight.exe list &lt;host&gt; --csv</c> (<see cref="MoonlightCatalog"/>). Streams started by hand in Moonlight
/// are seen and can be ended too.
/// </summary>
public sealed class MoonlightService : IDisposable
{
    private const string ProcessName = "Moonlight";
    private const string HostsKey = @"Software\Moonlight Game Streaming Project\Moonlight\hosts";
    private static readonly TimeSpan StartCheck = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan QuitTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FrontTimeout = TimeSpan.FromSeconds(30);
    private const string StreamWindowClass = "SDL_app";

    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly MoonlightCatalog _catalog;
    private Process? _stream; // the last stream we started (disposed when the next one starts)

    public MoonlightService(ILogger<MoonlightService> log)
    {
        _log = log;
        ExePath = Locate();
        _catalog = new MoonlightCatalog(PairedHosts, ListAppsAsync);
        if (ExePath is not null) _log.LogInformation("Moonlight at {Path}", ExePath);
    }

    /// <summary>Moonlight.exe, or <c>null</c> when it is not installed (the phone then hides the feature).</summary>
    public string? ExePath { get; }

    /// <summary>A stream started or ended (phones get the new state).</summary>
    public event Action? Changed;

    private static string? Locate()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Moonlight Game Streaming", "Moonlight.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Moonlight Game Streaming", "Moonlight.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Moonlight Game Streaming", "Moonlight.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// The paired computers with their apps, Desktop first (as a refresh fetched them, else as Moonlight remembers them).
    /// </summary>
    public IReadOnlyList<MoonlightHost> Hosts() => _catalog.Hosts();

    /// <summary><c>sys moonlightHosts</c>: the paired computers; with <paramref name="refresh"/> their apps are asked for first.</summary>
    public Task<IReadOnlyList<MoonlightHostEntry>> HostsAsync(bool refresh, CancellationToken ct)
    {
        if (ExePath is null) throw ProtocolException.Unsupported(ProtocolText.MoonlightNotInstalled);
        return _catalog.ListAsync(refresh, ct);
    }

    /// <summary>
    /// Paired hosts from Moonlight's own settings (Qt keeps them under HKCU: <c>hosts\N\hostname</c>,
    /// <c>apps\M\name</c>); apps hidden in Moonlight (<c>apps\M\hidden</c>) are left out.
    /// </summary>
    private List<MoonlightHost> PairedHosts()
    {
        try
        {
            using var hosts = Registry.CurrentUser.OpenSubKey(HostsKey);
            if (hosts is null) return [];
            var list = new List<MoonlightHost>();
            foreach (var index in Indexes(hosts))
            {
                using var host = hosts.OpenSubKey(index);
                if (host?.GetValue("hostname") is not string name || name.Length == 0) continue;
                var apps = new List<string>();
                using (var appsKey = host.OpenSubKey("apps"))
                {
                    if (appsKey is not null)
                    {
                        foreach (var appIndex in Indexes(appsKey))
                        {
                            using var app = appsKey.OpenSubKey(appIndex);
                            if (app?.GetValue("name") is string appName && appName.Length > 0
                                && !MoonlightCommandLine.IsTrue(app.GetValue("hidden")))
                                apps.Add(appName);
                        }
                    }
                }
                list.Add(new MoonlightHost(name, apps));
            }
            return list;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            _log.LogDebug(ex, "Reading Moonlight's hosts failed");
            return [];
        }
    }

    /// <summary>Qt arrays: numbered subkeys 1..size.</summary>
    private static IEnumerable<string> Indexes(RegistryKey key) =>
        key.GetSubKeyNames().Where(n => int.TryParse(n, out _)).OrderBy(n => int.Parse(n, System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// <c>Moonlight.exe list &lt;host&gt; --csv</c>: Moonlight-qt asks the host for its apps and prints them (no window);
    /// its output, or <c>null</c> when it failed. Cancelled (<see cref="MoonlightCatalog.ListTimeout"/>: a host that is
    /// off keeps Moonlight seeking it), the process is stopped.
    /// </summary>
    private async Task<string?> ListAppsAsync(string host, CancellationToken ct)
    {
        if (ExePath is not { } exe) return null;
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Qt 6 writes UTF-8; anything else shows up as U+FFFD and the remembered list is kept.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        foreach (var a in MoonlightCommandLine.List(host)) psi.ArgumentList.Add(a);
        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _log.LogWarning(ex, "Moonlight: listing the apps of {Host} did not start", host);
            return null;
        }
        if (process is null) return null;
        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
            try
            {
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _log.LogInformation("Moonlight: {Host} did not list its apps in time; keeping the apps Moonlight remembers", host);
                TryKillTree(process);
                await DrainAsync(output, errors).ConfigureAwait(false);
                return null;
            }
            var (stdout, stderr) = await DrainAsync(output, errors).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                _log.LogInformation("Moonlight: listing the apps of {Host} failed (exit code {Code}): {Error}",
                    host, process.ExitCode, Shorten(stderr.Length > 0 ? stderr : stdout));
                return null;
            }
            _log.LogInformation("Moonlight: {Host} listed its apps", host);
            return stdout;
        }
    }

    /// <summary>What the process printed, once its pipes are closed (at most a couple of seconds after it ended).</summary>
    private static async Task<(string Out, string Err)> DrainAsync(Task<string> output, Task<string> errors)
    {
        try
        {
            await Task.WhenAll(output, errors).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            return (output.Result, errors.Result);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            return ("", "");
        }
    }

    private static string Shorten(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= 300 ? line : line[..300] + "â€¦";
    }

    private void TryKillTree(Process p)
    {
        try { p.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
            or NotSupportedException or AggregateException)
        {
            _log.LogDebug(ex, "Stopping Moonlight {Pid} failed", p.Id);
        }
    }

    /// <summary>Moves the stream window to the TV (set by the host: the monitor VLC opens on); no-op there.</summary>
    public Action<IntPtr>? PlaceOnTv { get; set; }

    /// <summary>The stream window on the screen (of any Moonlight process, also one started by hand), or zero.</summary>
    public static IntPtr StreamWindow()
    {
        foreach (var p in Process.GetProcessesByName(ProcessName))
        {
            using (p)
            {
                foreach (var h in ForegroundWindow.WindowsOf(p.Id))
                    if (ForegroundWindow.ClassName(h) == StreamWindowClass) return h;
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>The host currently streamed to this PC (by any Moonlight process with a <c>stream</c> command line), or <c>null</c>.</summary>
    public static string? StreamingHost()
    {
        foreach (var p in Process.GetProcessesByName(ProcessName))
        {
            using (p)
            {
                if (ProcessCommandLine.TryGet(p.Id) is { } cmd
                    && MoonlightCommandLine.StreamingHost(ProcessCommandLine.Split(cmd)) is { } host)
                    return host;
            }
        }
        return null;
    }

    /// <summary>Starts streaming <paramref name="app"/> (default "Desktop") from <paramref name="host"/> (default the first paired one).</summary>
    public async Task StartAsync(string? host, string? app, CancellationToken ct)
    {
        var exe = ExePath ?? throw ProtocolException.Unsupported(ProtocolText.MoonlightNotInstalled);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (StreamingHost() is { } streaming)
            {
                _log.LogInformation("Moonlight is already streaming from {Host}", streaming);
                return;
            }
            var chosen = MoonlightCommandLine.ChooseHost(Hosts(), host) ?? throw ProtocolException.BadRequest(ProtocolText.MoonlightNoHost);
            var appName = MoonlightCommandLine.ChooseApp(chosen, app);
            // Moonlight's own window would be left on the TV behind the stream (and in front of VLC afterwards).
            await CloseAllAsync(ct).ConfigureAwait(false);

            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
            foreach (var a in MoonlightCommandLine.Stream(chosen.Name, appName)) psi.ArgumentList.Add(a);
            _log.LogInformation("Moonlight: streaming {App} from {Host}", appName, chosen.Name);
            var process = Process.Start(psi) ?? throw ProtocolException.BadRequest(ProtocolText.MoonlightStartFailed("-"));
            _stream?.Dispose();
            _stream = process;
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                _log.LogInformation("Moonlight stream ended (exit code {Code})", SafeExitCode(process));
                Changed?.Invoke();
            };
            using (var check = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                check.CancelAfter(StartCheck);
                try { await process.WaitForExitAsync(check.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { } // still running: the stream is up
            }
            if (process.HasExited && process.ExitCode != 0)
                throw ProtocolException.BadRequest(ProtocolText.MoonlightStartFailed(process.ExitCode));
            _ = Task.Run(() => BringStreamToFrontAsync(process), CancellationToken.None); // outlives the request
            Changed?.Invoke();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The companion runs in the background, so Windows opens Moonlight's stream window behind VLC (and SDL minimises a
    /// fullscreen window without focus), on the monitor Moonlight was last on. Waits up to <see cref="FrontTimeout"/> for
    /// the stream window, moves it to the TV and brings it to the front, restored; it is checked a few times, as
    /// Moonlight may still settle its mode right after showing it.
    /// </summary>
    private async Task BringStreamToFrontAsync(Process process)
    {
        var deadline = DateTime.UtcNow + FrontTimeout;
        var inFront = 0;
        try
        {
            while (DateTime.UtcNow < deadline && !process.HasExited)
            {
                var windows = ForegroundWindow.WindowsOf(process.Id);
                var stream = windows.FirstOrDefault(h => ForegroundWindow.ClassName(h) == StreamWindowClass);
                if (stream != IntPtr.Zero) PlaceOnTv?.Invoke(stream);
                if (stream != IntPtr.Zero && ForegroundWindow.Activate(stream) && ++inFront >= 3)
                {
                    _log.LogInformation("Moonlight's stream window is in front");
                    return;
                }
                await Task.Delay(500).ConfigureAwait(false);
            }
            if (!process.HasExited)
            {
                var classes = ForegroundWindow.WindowsOf(process.Id).Select(ForegroundWindow.ClassName);
                _log.LogWarning("Moonlight's stream window did not come to the front; its windows: {Classes}", string.Join(", ", classes));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or System.ComponentModel.Win32Exception)
        {
            _log.LogDebug(ex, "Watching Moonlight's window stopped");
        }
    }

    /// <summary>Ends the stream: quits the app on the host, then closes whatever Moonlight window is left.</summary>
    public async Task StopAsync(CancellationToken ct)
    {
        var exe = ExePath ?? throw ProtocolException.Unsupported(ProtocolText.MoonlightNotInstalled);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (StreamingHost() is { } host)
            {
                _log.LogInformation("Moonlight: quitting the stream from {Host}", host);
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe)! };
                foreach (var a in MoonlightCommandLine.Quit(host)) psi.ArgumentList.Add(a);
                using var quit = Process.Start(psi);
                if (quit is not null)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(QuitTimeout);
                    try
                    {
                        await quit.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                        _log.LogInformation("Moonlight quit exited with code {Code}", quit.ExitCode);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        _log.LogWarning("Moonlight quit did not finish in {Timeout}", QuitTimeout);
                        TryKill(quit);
                    }
                }
            }
            await CloseAllAsync(ct).ConfigureAwait(false);
            Changed?.Invoke();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Closes every Moonlight window gracefully, then kills what is left after <see cref="CloseTimeout"/>.</summary>
    private async Task CloseAllAsync(CancellationToken ct)
    {
        var processes = Process.GetProcessesByName(ProcessName);
        if (processes.Length == 0) return;
        try
        {
            foreach (var p in processes) p.CloseMainWindow();
            var deadline = DateTime.UtcNow + CloseTimeout;
            while (DateTime.UtcNow < deadline && processes.Any(p => !p.HasExited))
                await Task.Delay(200, ct).ConfigureAwait(false);
            foreach (var p in processes.Where(p => !p.HasExited))
            {
                _log.LogWarning("Moonlight {Pid} did not close; stopping it", p.Id);
                TryKill(p);
            }
        }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }
    }

    private void TryKill(Process p)
    {
        try { p.Kill(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _log.LogDebug(ex, "Stopping Moonlight {Pid} failed", p.Id);
        }
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _gate.Dispose();
    }

    private static string SafeExitCode(Process p)
    {
        try { return p.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture); }
        catch (InvalidOperationException) { return "?"; }
    }
}
