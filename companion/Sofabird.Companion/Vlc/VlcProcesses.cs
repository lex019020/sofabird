using System.Diagnostics;
using Sofabird.Companion.Interop;

namespace Sofabird.Companion.Vlc;

/// <summary>A <c>vlc.exe</c> process as <see cref="VlcManager"/> sees it.</summary>
internal interface IVlcProcess : IDisposable
{
    int Id { get; }
    /// <summary>Local start time; throws when it cannot be read (no access, the process is gone).</summary>
    DateTime StartTime { get; }
    bool HasExited { get; }
    int ExitCode { get; }
    /// <summary>Asks VLC to close (its main window), as the user would.</summary>
    void CloseMainWindow();
    void Kill();
    /// <summary>The command line of the process; <c>null</c> when it cannot be read.</summary>
    string? CommandLine();
}

/// <summary>
/// The <c>vlc.exe</c> processes of the PC: <see cref="SystemVlcProcesses"/> in the companion, a fake VLC in the
/// scenario tests.
/// </summary>
internal interface IVlcProcesses
{
    /// <summary>All running <c>vlc.exe</c> processes (other instances too); the caller disposes them.</summary>
    IReadOnlyList<IVlcProcess> Running();

    /// <summary>Starts <c>vlc.exe</c>; <c>null</c> when no process was started.</summary>
    IVlcProcess? Start(ProcessStartInfo startInfo);

    /// <summary>Whether any <c>vlc.exe</c> is running.</summary>
    bool IsAnyRunning()
    {
        var all = Running();
        foreach (var p in all) p.Dispose();
        return all.Count > 0;
    }
}

/// <summary>The real <c>vlc.exe</c> processes (<see cref="VlcLocator.RunningProcesses"/>, <see cref="Process.Start(ProcessStartInfo)"/>).</summary>
internal sealed class SystemVlcProcesses : IVlcProcesses
{
    public static SystemVlcProcesses Instance { get; } = new();

    public IReadOnlyList<IVlcProcess> Running() => VlcLocator.RunningProcesses().Select(p => new SystemVlcProcess(p)).ToList();

    public IVlcProcess? Start(ProcessStartInfo startInfo) => Process.Start(startInfo) is { } p ? new SystemVlcProcess(p) : null;

    private sealed class SystemVlcProcess(Process process) : IVlcProcess
    {
        public int Id => process.Id;
        public DateTime StartTime => process.StartTime;
        public bool HasExited => process.HasExited;
        public int ExitCode => process.ExitCode;
        public void CloseMainWindow() => process.CloseMainWindow();
        public void Kill() => process.Kill();
        public string? CommandLine() => ProcessCommandLine.TryGet(process.Id);
        public void Dispose() => process.Dispose();
    }
}

/// <summary>
/// What <see cref="VlcManager"/> talks to: the real VLC by default (<see cref="Default"/>), a fake VLC in the scenario
/// tests.
/// </summary>
/// <param name="Processes">The <c>vlc.exe</c> processes.</param>
/// <param name="HttpHandler">Transport of the HTTP interface; <c>null</c> — the network (127.0.0.1).</param>
/// <param name="RcConnect">Opens the RC connection; <c>null</c> — TCP to 127.0.0.1.</param>
internal sealed record VlcBackend(
    IVlcProcesses Processes,
    HttpMessageHandler? HttpHandler = null,
    Func<CancellationToken, Task<Stream>>? RcConnect = null)
{
    public static VlcBackend Default { get; } = new(SystemVlcProcesses.Instance);
}
