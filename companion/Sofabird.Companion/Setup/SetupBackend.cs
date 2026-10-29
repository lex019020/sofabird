using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Sofabird.Companion.Net;
using Sofabird.Companion.Services;
using Sofabird.Companion.Vlc;
using Sofabird.Companion.YouTube;
using Sofabird.Core.Power;
using Sofabird.Core.Setup;
using Sofabird.Core.Vlc;

namespace Sofabird.Companion.Setup;

public enum VlcSetupState
{
    NotInstalled,
    Configured,
    NotConfigured,
    /// <summary>Not configured yet and VLC is running: vlcrc and the Lua pages are written after it closes.</summary>
    VlcRunning,
}

/// <summary>Current state of every item of the setup window.</summary>
public sealed record SetupSnapshot(
    FirewallStatus? Firewall,
    string? FirewallError,
    IReadOnlyList<ConnectedNetwork> PublicNetworks,
    bool Autostart,
    VlcSetupState Vlc,
    bool KeepAwake,
    BluetoothServerStatus Bluetooth,
    bool Completed,
    bool ManageFirewall,
    bool ConfigureVlc,
    bool? MediaStation = null,
    IReadOnlyList<PowerDeviation>? PowerDeviations = null,
    bool EverCompleted = false,
    IReadOnlyList<string>? MediaFolders = null,
    string? Browser = null,
    bool BlockAds = false,
    bool SkipSponsors = false,
    IReadOnlyList<string>? MissingMediaFolders = null,
    IReadOnlyList<WakeAdapter>? WakeAdapters = null)
{
    public WakeState Wake => WakeOnLan.State(WakeAdapters ?? []);
}

/// <summary>What the user ticked.</summary>
public sealed record SetupChoices(bool Firewall, bool Autostart, bool ConfigureVlc, bool KeepAwake, bool MediaStation = false,
    IReadOnlyList<string>? MediaFolders = null, bool BlockAds = false, bool SkipSponsors = false, bool Wake = false);

public enum StepOutcome
{
    Done,
    /// <summary>Not chosen, or nothing to do.</summary>
    Skipped,
    /// <summary>The user declined the UAC prompt.</summary>
    Cancelled,
    /// <summary>Will happen later (VLC is running).</summary>
    Pending,
    Failed,
}

public sealed record StepResult(StepOutcome Outcome, string? Detail = null)
{
    public static readonly StepResult Done = new(StepOutcome.Done);
    public static readonly StepResult Skipped = new(StepOutcome.Skipped);
}

public sealed record SetupResult(StepResult Firewall, StepResult Autostart, StepResult Vlc, StepResult KeepAwake, StepResult? MediaStation = null,
    StepResult? BlockAds = null, StepResult? SkipSponsors = null, StepResult? Wake = null);

/// <summary>
/// Everything the "Setup and permissions" window reads and changes. The window talks only to this interface, so it
/// can be shown with a fake (no firewall, registry or VLC changes) during development.
/// </summary>
public interface ISetupBackend
{
    /// <summary>Reads the current state (firewall through COM, ~0.5 s): call it off the UI thread.</summary>
    SetupSnapshot Inspect();

    /// <summary>
    /// Applies the choices (at most one UAC prompt, for the firewall), saves them and marks the setup completed.
    /// Call it on the UI thread (keep-awake is bound to it); the firewall part runs in the background.
    /// </summary>
    Task<SetupResult> ApplyAsync(SetupChoices choices, CancellationToken ct);

    void OpenBluetoothSettings();
    void OpenNetworkSettings();

    /// <summary>Downloads and installs Firefox for YouTube on the TV; <c>null</c> — done, otherwise what went wrong.</summary>
    Task<string?> InstallBrowserAsync(IProgress<FirefoxInstallStep>? progress, CancellationToken ct);

    /// <summary>Opens the TV browser on this PC's screen at the sign-in to YouTube.</summary>
    void SignInToYouTube();
}

/// <summary>The real backend: settings, HKCU Run, vlcrc + Lua pages, keep-awake and Windows Firewall.</summary>
public sealed class SetupBackend : ISetupBackend
{
    private readonly AppPaths _paths;
    private readonly Settings _settings;
    private readonly SettingsStore _store;
    private readonly VlcManager _vlc;
    private readonly CompanionHost _host;
    private readonly LibraryService _library;
    private readonly BluetoothRfcommServer _bluetooth;
    private readonly PowerPlanService _powerPlan;
    private readonly YouTubeService _youtube;
    private readonly WakeOnLanService _wake;
    private readonly ILogger _log;

