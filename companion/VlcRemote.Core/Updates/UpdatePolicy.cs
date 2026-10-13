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

    /// <summary>
    /// The channel to store at the first start when none is set: the kind of build that was installed. Stored, so a test
    /// build that updates to a release (which is newer than its test builds) keeps getting test builds afterwards.
    /// <c>null</c> — nothing to store (already set, or a local build that does not update).
    /// </summary>
    public static string? ChannelToRemember(string? channel, string currentVersion) =>
        channel is not null || !UpdatesItself(currentVersion) ? null
        : IncludesTestBuilds(null, currentVersion) ? Beta : Stable;

    /// <summary>A downloaded update is applied (the companion restarts) only while VLC is not playing.</summary>
    public static bool CanApplyNow(bool vlcPlaying) => !vlcPlaying;
}
