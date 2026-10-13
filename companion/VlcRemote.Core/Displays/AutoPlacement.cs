namespace VlcRemote.Core.Displays;

public enum PlacementDecision
{
    /// <summary>Move the window to the preferred monitor.</summary>
    Move,
    /// <summary>This window was already placed once — never fight the user.</summary>
    AlreadyHandled,
    /// <summary>VLC was running before the companion started: its window is where the user left it.</summary>
    StartedBeforeCompanion,
    /// <summary>No preferred monitor ("don't manage the display").</summary>
    NoPreference,
    /// <summary>The preferred monitor is not connected (TV is off) — leave the window alone.</summary>
    PreferredNotConnected,
    /// <summary>The window is already on the preferred monitor.</summary>
    AlreadyThere,
    /// <summary>No main window appeared in time.</summary>
    WindowNotFound,
}

/// <summary>Inputs of <see cref="AutoPlacement.Decide"/>.</summary>
/// <param name="PreferredId">Preferred (or one-off target) monitor id.</param>
/// <param name="Monitors">Connected monitors.</param>
/// <param name="Window">Window handle (0 — not known yet: decide on everything else).</param>
/// <param name="WindowMonitorGdi">GDI device name of the monitor with the window (<c>null</c> — unknown).</param>
/// <param name="HandledWindows">Windows placed before.</param>
/// <param name="StartedBeforeCompanion">The VLC process is older than the companion.</param>
public sealed record PlacementInput(
    string? PreferredId,
    IReadOnlyList<MonitorInfo> Monitors,
    long Window,
    string? WindowMonitorGdi,
    IReadOnlyCollection<long> HandledWindows,
    bool StartedBeforeCompanion);

public static class AutoPlacement
{
    /// <summary>Should a new VLC window be moved to the preferred monitor?</summary>
    public static PlacementDecision Decide(PlacementInput input)
    {
        if (input.Window != 0 && input.HandledWindows.Contains(input.Window)) return PlacementDecision.AlreadyHandled;
        if (input.StartedBeforeCompanion) return PlacementDecision.StartedBeforeCompanion;
        if (string.IsNullOrEmpty(input.PreferredId)) return PlacementDecision.NoPreference;
        var target = MonitorTopology.FindById(input.Monitors, input.PreferredId);
        if (target is null) return PlacementDecision.PreferredNotConnected;
        if (input.WindowMonitorGdi is not null &&
            string.Equals(input.WindowMonitorGdi, target.GdiDeviceName, StringComparison.OrdinalIgnoreCase))
            return PlacementDecision.AlreadyThere;
        return PlacementDecision.Move;
    }
}
