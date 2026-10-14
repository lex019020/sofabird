using System.Diagnostics;
using Microsoft.Win32;

namespace Sofabird.Companion.Vlc;

public sealed record VlcInstallation(string ExePath, string InstallDir, string? Version)
{
    public string LuaHttpDir => Path.Combine(InstallDir, "lua", "http");
}

/// <summary>Finds the installed VLC: the <c>HKLM\SOFTWARE\VideoLAN\VLC</c> registry key, then Program Files.</summary>
public static class VlcLocator
{
    public static VlcInstallation? Find(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return Describe(overridePath);

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\VideoLAN\VLC");
            var dir = key?.GetValue("InstallDir") as string;
            if (!string.IsNullOrEmpty(dir))
            {
                var exe = Path.Combine(dir, "vlc.exe");
                if (File.Exists(exe))
                    return Describe(exe, key?.GetValue("Version") as string);
            }
        }
        catch
        {
            // no access to the registry — move on
        }

        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            if (string.IsNullOrEmpty(root)) continue;
            var exe = Path.Combine(root, "VideoLAN", "VLC", "vlc.exe");
            if (File.Exists(exe)) return Describe(exe);
        }
        return null;
    }

    private static VlcInstallation Describe(string exe, string? registryVersion = null)
    {
        string? version = registryVersion;
        if (string.IsNullOrWhiteSpace(version))
        {
            try
            {
                var fvi = FileVersionInfo.GetVersionInfo(exe);
                version = fvi.ProductVersion ?? fvi.FileVersion;
            }
            catch
            {
                version = null;
            }
        }
        return new VlcInstallation(exe, Path.GetDirectoryName(exe)!, version?.Trim());
    }

    /// <summary>All <c>vlc.exe</c> processes (including other instances).</summary>
    public static Process[] RunningProcesses()
    {
        try { return Process.GetProcessesByName("vlc"); }
        catch { return []; }
    }

    /// <summary>Whether any <c>vlc.exe</c> is running.</summary>
    public static bool IsAnyRunning()
    {
        var procs = RunningProcesses();
        foreach (var p in procs) p.Dispose();
        return procs.Length > 0;
    }
}
