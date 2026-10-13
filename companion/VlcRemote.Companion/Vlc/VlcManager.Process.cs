using System.Diagnostics;
using Microsoft.Extensions.Logging;
using VlcRemote.Companion.Interop;
using VlcRemote.Core.Session;
using VlcRemote.Core.Vlc;
using VlcRemote.Core.Localization;

namespace VlcRemote.Companion.Vlc;

// The VLC process: launch with the companion's interfaces, restart, quit.
public sealed partial class VlcManager
{
    // ------------------------------------------------------------------ process control

    public bool IsProcessRunning => RunState is VlcRunState.Running or VlcRunState.RunningNoApi or VlcRunState.Starting;

    /// <summary>
    /// Start <c>vlc.exe</c> with the HTTP+RC interfaces. If VLC is already under control, open
    /// <paramref name="uri"/> over HTTP. If VLC is open without control (<see cref="VlcRunState.RunningNoApi"/>),
    /// restart it with the same file (the position is lost). If it is starting, wait for it.
    /// </summary>
    public async Task LaunchAsync(string? uri, int? displayIndex, CancellationToken ct)
    {
        Installation ??= VlcLocator.Find(_settings.VlcPath);
        if (Installation is null)
            throw ProtocolException.VlcNotRunning(ProtocolText.VlcNotFoundInstall);

        if (RunState == VlcRunState.Starting)
        {
            var wait = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
            while (RunState == VlcRunState.Starting && DateTimeOffset.UtcNow < wait)
            {
                Poke();
                await Task.Delay(300, ct).ConfigureAwait(false);
            }
        }

        switch (RunState)
        {
            case VlcRunState.Running:
                if (!string.IsNullOrEmpty(uri))
                    await OpenAsync(uri, enqueue: false, subtitle: null, audioTrack: null, subtitleTrack: null, ct).ConfigureAwait(false);
                return;
            case VlcRunState.RunningNoApi:
                await RestartAsync(uri, displayIndex, ct).ConfigureAwait(false);
                return;
            case VlcRunState.Starting:
                _log.LogWarning("launch: VLC is still starting after 20 s");
                return;
        }

        await StartProcessAsync(uri, displayIndex, ct).ConfigureAwait(false);
    }

    private async Task StartProcessAsync(string? uri, int? displayIndex, CancellationToken ct)
    {
        var installation = Installation ?? throw ProtocolException.VlcNotRunning(ProtocolText.VlcNotFoundInstall);
        if (_settings.AutoInstallLuaPages) TryInstallLuaPages(initial: false);
        TryConfigureVlcrc();

        // Window placement: the media is opened only after the window is on the target monitor, so the video
        // (fullscreen with LaunchFullscreen) starts there instead of flashing on another screen first.
        string? target = null;
        try { target = LaunchTargetResolver?.Invoke(displayIndex); }
        catch (Exception ex) { _log.LogWarning(ex, "Resolving the launch display failed"); }
        var deferredUri = target is not null && PlaceLaunchedWindowAsync is not null && !string.IsNullOrEmpty(uri) ? uri : null;

        var psi = new ProcessStartInfo
        {
            FileName = installation.ExePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = installation.InstallDir,
        };
        foreach (var a in VlcCommandLine.BuildLaunchArgs(LaunchOptions, deferredUri is null ? uri : null, fullscreenFollowsWindow: target is not null))
            psi.ArgumentList.Add(a);

        _log.LogInformation("Launching VLC: {Exe} {Args}{Target}", psi.FileName, VlcCommandLine.RedactArgs(psi.ArgumentList),
            target is null ? "" : $" (window -> {target}{(deferredUri is null ? "" : ", media after placement")})");
        Process process;
        Task? placement = null;
        _launchInProgress = true;
        try
        {
            process = Process.Start(psi) ?? throw ProtocolException.VlcError(ProtocolText.VlcStartFailed);
            _owned = process;
            _ownedStartedAt = DateTimeOffset.UtcNow;
            ProcessId = process.Id;
            if (target is not null && PlaceLaunchedWindowAsync is { } place)
                placement = place(process.Id, target, ct);
        }
        finally
        {
            _launchInProgress = false;
        }
        SetRunState(VlcRunState.Starting);
        Poke();

        // Wait for HTTP for up to 20 s.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (process.HasExited)
                throw ProtocolException.VlcError(ProtocolText.VlcExitedImmediately(process.ExitCode));
            if (RunState == VlcRunState.Running) break;
            await Task.Delay(300, ct).ConfigureAwait(false);
            Poke();
        }
        if (RunState != VlcRunState.Running)
            _log.LogWarning("VLC started but HTTP interface did not answer within 20 s");

