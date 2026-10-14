namespace Sofabird.Core.Vlc;

public enum VlcRunState
{
    /// <summary>vlc.exe was not found on disk.</summary>
    NotInstalled,
    /// <summary>There is no vlc.exe process.</summary>
    NotRunning,
    /// <summary>The process was started by the companion, the interfaces are still coming up.</summary>
    Starting,
    /// <summary>vlc.exe is running, but the HTTP interface does not answer with our password.</summary>
    RunningNoApi,
    /// <summary>Full control: HTTP (+RC).</summary>
    Running,
}

/// <summary>The decisions of the VLC polling loop, kept free of processes and sockets so they can be tested.</summary>
public static class VlcRunStatePolicy
{
    /// <summary>Failed status requests in a row that a running VLC is allowed before it counts as lost.</summary>
    public const int FailuresBeforeLost = 3;

    /// <summary>How long after its start a VLC without HTTP is still "starting" (a stale plugins cache takes ~12 s).</summary>
    public static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The state after a failed status request while a vlc.exe process exists, or <c>null</c> to keep the current one:
    /// a one-off failure (VLC busy opening a file) is not a loss of control.
    /// </summary>
    public static VlcRunState? AfterHttpFailure(VlcRunState current, int failuresInARow, bool startedRecently)
    {
        if (current == VlcRunState.Running && failuresInARow < FailuresBeforeLost) return null;
        return startedRecently ? VlcRunState.Starting : VlcRunState.RunningNoApi;
    }

    /// <summary>Poll fast while playing (the position) or starting (to notice HTTP early), slowly otherwise.</summary>
    public static TimeSpan PollDelay(VlcRunState state, bool playing) =>
        (state == VlcRunState.Running && playing) || state == VlcRunState.Starting
            ? TimeSpan.FromMilliseconds(500)
            : TimeSpan.FromSeconds(2);
}