    public SetupBackend(AppPaths paths, Settings settings, SettingsStore store, VlcManager vlc, CompanionHost host, LibraryService library,
        BluetoothRfcommServer bluetooth, PowerPlanService powerPlan, YouTubeService youtube, WakeOnLanService wake, ILogger<SetupBackend> log)
    {
        _youtube = youtube;
        _wake = wake;
        _powerPlan = powerPlan;
        _paths = paths;
        _settings = settings;
        _store = store;
        _vlc = vlc;
        _host = host;
        _library = library;
        _bluetooth = bluetooth;
        _log = log;
    }

    public static string CompanionExe => Environment.ProcessPath ?? Application.ExecutablePath;

    public IReadOnlyList<FirewallProgram> FirewallPrograms => FirewallPlanner.Programs(CompanionExe, _vlc.Installation?.ExePath);

    /// <summary>Firewall state only (startup check); <c>null</c> if it cannot be read.</summary>
    public FirewallStatus? InspectFirewall()
    {
        var (status, error) = ComFirewallRuleSource.TryInspect(FirewallPrograms);
        if (error is not null) _log.LogWarning("Reading the firewall rules failed: {Error}", error);
        return status;
    }

    public bool ShouldShowOnStart => SetupPolicy.ShouldShowOnStart(_settings.SetupCompleted, _settings.SetupVersion);

    public SetupSnapshot Inspect()
    {
        var (fw, fwError) = ComFirewallRuleSource.TryInspect(FirewallPrograms);
        var publicNets = NetworkCategories.Connected().Where(n => n.Category == NetworkCategory.Public).ToList();
        bool autostart;
        try { autostart = AutostartService.IsEnabled(); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { autostart = false; }
        return new SetupSnapshot(fw, fwError, publicNets, autostart, InspectVlc(), _settings.KeepAwake, _bluetooth.Status,
            _settings.SetupCompleted && _settings.SetupVersion >= SetupPolicy.CurrentVersion, _settings.ManageFirewall, _settings.ConfigureVlc,
            _settings.MediaStation, _powerPlan.CheckMediaStation(), _settings.SetupCompleted, _settings.MediaRoots.ToList(),
            _youtube.BrowserPath, _youtube.BlockAds, _youtube.SkipSponsors,
            _settings.MediaRoots.Where(r => !Directory.Exists(r)).ToList(), _wake.Inspect());
    }

    private VlcSetupState InspectVlc()
    {
        bool? configured;
        try { configured = _vlc.IsConfiguredForPhone(); }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Checking the VLC configuration failed");
            configured = false;
        }
        if (configured is null) return VlcSetupState.NotInstalled;
        if (configured == true && _settings.ConfigureVlc) return VlcSetupState.Configured;
        return VlcLocator.IsAnyRunning() ? VlcSetupState.VlcRunning : VlcSetupState.NotConfigured;
    }

    public async Task<SetupResult> ApplyAsync(SetupChoices choices, CancellationToken ct)
    {
        _log.LogInformation("Setup: applying (firewall {Firewall}, autostart {Autostart}, configure VLC {Vlc}, keep awake {KeepAwake}, media station {MediaStation})",
            choices.Firewall, choices.Autostart, choices.ConfigureVlc, choices.KeepAwake, choices.MediaStation);

        // The settings first: the VLC step reads them (Lua pages are installed only when VLC is configured).
        _store.Update(s =>
        {
            s.ConfigureVlc = choices.ConfigureVlc;
            s.AutoInstallLuaPages = choices.ConfigureVlc;
            s.MediaStation = choices.MediaStation;
            s.ManageFirewall = choices.Firewall;
            s.SetupCompleted = true;
            s.SetupVersion = SetupPolicy.CurrentVersion;
            if (choices.MediaFolders is not null) s.MediaRoots = choices.MediaFolders.ToList();
        });
        if (choices.MediaFolders is not null) _library.Rescan();
        var autostart = ApplyAutostart(choices.Autostart);
        var vlc = ApplyVlc(choices.ConfigureVlc);
        _host.SetKeepAwake(choices.KeepAwake);
        var mediaStation = ApplyMediaStation(choices.MediaStation);

        var blockAds = await ApplyAddonAsync(TvAddon.UBlock, choices.BlockAds, ct);
        var skipSponsors = await ApplyAddonAsync(TvAddon.SponsorBlock, choices.SkipSponsors, ct);
        var (firewall, wake) = await ApplyElevatedAsync(choices.Firewall, choices.Wake, ct);
        _log.LogInformation("Setup: firewall {Firewall}, autostart {Autostart}, VLC {Vlc}, media station {MediaStation}, ad blocking {BlockAds}, wake {Wake}",
            firewall.Outcome, autostart.Outcome, vlc.Outcome, mediaStation.Outcome, blockAds.Outcome, wake.Outcome);
        return new SetupResult(firewall, autostart, vlc, StepResult.Done, mediaStation, blockAds, skipSponsors, wake);
    }

