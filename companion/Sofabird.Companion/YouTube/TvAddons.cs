using Microsoft.Extensions.Logging;
using Sofabird.Core.YouTube;

namespace Sofabird.Companion.YouTube;

/// <summary>An optional add-on of the TV browser, from addons.mozilla.org (<see cref="AmoSlug"/>).</summary>
public sealed record TvAddon(string Id, string AmoSlug, string Name)
{
    /// <summary>Blocks ads (GPL-3.0).</summary>
    public static readonly TvAddon UBlock = new("uBlock0@raymondhill.net", "ublock-origin", "uBlock Origin");

    /// <summary>Skips sponsor segments, intros and the like, as marked by its community (LGPL-3.0).</summary>
    public static readonly TvAddon SponsorBlock = new("sponsorBlocker@ajay.app", "sponsorblock", "SponsorBlock");

    public static readonly IReadOnlyList<TvAddon> All = [UBlock, SponsorBlock];

    public Uri Download => new($"https://addons.mozilla.org/firefox/downloads/latest/{AmoSlug}/latest.xpi");
}

/// <summary>
/// Add-ons of the TV browser besides our own extension, each opt-in (the setup window or the phone): uBlock Origin and
/// SponsorBlock. One is downloaded once from addons.mozilla.org (signed by Mozilla; they are not shipped with the
/// companion), kept in the data folder and put into the profile's <c>extensions</c> folder before the browser starts;
/// Firefox then keeps it up to date itself. Turning one off removes it from the profile.
/// </summary>
public sealed class TvAddons : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    private readonly string _stagingDir;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _download = new(1, 1);

    public TvAddons(string dataDir, ILogger<TvAddons> log)
    {
        _stagingDir = Path.Combine(dataDir, "tv-addons");
        _log = log;
    }

    private string Staged(TvAddon addon) => Path.Combine(_stagingDir, addon.Id + ".xpi");

    /// <summary>The add-on has been downloaded (the profile gets it at the browser's next start).</summary>
    public bool Has(TvAddon addon) => File.Exists(Staged(addon));

    /// <summary>Downloads the add-on unless it is here already; <c>false</c> when the download failed.</summary>
    public async Task<bool> EnsureAsync(TvAddon addon, CancellationToken ct)
    {
        await _download.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Has(addon)) return true;
            _log.LogInformation("Downloading {Addon} from addons.mozilla.org", addon.Name);
            using var request = new HttpRequestMessage(HttpMethod.Get, addon.Download);
            request.Headers.UserAgent.ParseAdd("Sofabird/" + AppVersion.Informational.Split('+')[0]);
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            using (var check = new MemoryStream(bytes))
            {
                var package = AddonPackage.Inspect(check);
                if (package is null || package.Id != addon.Id || !package.MozillaSigned)
                {
                    _log.LogWarning("The download is not a signed {Addon} ({Bytes} bytes)", addon.Name, bytes.Length);
                    return false;
                }
                _log.LogInformation("{Addon} {Version} downloaded", addon.Name, package.Version);
            }
            Directory.CreateDirectory(_stagingDir);
            var temp = Staged(addon) + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(false);
            File.Move(temp, Staged(addon), overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            _log.LogWarning(ex, "Downloading {Addon} failed", addon.Name);
            return false;
        }
        finally
        {
            _download.Release();
        }
    }

    /// <summary>
    /// Before the browser starts: each add-on into the profile when it is on (only when it is not there, so the version
    /// Firefox updated it to is kept), out of it otherwise.
    /// </summary>
    public void ApplyTo(string profileDir, Func<TvAddon, bool> enabled)
    {
        foreach (var addon in TvAddon.All)
        {
            var target = Path.Combine(profileDir, "extensions", addon.Id + ".xpi");
            try
            {
                if (enabled(addon) && Has(addon) && !File.Exists(target))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(Staged(addon), target);
                    _log.LogInformation("{Addon}: installed into the TV profile", addon.Name);
                }
                else if (!enabled(addon) && File.Exists(target))
                {
                    File.Delete(target);
                    _log.LogInformation("{Addon}: removed from the TV profile", addon.Name);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(ex, "Changing {Addon} in the TV profile failed", addon.Name);
            }
        }
    }

    public void Dispose() => _download.Dispose();
}
