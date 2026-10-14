using Microsoft.Win32;

namespace Sofabird.Companion.Services;

/// <summary>Autostart via <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> (no administrator rights).</summary>
public static class AutostartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Sofabird";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string s && !string.IsNullOrEmpty(s);
    }

    /// <summary>
    /// The installation moved (the script install in <c>Sofabird\app</c> → Setup's <c>Sofabird\current</c>):
    /// an enabled autostart is pointed at this executable.
    /// </summary>
    public static void RepairPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        if (key?.GetValue(ValueName) is not string value || value.Length == 0) return;
        var exe = Environment.ProcessPath ?? Application.ExecutablePath;
        if (!value.Contains(exe, StringComparison.OrdinalIgnoreCase)) SetEnabled(true);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
        {
            var exe = Environment.ProcessPath ?? Application.ExecutablePath;
            key.SetValue(ValueName, $"\"{exe}\" --autostart");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
