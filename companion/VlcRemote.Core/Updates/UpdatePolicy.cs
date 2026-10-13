namespace VlcRemote.Core.Updates;

/// <summary>Which builds the companion updates itself to, kept free of Velopack so it can be tested.</summary>
public static class UpdatePolicy
{
    public const string Stable = "stable";
    public const string Beta = "beta";

    /// <summary>
    /// Builds from the working copy (<c>-local.&lt;time&gt;</c>, deploy-windows.ps1 -Local) are not updated: the next
    /// test build would replace the code being tried within hours.
    /// </summary>
    public static bool UpdatesItself(string currentVersion) =>
        !currentVersion.Contains("-local", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether test builds (GitHub pre-releases) are offered: the <c>updateChannel</c> setting, or by default the kind
    /// of build that is installed (a test build stays on test builds, a release on releases).
    /// </summary>
    public static bool IncludesTestBuilds(string? channel, string currentVersion) => channel switch
    {
        Beta => true,
        Stable => false,
        _ => currentVersion.Contains("-beta", StringComparison.OrdinalIgnoreCase),
    };

    /// <summary>A downloaded update is applied (the companion restarts) only while VLC is not playing.</summary>
    public static bool CanApplyNow(bool vlcPlaying) => !vlcPlaying;
}
