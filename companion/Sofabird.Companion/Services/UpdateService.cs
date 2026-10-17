using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;
using Sofabird.Core.Updates;

namespace Sofabird.Companion.Services;

/// <summary>
/// Quiet automatic updates from GitHub Releases (Velopack): checks a few minutes after the start and then every
/// 6 hours, downloads in the background and restarts the companion into the new version as soon as VLC is not
/// playing. A companion not installed by Velopack (run from the build folder) or a local build does nothing.
/// </summary>
public sealed class UpdateService
{
    private const string RepoUrl = "https://github.com/lex019020/sofabird";
    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(6);
    private static readonly TimeSpan RetryApply = TimeSpan.FromMinutes(10);

    private readonly Settings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly Func<bool> _vlcPlaying;
    private readonly Action _exitForUpdate;
    private readonly BackgroundWork _background;
    private readonly ILogger _log;

    /// <param name="vlcPlaying">An update does not restart the companion in the middle of a movie.</param>
    /// <param name="exitForUpdate">Shuts the companion down gracefully; Velopack's updater waits for the exit.</param>
    public UpdateService(Settings settings, SettingsStore settingsStore, Func<bool> vlcPlaying, Action exitForUpdate, BackgroundWork background,
        ILogger<UpdateService> log)
    {
        _settings = settings;
        _settingsStore = settingsStore;
        _vlcPlaying = vlcPlaying;
        _exitForUpdate = exitForUpdate;
        _background = background;
        _log = log;
        IsInstalled = new UpdateManager(new GithubSource(RepoUrl, null, false)).IsInstalled;
    }

    /// <summary>Installed by Velopack (Setup.exe), so it can update itself.</summary>
    public bool IsInstalled { get; }

    /// <summary>Test builds are offered too (tray: "Get test builds").</summary>
    public bool IncludesTestBuilds => UpdatePolicy.IncludesTestBuilds(_settings.UpdateChannel, AppVersion.Informational);

    /// <summary>Takes effect at the next check.</summary>
    public void SetIncludesTestBuilds(bool on) =>
        _settingsStore.Update(s => s.UpdateChannel = on ? UpdatePolicy.Beta : UpdatePolicy.Stable);

    public void Start()
    {
        if (!IsInstalled || !UpdatePolicy.UpdatesItself(AppVersion.Informational))
        {
            _log.LogInformation("Automatic updates are off: {Reason}", IsInstalled ? "a local build" : "not installed by Setup");
            return;
        }
        _background.Run("Update loop", LoopAsync);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        await Task.Delay(FirstCheck, ct).ConfigureAwait(false);
        while (true)
        {
            var waiting = false;
            try { waiting = await CheckAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "Update check failed"); }
            await Task.Delay(waiting ? RetryApply : CheckEvery, ct).ConfigureAwait(false);
        }
    }

    /// <returns><c>true</c> when an update is downloaded and waits for VLC to stop playing.</returns>
    private async Task<bool> CheckAsync(CancellationToken ct)
    {
        var testBuilds = IncludesTestBuilds;
        var manager = new UpdateManager(new GithubSource(RepoUrl, null, testBuilds));
        var target = manager.UpdatePendingRestart;
        if (target is null)
        {
            var info = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (info is null)
            {
                _log.LogDebug("No update ({Channel})", testBuilds ? UpdatePolicy.Beta : UpdatePolicy.Stable);
                return false;
            }
            _log.LogInformation("Downloading update {Version}", info.TargetFullRelease.Version);
            await manager.DownloadUpdatesAsync(info, null, ct).ConfigureAwait(false);
            target = info.TargetFullRelease;
        }
        if (!UpdatePolicy.CanApplyNow(_vlcPlaying()))
        {
            _log.LogInformation("Update {Version} is ready; it is applied when nothing plays on the TV", target.Version);
            return true;
        }
        _log.LogInformation("Restarting into update {Version}", target.Version);
        manager.WaitExitThenApplyUpdates(target, silent: true, restart: true, []);
        _exitForUpdate();
        return false;
    }
}
