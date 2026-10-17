using Microsoft.Extensions.Logging;
using Sofabird.Core.YouTube;

namespace Sofabird.Companion.YouTube;

/// <summary>
/// Add-ons of the TV browser besides our own extension: uBlock Origin, opt-in ("Block ads" in the setup window or on the
/// phone). It is downloaded once from addons.mozilla.org (signed by Mozilla; uBlock Origin is GPL-3.0 and is not
/// shipped with the companion), kept in the data folder and put into the profile's <c>extensions</c> folder before the
/// browser starts; Firefox then keeps it up to date itself. Turning it off removes it from the profile.
/// </summary>
public sealed class TvAddons
{
    public const string UBlockId = "uBlock0@raymondhill.net";
    private const string UBlockUrl = "https://addons.mozilla.org/firefox/downloads/latest/ublock-origin/latest.xpi";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    private readonly string _stagedUBlock;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _download = new(1, 1);

    public TvAddons(string dataDir, ILogger<TvAddons> log)
    {
        _stagedUBlock = Path.Combine(dataDir, "tv-addons", UBlockId + ".xpi");
        _log = log;
    }

    /// <summary>uBlock Origin has been downloaded (the profile gets it at the browser's next start).</summary>
    public bool HasUBlock => File.Exists(_stagedUBlock);

    /// <summary>Downloads uBlock Origin unless it is here already; <c>false</c> when the download failed.</summary>
    public async Task<bool> EnsureUBlockAsync(CancellationToken ct)
    {
        await _download.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (HasUBlock) return true;
            _log.LogInformation("Downloading uBlock Origin from addons.mozilla.org");
            using var request = new HttpRequestMessage(HttpMethod.Get, UBlockUrl);
            request.Headers.UserAgent.ParseAdd("Sofabird/" + AppVersion.Informational.Split('+')[0]);
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            using (var check = new MemoryStream(bytes))
            {
                if (AddonPackage.Inspect(check) is not { Id: UBlockId, MozillaSigned: true } package)
                {
                    _log.LogWarning("The download is not a signed uBlock Origin ({Bytes} bytes)", bytes.Length);
                    return false;
                }
                _log.LogInformation("uBlock Origin {Version} downloaded", package.Version);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_stagedUBlock)!);
            var temp = _stagedUBlock + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(false);
            File.Move(temp, _stagedUBlock, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            _log.LogWarning(ex, "Downloading uBlock Origin failed");
            return false;
        }
        finally
        {
            _download.Release();
        }
    }

    /// <summary>
    /// Before the browser starts: uBlock Origin into the profile when ads are blocked (only when it is not there, so the
    /// version Firefox updated to is kept), out of it otherwise.
    /// </summary>
    public void ApplyTo(string profileDir, bool blockAds)
    {
        var target = Path.Combine(profileDir, "extensions", UBlockId + ".xpi");
        try
        {
            if (blockAds && HasUBlock && !File.Exists(target))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(_stagedUBlock, target);
                _log.LogInformation("uBlock Origin: installed into the TV profile");
            }
            else if (!blockAds && File.Exists(target))
            {
                File.Delete(target);
                _log.LogInformation("uBlock Origin: removed from the TV profile");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Changing uBlock Origin in the TV profile failed");
        }
    }
}
