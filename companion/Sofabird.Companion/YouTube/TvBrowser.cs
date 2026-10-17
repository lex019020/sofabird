using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Sofabird.Companion.Interop;

namespace Sofabird.Companion.YouTube;

/// <summary>
/// The TV browser: Firefox (or LibreWolf) with a profile of its own (<c>tv-browser</c> in the data folder), separate from
/// the user's everyday browser. It shows YouTube in kiosk mode on the TV; the user signs in to it once from the setup
/// window. Firefox keys its remote handling by the profile path, so it runs next to the user's own Firefox.
/// </summary>
public sealed class TvBrowser
{
    /// <summary>Firefox's top-level window class (also LibreWolf's).</summary>
    public const string WindowClass = "MozillaWindowClass";

    /// <summary>
    /// The profile's preferences, written before every start: no first-run pages, autoplay allowed, no session restore,
    /// no fullscreen warning, a link from the phone replaces the page (no pile of tabs), its own taskbar/media identity,
    /// no "open at sign-in" (Firefox turns it on by itself once per profile); add-ons placed in the profile's
    /// <c>extensions</c> folder (the TV extension, uBlock Origin) are enabled without asking, and a page may go
    /// fullscreen without a key press (the extension does that).
    /// </summary>
    private static readonly string[] Preferences =
    [
        "user_pref(\"media.autoplay.default\", 0);",
        "user_pref(\"media.autoplay.blocking_policy\", 0);",
        "user_pref(\"browser.shell.checkDefaultBrowser\", false);",
        "user_pref(\"browser.aboutwelcome.enabled\", false);",
        "user_pref(\"browser.startup.homepage_override.mstone\", \"ignore\");",
        "user_pref(\"browser.sessionstore.resume_from_crash\", false);",
        "user_pref(\"full-screen-api.warning.timeout\", 0);",
        "user_pref(\"taskbar.grouping.useprofile\", true);",
        "user_pref(\"browser.translations.automaticallyPopup\", false);",
        "user_pref(\"browser.link.open_newwindow.override.external\", 1);",
        "user_pref(\"browser.startup.windowsLaunchOnLogin.enabled\", false);",
        "user_pref(\"browser.startup.windowsLaunchOnLogin.alreadyApplied\", true);",
        "user_pref(\"browser.startup.windowsLaunchOnLogin.disableLaunchOnLoginPrompt\", true);",
        "user_pref(\"extensions.autoDisableScopes\", 14);",
        "user_pref(\"extensions.startupScanScopes\", 1);",
        "user_pref(\"full-screen-api.allow-trusted-requests-only\", false);",
    ];

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly ILogger _log;
    private readonly string? _overridePath;
    private readonly TvAddons _addons;
    private readonly Func<TvAddon, bool> _addonOn;
    private DateTime _runningCheckedAt;
    private bool _running;

    public TvBrowser(string dataDir, string? overridePath, TvAddons addons, Func<TvAddon, bool> addonOn, ILogger<TvBrowser> log)
    {
        _log = log;
        _overridePath = overridePath;
        _addons = addons;
        _addonOn = addonOn;
        ProfileDir = Path.Combine(dataDir, "tv-browser");
        Refresh();
    }

    public string ProfileDir { get; }

    /// <summary>firefox.exe / librewolf.exe, or <c>null</c> when none is installed.</summary>
    public string? ExePath { get; private set; }

    /// <summary>Looks for the browser again (after the setup window installed Firefox).</summary>
    public void Refresh()
    {
        string[] candidates =
        [
            _overridePath ?? "",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mozilla Firefox", "firefox.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Mozilla Firefox", "firefox.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Mozilla Firefox", "firefox.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LibreWolf", "librewolf.exe"),
        ];
        ExePath = candidates.FirstOrDefault(p => p.Length > 0 && File.Exists(p));
        if (ExePath is not null) _log.LogInformation("TV browser: {Path}", ExePath);
    }

    private string ProcessName => Path.GetFileNameWithoutExtension(ExePath ?? "firefox");

