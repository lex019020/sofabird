namespace VlcRemote.Companion;

/// <summary>
/// Companion folders. Default: <c>%LocalAppData%\VlcRemote</c>; overridden by the
/// <c>--data-dir &lt;path&gt;</c> argument or the <c>VLCREMOTE_DATA_DIR</c> variable (for tests).
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string dataDir)
    {
        DataDir = dataDir;
        LogsDir = Path.Combine(dataDir, "logs");
        SettingsFile = Path.Combine(dataDir, "settings.json");
        PairedDevicesFile = Path.Combine(dataDir, "paired.json");
        HistoryFile = Path.Combine(dataDir, "history.json");
    }

    public string DataDir { get; }
    public string LogsDir { get; }
    public string SettingsFile { get; }
    public string PairedDevicesFile { get; }
    /// <summary>Watch history and per-folder track choices.</summary>
    public string HistoryFile { get; }

    public static AppPaths Resolve(string[] args)
    {
        string? dir = null;
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].Equals("--data-dir", StringComparison.OrdinalIgnoreCase))
                dir = args[i + 1];
        dir ??= Environment.GetEnvironmentVariable("VLCREMOTE_DATA_DIR");
        if (string.IsNullOrWhiteSpace(dir))
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VlcRemote");
        var paths = new AppPaths(Path.GetFullPath(dir));
        Directory.CreateDirectory(paths.DataDir);
        Directory.CreateDirectory(paths.LogsDir);
        return paths;
    }
}
