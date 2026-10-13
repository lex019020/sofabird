using System.Diagnostics;
using Microsoft.Extensions.Logging;
using VlcRemote.Companion.Net;
using VlcRemote.Companion.Services;
using VlcRemote.Companion.Vlc;
using VlcRemote.Core.Power;
using VlcRemote.Core.Setup;
using VlcRemote.Core.Vlc;

namespace VlcRemote.Companion.Setup;

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
    bool EverCompleted = false);

/// <summary>What the user ticked.</summary>
public sealed record SetupChoices(bool Firewall, bool Autostart, bool ConfigureVlc, bool KeepAwake, bool MediaStation = false);

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

public sealed record SetupResult(StepResult Firewall, StepResult Autostart, StepResult Vlc, StepResult KeepAwake, StepResult? MediaStation = null);

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
}

/// <summary>The real backend: settings, HKCU Run, vlcrc + Lua pages, keep-awake and Windows Firewall.</summary>
public sealed class SetupBackend : ISetupBackend
{
    private readonly AppPaths _paths;
    private readonly Settings _settings;
    private readonly SettingsStore _store;
    private readonly VlcManager _vlc;
    private readonly PowerService _power;
    private readonly BluetoothRfcommServer _bluetooth;
    private readonly PowerPlanService _powerPlan;
    private readonly ILogger _log;

    public SetupBackend(AppPaths paths, Settings settings, SettingsStore store, VlcManager vlc, PowerService power,
        BluetoothRfcommServer bluetooth, PowerPlanService powerPlan, ILogger<SetupBackend> log)
    {
        _powerPlan = powerPlan;
        _paths = paths;
        _settings = settings;
        _store = store;
        _vlc = vlc;
        _power = power;
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
            _settings.MediaStation, _powerPlan.CheckMediaStation(), _settings.SetupCompleted);
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
        return IsVlcRunning() ? VlcSetupState.VlcRunning : VlcSetupState.NotConfigured;
    }

    private static bool IsVlcRunning()
    {
        var procs = VlcLocator.RunningProcesses();
        foreach (var p in procs) p.Dispose();
        return procs.Length > 0;
    }

    public async Task<SetupResult> ApplyAsync(SetupChoices choices, CancellationToken ct)
    {
        _log.LogInformation("Setup: applying (firewall {Firewall}, autostart {Autostart}, configure VLC {Vlc}, keep awake {KeepAwake}, media station {MediaStation})",
            choices.Firewall, choices.Autostart, choices.ConfigureVlc, choices.KeepAwake, choices.MediaStation);

        var autostart = ApplyAutostart(choices.Autostart);
        var vlc = ApplyVlc(choices.ConfigureVlc);
        ApplyKeepAwake(choices.KeepAwake);
        var mediaStation = ApplyMediaStation(choices.MediaStation);

        _settings.ManageFirewall = choices.Firewall;
        _settings.SetupCompleted = true;
        _settings.SetupVersion = SetupPolicy.CurrentVersion;
        _store.Save(_settings);

        var firewall = choices.Firewall ? await ApplyFirewallAsync(ct) : StepResult.Skipped;
        _log.LogInformation("Setup: firewall {Firewall}, autostart {Autostart}, VLC {Vlc}, media station {MediaStation}",
            firewall.Outcome, autostart.Outcome, vlc.Outcome, mediaStation.Outcome);
        return new SetupResult(firewall, autostart, vlc, StepResult.Done, mediaStation);
    }

    /// <summary>Unticking only stops the watching: the previous power settings are not known, so they stay.</summary>
    private StepResult ApplyMediaStation(bool on)
    {
        _settings.MediaStation = on;
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
        _settings.ConfigureVlc = on;
        _settings.AutoInstallLuaPages = on;
        if (!on) return StepResult.Skipped;
        if (_vlc.Installation is null) return StepResult.Skipped;
        if (IsVlcRunning())
        {
            // Both are retried by VlcManager as soon as VLC closes.
            _vlc.TryInstallLuaPages(initial: false);
            return new StepResult(StepOutcome.Pending);
        }
        var rc = _vlc.TryConfigureVlcrc();
        _vlc.TryInstallLuaPages(initial: false);
        return rc is null or VlcrcResult.Skipped || _vlc.IsConfiguredForPhone() != true
            ? new StepResult(StepOutcome.Failed)
            : StepResult.Done;
    }

    private void ApplyKeepAwake(bool on)
    {
        _settings.KeepAwake = on;
        _power.KeepAwakeEnabled = on;
        _power.OnPlaybackState(_vlc.LastState?.Playback.State == "playing");
        _vlc.Poke();
    }

    private async Task<StepResult> ApplyFirewallAsync(CancellationToken ct)
    {
        var programs = FirewallPrograms;
        var before = await Task.Run(() => ComFirewallRuleSource.TryInspect(programs), ct);
        if (before.Status is { Ok: true }) return StepResult.Done;

        var (outcome, error) = await FirewallElevation.RunElevatedAsync(CompanionExe, _vlc.Installation?.ExePath, _paths.DataDir, _log, ct);
        if (outcome == FirewallApplyOutcome.Cancelled) return new StepResult(StepOutcome.Cancelled);
        var after = await Task.Run(() => ComFirewallRuleSource.TryInspect(programs), ct);
        if (after.Status is { Ok: true }) return StepResult.Done;
        return new StepResult(StepOutcome.Failed, error ?? after.Error ?? $"code {FirewallElevation.ExitStillNotOk}"); // technical detail, shown in parentheses
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
