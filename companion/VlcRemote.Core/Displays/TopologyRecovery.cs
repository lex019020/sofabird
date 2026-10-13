namespace VlcRemote.Core.Displays;

/// <summary>What the companion does with the VLC window after the set of monitors changed.</summary>
public enum TopologyReaction
{
    /// <summary>Nothing to do (the user keeps VLC on another monitor, or the preferred one was not there before either).</summary>
    None,
    /// <summary>No preferred monitor: the companion does not manage the VLC window.</summary>
    NotManaged,
    /// <summary>The preferred monitor is there: bring the window back to it (restored, fullscreen again if it was).</summary>
    Restore,
    /// <summary>The monitor with VLC went away (TV unplugged or switched off at the mains): pause playback.</summary>
    PreferredLost,
}

/// <param name="PreferredId">The preferred monitor (the TV); <c>null</c> — the window is not managed.</param>
/// <param name="VlcMonitorBefore">Id of the monitor the VLC window was on before the change; <c>null</c> — unknown.</param>
/// <param name="AwaitingPreferred">The preferred monitor was lost earlier and has not come back since.</param>
public sealed record TopologyInput(
    string? PreferredId,
    string? VlcMonitorBefore,
    IReadOnlyList<MonitorInfo> Before,
    IReadOnlyList<MonitorInfo> After,
    bool AwaitingPreferred);

/// <summary>
/// Rules for a monitor change (laptop lid closed, TV switched off/on, HDMI unplugged). Closing the lid of a laptop
/// that shows VLC on the TV makes Windows rebuild the desktop; VLC then drops fullscreen and may end up minimized,
/// so the window is put back. A window the user moved to another monitor on purpose is left alone.
/// </summary>
public static class TopologyRecovery
{
    public static TopologyReaction Decide(TopologyInput input)
    {
        var preferred = input.PreferredId;
        if (preferred is null) return TopologyReaction.NotManaged;
        var vlcBefore = input.VlcMonitorBefore;
        var vlcWasOnPreferred = vlcBefore is null || SameId(vlcBefore, preferred);

        if (MonitorTopology.FindById(input.After, preferred) is not null)
        {
            if (input.AwaitingPreferred || vlcWasOnPreferred) return TopologyReaction.Restore;
            // The window's own monitor is gone: Windows moved it somewhere, put it on the preferred one.
            return MonitorTopology.FindById(input.After, vlcBefore) is null ? TopologyReaction.Restore : TopologyReaction.None;
        }

        var wasConnected = MonitorTopology.FindById(input.Before, preferred) is not null;
        return wasConnected && vlcWasOnPreferred ? TopologyReaction.PreferredLost : TopologyReaction.None;
    }

    /// <summary>
    /// VLC leaves fullscreen when the desktop is rebuilt under it, and the companion sees the change only after that.
    /// It counts as fullscreen if it was seen fullscreen within <paramref name="memory"/> before the change.
    /// </summary>
    public static bool WasFullscreen(DateTimeOffset? lastSeenFullscreen, DateTimeOffset changedAt, TimeSpan memory) =>
        lastSeenFullscreen is { } seen && seen >= changedAt - memory;

    private static bool SameId(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
