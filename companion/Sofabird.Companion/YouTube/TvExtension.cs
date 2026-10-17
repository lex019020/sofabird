using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Sofabird.Companion.YouTube;

/// <summary>
/// The Sofabird TV extension (<c>companion/tv-extension</c>, signed by Mozilla, embedded here): it reads YouTube's
/// player in the TV browser and carries out the companion's commands. Firefox starts <c>Sofabird.exe</c> as its native
/// messaging host (<see cref="TvExtensionHost"/>), which relays to the running companion over a named pipe of the
/// current user (<see cref="TvExtensionBridge"/>).
/// </summary>
public static class TvExtension
{
    public const string ExtensionId = "tv@sofabird.app";
    public const string HostName = "app.sofabird.tv";
    private const string ResourceName = "tv-extension.xpi";
    private const string HostsKey = @"Software\Mozilla\NativeMessagingHosts\" + HostName;

    /// <summary>The pipe between the host and the companion: per user (the pipe namespace is shared by all sessions).</summary>
    public static string PipeName
    {
        get
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
            return "Sofabird.TvExtension." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid)))[..16];
        }
    }

    /// <summary>Firefox starts the host as <c>Sofabird.exe &lt;manifest path&gt; tv@sofabird.app</c>.</summary>
    public static bool IsHostStart(string[] args) => args.Contains(ExtensionId, StringComparer.Ordinal);

    /// <summary>
    /// Registers <paramref name="exePath"/> as the native messaging host for the extension (per user: a manifest in the
    /// data folder and its path under <c>HKCU\Software\Mozilla\NativeMessagingHosts</c>). Idempotent.
    /// </summary>
    public static void Register(string exePath, string dataDir, ILogger log)
    {
        try
        {
            var manifestPath = Path.Combine(dataDir, "tv-extension-host.json");
            var manifest = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["name"] = HostName,
                ["description"] = "Sofabird: the TV extension's link to the companion",
                ["path"] = exePath,
                ["type"] = "stdio",
                ["allowed_extensions"] = new[] { ExtensionId },
            });
            if (!File.Exists(manifestPath) || File.ReadAllText(manifestPath) != manifest)
            {
                Directory.CreateDirectory(dataDir);
                File.WriteAllText(manifestPath, manifest);
            }
            using var key = Registry.CurrentUser.CreateSubKey(HostsKey);
            if (key.GetValue(null) as string != manifestPath)
            {
                key.SetValue(null, manifestPath);
                log.LogInformation("TV extension: native messaging host registered ({Path})", manifestPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            log.LogWarning(ex, "Registering the TV extension's host failed");
        }
    }

    /// <summary>Removes the host registration (uninstall).</summary>
    public static void Unregister()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(HostsKey, throwOnMissingSubKey: false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }

    /// <summary>
    /// Puts the signed extension into the profile's <c>extensions</c> folder (Firefox installs it at the next start;
    /// <c>user.js</c> enables add-ons placed there without asking). Only while the browser is closed.
    /// </summary>
    public static void InstallInto(string profileDir, ILogger log)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            log.LogWarning("TV extension: not embedded in this build");
            return;
        }
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        var target = Path.Combine(profileDir, "extensions", ExtensionId + ".xpi");
        try
        {
            if (File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, bytes);
            log.LogInformation("TV extension: installed into the TV profile ({Bytes} bytes)", bytes.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Installing the TV extension failed");
        }
    }
}