    /// <summary>Firefox installed per user for the TV (the setup window's place for it), not the user's own one.</summary>
    private bool IsTvInstall => ExePath is not null && ExePath.StartsWith(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Firefox turns "open at sign-in" on by itself the first time a profile runs (a <c>Mozilla-Firefox-…</c> value
    /// with <c>-os-autostart</c> under Run): at the next sign-in a default-profile window with the welcome page would
    /// open on the PC. Removed for the Firefox installed for the TV; the user's own Firefox is left alone.
    /// </summary>
    public void RemoveLaunchOnLogin()
    {
        if (!IsTvInstall) return;
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (run is null) return;
            foreach (var name in run.GetValueNames())
            {
                if (!name.StartsWith("Mozilla-Firefox-", StringComparison.Ordinal) || run.GetValue(name) is not string cmd) continue;
                if (!cmd.Contains(ExePath!, StringComparison.OrdinalIgnoreCase) || !cmd.Contains("-os-autostart", StringComparison.Ordinal)) continue;
                run.DeleteValue(name, throwOnMissingValue: false);
                _log.LogInformation("TV browser: removed Firefox's start at sign-in ({Name})", name);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _log.LogWarning(ex, "Removing Firefox's start at sign-in failed");
        }
    }

    /// <summary>Whether the TV profile runs (cached for a second: the state asks often).</summary>
    public bool IsRunning()
    {
        if (DateTime.UtcNow - _runningCheckedAt < TimeSpan.FromSeconds(1)) return _running;
        _running = OurProcesses().Count > 0;
        _runningCheckedAt = DateTime.UtcNow;
        return _running;
    }

    /// <summary>Opens <paramref name="url"/> on the TV: starts the profile in kiosk mode, or loads it in the open one.</summary>
    public void Open(string url)
    {
        var exe = ExePath ?? throw new InvalidOperationException("No TV browser");
        var running = OurProcesses().Count > 0;
        if (!running) PrepareProfile();
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        psi.ArgumentList.Add("-profile");
        psi.ArgumentList.Add(ProfileDir);
        if (!running) psi.ArgumentList.Add("-kiosk");
        psi.ArgumentList.Add(url);
        _log.LogInformation("TV browser: {Mode} {Url}", running ? "loading" : "starting with", url);
        using var _ = Process.Start(psi);
        _runningCheckedAt = DateTime.MinValue;
    }

    /// <summary>Opens the profile in a normal window on the PC's own screen, for signing in (no kiosk, no remote control).</summary>
    public void OpenForSignIn(string url)
    {
        var exe = ExePath ?? throw new InvalidOperationException("No TV browser");
        if (OurProcesses().Count == 0) PrepareProfile();
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        foreach (var a in new[] { "-profile", ProfileDir, url }) psi.ArgumentList.Add(a);
        using var _ = Process.Start(psi);
        _runningCheckedAt = DateTime.MinValue;
    }

    /// <summary>The largest visible browser window of the TV profile.</summary>
    public IntPtr FindWindow()
    {
        var best = IntPtr.Zero;
        long bestArea = 0;
        foreach (var pid in OurProcesses())
        {
            foreach (var h in ForegroundWindow.WindowsOf(pid))
            {
                if (ForegroundWindow.ClassName(h) != WindowClass || !NativeMethods.GetWindowRect(h, out var r)) continue;
                long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                if (area > bestArea)
                {
                    bestArea = area;
                    best = h;
                }
            }
        }
        return best;
    }

    /// <summary>The title of the TV browser's window ("&lt;video&gt; - YouTube — Mozilla Firefox"), or <c>null</c>.</summary>
    public string? WindowTitle()
    {
        var h = FindWindow();
        if (h == IntPtr.Zero) return null;
        var length = NativeMethods.GetWindowTextLength(h);
        if (length <= 0) return null;
        var buffer = new char[length + 1];
        var copied = NativeMethods.GetWindowText(h, buffer, buffer.Length);
        return new string(buffer, 0, Math.Max(0, copied));
    }

    /// <summary>Closes the TV profile's windows (the browser exits; the user's own browser is untouched).</summary>
    public void Close()
    {
        foreach (var pid in OurProcesses())
            foreach (var h in ForegroundWindow.WindowsOf(pid))
                if (ForegroundWindow.ClassName(h) == WindowClass) NativeMethods.PostMessage(h, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        _runningCheckedAt = DateTime.MinValue;
    }

    /// <summary>Browser processes started with the TV profile (by their command line).</summary>
    private List<int> OurProcesses()
    {
        var list = new List<int>();
        if (ExePath is null) return list;
        foreach (var p in Process.GetProcessesByName(ProcessName))
        {
            using (p)
            {
                if (ProcessCommandLine.TryGet(p.Id) is { } cmd && cmd.Contains(ProfileDir, StringComparison.OrdinalIgnoreCase))
                    list.Add(p.Id);
            }
        }
        return list;
    }

    /// <summary>Before the browser starts: its preferences, the TV extension, the optional add-ons, no start at sign-in.</summary>
    private void PrepareProfile()
    {
        WritePreferences();
        TvExtension.InstallInto(ProfileDir, _log);
        _addons.ApplyTo(ProfileDir, _addonOn);
        RemoveLaunchOnLogin();
    }

    private void WritePreferences()
    {
        try
        {
            Directory.CreateDirectory(ProfileDir);
            File.WriteAllText(Path.Combine(ProfileDir, "user.js"), string.Join("\n", Preferences) + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Writing the TV browser's preferences failed");
        }
    }
}
