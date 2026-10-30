using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Sofabird.Companion.Interop;
using Sofabird.Companion.Setup;
using Sofabird.Core.Vlc;

namespace Sofabird.Companion.Vlc;

public enum VlcInstallStep { Downloading, Installing }

/// <summary>
/// Installs VLC media player from the setup window: the newest 3.0.x from VideoLAN's server (<see cref="VlcDownload"/>),
/// checked against the SHA-256 VideoLAN publishes and to be signed by VideoLAN, installed silently. VLC installs for
/// all users into Program Files, so Windows asks for administrator rights once.
/// </summary>
public sealed class VlcInstaller
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);
    /// <summary>The user said no to the administrator prompt.</summary>
    private const int ErrorCancelled = 1223;
    private readonly ILogger _log;

    public VlcInstaller(ILogger<VlcInstaller> log) => _log = log;

    /// <summary>Done when VLC is installed now; Cancelled when the administrator prompt was declined; else Failed with why.</summary>
    public async Task<StepResult> InstallAsync(IProgress<VlcInstallStep>? progress, CancellationToken ct)
    {
        var folder = Path.Combine(Path.GetTempPath(), "Sofabird");
        string? setup = null;
        try
        {
            progress?.Report(VlcInstallStep.Downloading);
            var version = VlcDownload.LatestVersion(await Http.GetStringAsync(VlcDownload.Index, ct).ConfigureAwait(false));
            if (version is null) return Failed("no VLC 3.0 release found at videolan.org");
            var name = VlcDownload.InstallerName(version);
            var expected = VlcDownload.ParseChecksum(await Http.GetStringAsync(VlcDownload.ChecksumUrl(version), ct).ConfigureAwait(false), name);
            if (expected is null) return Failed("VideoLAN's checksum could not be read");

            Directory.CreateDirectory(folder);
            setup = Path.Combine(folder, name);
            _log.LogInformation("Downloading VLC {Version}: {Url}", version, VlcDownload.InstallerUrl(version));
            using (var response = await Http.GetAsync(VlcDownload.InstallerUrl(version), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var file = File.Create(setup);
                await response.Content.CopyToAsync(file, ct).ConfigureAwait(false);
            }
            string actual;
            await using (var file = File.OpenRead(setup))
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false));
            if (actual != expected) return Failed("the download does not match VideoLAN's checksum");
            var signer = WinTrust.TrustedSigner(setup);
            if (signer != VlcDownload.Publisher) return Failed($"the download is not signed by {VlcDownload.Publisher} ({signer ?? "unsigned"})");

            progress?.Report(VlcInstallStep.Installing);
            var psi = new ProcessStartInfo(setup, "/S") { UseShellExecute = true, Verb = "runas" };
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("the installer did not start");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(InstallTimeout);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (VlcLocator.Find(null) is not { } installed) return Failed($"the installer exited with code {process.ExitCode}");
            _log.LogInformation("VLC {Version} installed: {Path}", installed.Version, installed.ExePath);
            return StepResult.Done;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            _log.LogInformation("Installing VLC: the administrator prompt was declined");
            return new StepResult(StepOutcome.Cancelled);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Failed("it took too long");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or Win32Exception
            or InvalidOperationException)
        {
            _log.LogWarning(ex, "Installing VLC failed");
            return new StepResult(StepOutcome.Failed, ex.Message);
        }
        finally
        {
            if (setup is not null)
            {
                try { File.Delete(setup); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private StepResult Failed(string why)
    {
        _log.LogWarning("Installing VLC failed: {Why}", why);
        return new StepResult(StepOutcome.Failed, why);
    }
}
