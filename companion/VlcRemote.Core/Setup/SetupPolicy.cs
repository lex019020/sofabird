namespace VlcRemote.Core.Setup;

/// <summary>What the companion tells the user about the firewall at startup (tray notification).</summary>
public enum StartupNotice
{
    None,
    /// <summary>A block rule (usually from "Cancel" in the Windows prompt) stops the phone from connecting.</summary>
    FirewallBlocked,
    /// <summary>Setup was completed with network access on, but the allow rules are gone.</summary>
    FirewallMissing,
}

/// <summary>
/// When the "Setup and permissions" window appears by itself. Every permission or system change the companion makes
/// is an item of that window; adding an item bumps <see cref="CurrentVersion"/>, so the window is shown once more
/// to users who completed an older version.
/// </summary>
public static class SetupPolicy
{
    /// <summary>
    /// Version of the item set. 1: network access (firewall), start with Windows, VLC configuration, keep awake,
    /// Bluetooth (info).
    /// </summary>
    public const int CurrentVersion = 1;

    /// <summary>Show the window at startup: never completed, or completed for an older item set.</summary>
    public static bool ShouldShowOnStart(bool completed, int completedVersion) =>
        !completed || completedVersion < CurrentVersion;

    /// <summary>
    /// The startup notification about the firewall. Nothing when the user turned network access management off,
    /// when the state is unknown, or when the firewall is fine. Block rules are reported always; missing allow rules
    /// only after setup was completed (before that the window itself asks).
    /// </summary>
    public static StartupNotice Decide(bool completed, bool manageFirewall, FirewallStatus? status)
    {
        if (!manageFirewall || status is null || status.Ok) return StartupNotice.None;
        if (status.AnyBlocked) return StartupNotice.FirewallBlocked;
        return completed && status.AnyMissing ? StartupNotice.FirewallMissing : StartupNotice.None;
    }

    /// <summary>
    /// Whether the network listeners should wait for the setup window: it is due and the firewall would make Windows
    /// show its own "allow access" prompt (no rule for a program yet) or silently drop connections (block rules).
    /// Starting the listeners after the window applied the rules avoids that prompt.
    /// </summary>
    public static bool DeferListeners(bool showOnStart, FirewallStatus? status) =>
        showOnStart && status is { Ok: false };
}