    private async Task<StepResult> ApplyAddonAsync(TvAddon addon, bool on, CancellationToken ct)
    {
        if (on == _settings.IsOn(addon) || (on && _youtube.BrowserPath is null)) return StepResult.Skipped;
        try
        {
            await _youtube.SetAddonAsync(addon, on, ct);
            return StepResult.Done;
        }
        catch (Core.Session.ProtocolException ex)
        {
            return new StepResult(StepOutcome.Failed, ex.Message);
        }
    }

    public Task<string?> InstallBrowserAsync(IProgress<FirefoxInstallStep>? progress, CancellationToken ct) =>
        _youtube.InstallBrowserAsync(progress, ct);

    public void SignInToYouTube() => _youtube.SignIn();

    /// <summary>Unticking only stops the watching: the previous power settings are not known, so they stay.</summary>
    private StepResult ApplyMediaStation(bool on)
    {
        if (!on) return StepResult.Skipped;
        var error = _powerPlan.ApplyMediaStation();
        return error is null ? StepResult.Done : new StepResult(StepOutcome.Failed, error);
    }

    private StepResult ApplyAutostart(bool on)
    {
        try
        {
            if (AutostartService.IsEnabled() != on) AutostartService.SetEnabled(on);
            return StepResult.Done;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            _log.LogWarning(ex, "Setup: changing autostart failed");
            return new StepResult(StepOutcome.Failed, ex.Message);
        }
    }

    private StepResult ApplyVlc(bool on)
    {
        if (!on) return StepResult.Skipped;
        if (_vlc.Installation is null) return StepResult.Skipped;
        if (VlcLocator.IsAnyRunning())
        {
            // Both are retried by VlcManager as soon as VLC closes.
            _vlc.TryInstallLuaPages();
            return new StepResult(StepOutcome.Pending);
        }
        var rc = _vlc.TryConfigureVlcrc();
        _vlc.TryInstallLuaPages();
        return rc is null or VlcrcResult.Skipped || _vlc.IsConfiguredForPhone() != true
            ? new StepResult(StepOutcome.Failed)
            : StepResult.Done;
    }

    /// <summary>
    /// The firewall rules and Wake-on-LAN, the two steps that need administrator rights: one elevated helper (one UAC
    /// prompt) for whichever of them has something to change; each is checked again afterwards.
    /// </summary>
    private async Task<(StepResult Firewall, StepResult Wake)> ApplyElevatedAsync(bool firewall, bool wake, CancellationToken ct)
    {
        var programs = FirewallPrograms;
        var firewallOk = !firewall || (await Task.Run(() => ComFirewallRuleSource.TryInspect(programs), ct)).Status is { Ok: true };
        var toWake = wake ? WakeOnLan.ToFix(await Task.Run(_wake.Inspect, ct)) : [];
        if (firewallOk && toWake.Count == 0)
            return (firewall ? StepResult.Done : StepResult.Skipped, wake ? StepResult.Done : StepResult.Skipped);

        var (outcome, error) = await SetupElevation.RunElevatedAsync(CompanionExe, _vlc.Installation?.ExePath, _paths.DataDir,
            !firewallOk, toWake.Select(a => a.Name).ToList(), _log, ct);
        if (outcome == FirewallApplyOutcome.Cancelled)
            return (firewallOk ? StepResult.Done : new StepResult(StepOutcome.Cancelled), toWake.Count == 0 ? StepResult.Done : new StepResult(StepOutcome.Cancelled));

        var firewallResult = firewall ? StepResult.Done : StepResult.Skipped;
        if (!firewallOk)
        {
            var after = await Task.Run(() => ComFirewallRuleSource.TryInspect(programs), ct);
            if (after.Status is not { Ok: true })
                firewallResult = new StepResult(StepOutcome.Failed, error ?? after.Error ?? $"code {SetupElevation.ExitStillNotOk}"); // technical detail, shown in parentheses
        }
        var wakeResult = wake ? StepResult.Done : StepResult.Skipped;
        if (toWake.Count > 0 && WakeOnLan.State(await Task.Run(_wake.Inspect, ct)) != WakeState.Ready)
            wakeResult = new StepResult(StepOutcome.Failed, error ?? $"code {SetupElevation.ExitWakeFailed}");
        return (firewallResult, wakeResult);
    }

    public void OpenBluetoothSettings() => OpenUri("ms-settings:bluetooth");

    public void OpenNetworkSettings() => OpenUri("ms-settings:network-status");

    private void OpenUri(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _log.LogWarning(ex, "Opening {Uri} failed", uri);
        }
    }
}