        if (placement is not null)
        {
            try { await placement.ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "Placing the VLC window failed"); }
        }
        if (deferredUri is not null)
        {
            if (RunState == VlcRunState.Running)
                await OpenAsync(deferredUri, enqueue: false, subtitle: null, audioTrack: null, subtitleTrack: null, ct).ConfigureAwait(false);
            else
                _log.LogWarning("Cannot open {Uri}: VLC HTTP interface is not available", deferredUri);
        }
    }

    private VlcLaunchOptions LaunchOptions => new(_settings.VlcHttpHost, _settings.VlcHttpPort, _settings.VlcHttpPassword ?? "",
        _settings.VlcRcPort, _settings.LaunchFullscreen, _settings.VlcExtraArgs);

    /// <summary>
    /// Close all VLC instances (including those open without control) and start again with control.
    /// Without <paramref name="uri"/> VLC opens with the same file: the current item (if VLC is under control)
    /// or the file from the process command line. The playback position is lost.
    /// </summary>
    public async Task RestartAsync(string? uri, int? displayIndex, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(uri))
        {
            uri = RunState == VlcRunState.Running ? LastState?.Item?.Uri : null;
            uri ??= ReadMediaFromRunningProcess();
        }
        _log.LogInformation("Restarting VLC with phone control; media: {Media}", string.IsNullOrEmpty(uri) ? "(none)" : uri);

        if (RunState == VlcRunState.Running)
        {
            try
            {
                await _rc.QuitAsync(ct).ConfigureAwait(false);
                await WaitForExitAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            }
            catch (VlcException ex)
            {
                _log.LogDebug("RC quit failed: {Error}", ex.Message);
            }
        }
        foreach (var p in VlcLocator.RunningProcesses())
        {
            try { p.CloseMainWindow(); } catch { }
            p.Dispose();
        }
        if (!await WaitForExitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false))
        {
            foreach (var p in VlcLocator.RunningProcesses())
            {
                _log.LogWarning("VLC {Pid} did not exit gracefully; killing", p.Id);
                try { p.Kill(); } catch { }
                p.Dispose();
            }
            if (!await WaitForExitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false))
                throw ProtocolException.VlcError(ProtocolText.VlcCloseFailed);
        }

        // The poll will see that there is no process (NotRunning) and write vlcrc; then a normal start.
        await PollOnceAsync(ct).ConfigureAwait(false);
        TryConfigureVlcrc();
        Installation ??= VlcLocator.Find(_settings.VlcPath);
        await StartProcessAsync(uri, displayIndex, ct).ConfigureAwait(false);
    }

    /// <summary>The file/URI from the command line of the running vlc.exe (for example, opened by double-clicking a file).</summary>
    private string? ReadMediaFromRunningProcess()
    {
        var procs = VlcLocator.RunningProcesses();
        try
        {
            foreach (var p in procs)
            {
                var cmd = ProcessCommandLine.TryGet(p.Id);
                if (cmd is null) continue;
                var media = VlcCommandLine.ExtractMedia(ProcessCommandLine.Split(cmd));
                _log.LogDebug("VLC {Pid} command line media: {Media}", p.Id, media ?? "(none)");
                if (media is not null) return media;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Reading VLC command line failed");
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
        return null;
    }

    /// <summary>Close VLC: RC <c>quit</c>, then <c>vlc://quit</c> over HTTP, then closing the window.</summary>
    public async Task QuitAsync(CancellationToken ct)
    {
        if (RunState == VlcRunState.Running)
        {
            try
            {
                await _rc.QuitAsync(ct).ConfigureAwait(false);
                if (await WaitForExitAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false)) return;
            }
            catch (VlcException ex)
            {
                _log.LogDebug("RC quit failed: {Error}", ex.Message);
            }
            try
            {
                await _http.CommandAsync("in_play", ct, ("input", "vlc://quit")).ConfigureAwait(false);
                if (await WaitForExitAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false)) return;
            }
            catch (VlcException ex)
            {
                _log.LogDebug("vlc://quit failed: {Error}", ex.Message);
            }
        }
        foreach (var p in VlcLocator.RunningProcesses())
        {
            try { p.CloseMainWindow(); } catch { }
            p.Dispose();
        }
        await WaitForExitAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        Poke();
    }

    private static async Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var procs = VlcLocator.RunningProcesses();
            var any = procs.Length > 0;
            foreach (var p in procs) p.Dispose();
            if (!any) return true;
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        return false;
    }
}
