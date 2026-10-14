using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Sofabird.Companion.Services;
using Sofabird.Core.Lua;
using Sofabird.Core.Setup;
using Sofabird.Core.Vlc;

namespace Sofabird.Companion.Setup;

/// <summary>
/// Replaces the companion under its earlier name, "VLC Remote Companion", once: carries its settings, history and
/// library metadata over, stops it, moves its autostart to Sofabird, renames its marks in VLC's folders and uninstalls
/// it. Its firewall rules are replaced by the setup window (that needs UAC). Every step is best effort.
/// </summary>
internal static class LegacyCompanion
{
    private const string ProcessName = "VlcRemote.Companion";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "VlcRemoteCompanion";
    private const string QuitEventPrefix = @"Local\VlcRemoteCompanion_Quit_";
    private const string VlcrcBackupSuffix = ".vlcremote-backup";
    private const string LuaVersionFile = "vlcremote.version";

    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string DataDir => Path.Combine(LocalAppData, "VlcRemote");
    private static string UpdateExe => Path.Combine(LocalAppData, "VlcRemoteCompanion", "Update.exe");

    /// <param name="hash">The single-instance hash of a data folder (the old companion named its quit event with it).</param>
    public static void Migrate(AppPaths paths, Func<string, string> hash, ILogger log)
    {
        Try(log, "carry the data over", () =>
        {
            var copied = LegacyData.CopyIfFirstStart(DataDir, paths.DataDir);
            if (copied.Count > 0) log.LogInformation("Carried over from VLC Remote Companion: {Files}", string.Join(", ", copied));
        });
        Try(log, "stop the old companion", () => Stop(hash(Path.GetFullPath(DataDir)), log));
        Try(log, "move the autostart", () => MoveAutostart(log));
        Try(log, "rename the marks in VLC's folders", RenameVlcMarks);
        if (File.Exists(UpdateExe)) _ = Task.Run(() => Try(log, "uninstall the old companion", () => Uninstall(log)));
    }

    private static void Stop(string dataDirHash, ILogger log)
    {
        if (!EventWaitHandle.TryOpenExisting(QuitEventPrefix + dataDirHash, out var quit)) return;
        using (quit) quit.Set();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && Process.GetProcessesByName(ProcessName).Length > 0) Thread.Sleep(250);
        foreach (var p in Process.GetProcessesByName(ProcessName))
            using (p) p.Kill();
        log.LogInformation("Stopped VLC Remote Companion");
    }

    private static void MoveAutostart(ILogger log)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(RunValue) is not string value || value.Length == 0) return;
        key.DeleteValue(RunValue, throwOnMissingValue: false);
        AutostartService.SetEnabled(true);
        log.LogInformation("Autostart moved from VLC Remote Companion to Sofabird");
    }

    private static void RenameVlcMarks()
    {
        var oldBackup = VlcrcConfigurator.DefaultPath + VlcrcBackupSuffix;
        var newBackup = VlcrcConfigurator.DefaultPath + VlcrcConfigurator.BackupSuffix;
        if (File.Exists(oldBackup) && !File.Exists(newBackup)) File.Move(oldBackup, newBackup);
        // Without its own version file Sofabird installs the pages again and writes its file.
        File.Delete(Path.Combine(LuaPagesInstaller.DefaultTargetDir, LuaVersionFile));
    }

    private static void Uninstall(ILogger log)
    {
        using var p = Process.Start(new ProcessStartInfo(UpdateExe, "uninstall --silent") { UseShellExecute = false, CreateNoWindow = true });
        if (p is null) return;
        if (!p.WaitForExit(TimeSpan.FromMinutes(1))) { log.LogWarning("Uninstalling VLC Remote Companion takes too long"); return; }
        log.LogInformation("VLC Remote Companion uninstalled (exit code {Code})", p.ExitCode);
    }

    private static void Try(ILogger log, string what, Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
            or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            log.LogWarning(ex, "Replacing VLC Remote Companion: could not {What}", what);
        }
    }
}
