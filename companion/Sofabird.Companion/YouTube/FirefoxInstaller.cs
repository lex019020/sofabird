using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Sofabird.Companion.Interop;
using Sofabird.Core.YouTube;

namespace Sofabird.Companion.YouTube;

public enum FirefoxInstallStep { Downloading, Installing }

/// <summary>
/// Installs Firefox for the TV from the setup window: the latest release from Mozilla's download service in Windows'
/// language, checked to be signed by Mozilla Corporation, installed silently for the current user into
/// <c>%LOCALAPPDATA%\Mozilla Firefox</c> (no desktop or taskbar shortcuts, no maintenance service).
/// </summary>
public sealed class FirefoxInstaller
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);
    private readonly ILogger _log;

    public FirefoxInstaller(ILogger<FirefoxInstaller> log) => _log = log;

    public static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mozilla Firefox");

    /// <summary><c>null</c> when Firefox is installed, otherwise what went wrong (for the log and the window).</summary>
    public async Task<string?> InstallAsync(IProgress<FirefoxInstallStep>? progress, CancellationToken ct)
    {
        var folder = Path.Combine(Path.GetTempPath(), "Sofabird");
        var setup = Path.Combine(folder, "Firefox Setup.exe");
        try
        {
            Directory.CreateDirectory(folder);
            progress?.Report(FirefoxInstallStep.Downloading);
            var url = FirefoxDownload.Url(CultureInfo.CurrentUICulture);
            _log.LogInformation("Downloading Firefox: {Url}", url);
            using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var file = File.Create(setup);
                await response.Content.CopyToAsync(file, ct).ConfigureAwait(false);
            }

            if (!SignedByMozilla(setup)) return "the download is not signed by " + FirefoxDownload.Publisher;

            progress?.Report(FirefoxInstallStep.Installing);
            var psi = new ProcessStartInfo(setup) { UseShellExecute = false };
            foreach (var a in new[]
            {
                "/S", "/InstallDirectoryPath=" + InstallDir, "/DesktopShortcut=false", "/TaskbarShortcut=false",
                "/PrivateBrowsingShortcut=false", "/MaintenanceService=false", "/PreventRebootRequired=true",
            })
                psi.ArgumentList.Add(a);
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("the installer did not start");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(InstallTimeout);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var exe = Path.Combine(InstallDir, "firefox.exe");
            if (process.ExitCode != 0 || !File.Exists(exe)) return $"the installer exited with code {process.ExitCode}";
            _log.LogInformation("Firefox installed: {Path}", exe);
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return "the installer did not finish in time";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _log.LogWarning(ex, "Installing Firefox failed");
            return ex.Message;
        }
        finally
        {
            try { File.Delete(setup); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>A valid, trusted Authenticode signature whose signer is Mozilla Corporation.</summary>
    private bool SignedByMozilla(string path)
    {
        if (!WinTrust.IsTrusted(path))
        {
            _log.LogWarning("The Firefox installer's signature is not valid");
            return false;
        }
        try
        {
#pragma warning disable SYSLIB0057 // the signer of a signed file: there is no loader replacement for this
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            var subject = cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            var organization = cert.SubjectName.Name;
            var ok = organization.Contains("O=" + FirefoxDownload.Publisher, StringComparison.Ordinal);
            if (!ok) _log.LogWarning("The Firefox installer is signed by {Subject}", subject);
            return ok;
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            _log.LogWarning(ex, "The Firefox installer's signer could not be read");
            return false;
        }
    }
}
