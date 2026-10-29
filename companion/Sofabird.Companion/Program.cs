using System.Net;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;
using Velopack;
using Sofabird.Companion.Net;
using Sofabird.Companion.Services;
using Sofabird.Companion.Setup;
using Sofabird.Companion.Tray;
using Sofabird.Companion.Vlc;
using Sofabird.Companion.YouTube;
using Sofabird.Core.History;
using Sofabird.Core.Lua;
using Sofabird.Core.Pairing;
using Sofabird.Core.Protocol;
using Sofabird.Core.Setup;
using Sofabird.Core.Updates;
using Sofabird.Core.YouTube;
using Sofabird.Companion.Localization;

namespace Sofabird.Companion;

internal static class Program
{
    /// <summary>How long Windows' sleep waits for "sleeping" to reach the phones (it allows about two seconds).</summary>
    private static readonly TimeSpan SleepNoticeTimeout = TimeSpan.FromSeconds(1);

    [STAThread]
    private static int Main(string[] args)
    {
        // First: Velopack's install/update/uninstall hooks run here and exit; a normal start falls through.
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ =>
            {
                AutostartService.SetEnabled(false);
                TvExtension.Unregister();
            })
            .Run();

        // Firefox starts us as the TV extension's native messaging host: relay and exit, nothing else.
        if (TvExtension.IsHostStart(args)) return TvExtensionHost.Run();

        var paths = AppPaths.Resolve(args);
        var quitEventName = @"Local\Sofabird_Quit_" + Hash(paths.DataDir);
        if (args.Contains("--shutdown", StringComparer.OrdinalIgnoreCase))
            return RequestShutdown(quitEventName);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(Path.Combine(paths.LogsDir, "companion-.log"),
                formatProvider: System.Globalization.CultureInfo.InvariantCulture,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        // Elevated firewall helper (started by the setup window with one UAC prompt): runs next to the main instance.
        if (args.Contains(SetupElevation.HelperArg, StringComparer.OrdinalIgnoreCase))
        {
            using var helperLf = new SerilogLoggerFactory(Log.Logger, dispose: false);
            try { return SetupElevation.RunHelper(args, helperLf.CreateLogger("FirewallHelper")); }
            finally { Log.CloseAndFlush(); }
        }

        using var mutex = new Mutex(initiallyOwned: true, "Local\\Sofabird_" + Hash(paths.DataDir), out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(UiText.AlreadyRunning, "Sofabird", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }

        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => Log.Error(e.Exception, "Unhandled UI exception");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Warning(e.Exception, "Unobserved task exception"); e.SetObserved(); };

        using var lf = new SerilogLoggerFactory(Log.Logger, dispose: false);
        var log = lf.CreateLogger("Program");
        log.LogInformation("Sofabird {Version} starting; data dir {Dir}",
            AppVersion.Informational, paths.DataDir);

        try
        {
            return Run(paths, lf, log, quitEventName);
        }
        catch (Exception ex)
        {
            log.LogCritical(ex, "Fatal error");
            MessageBox.Show(UiText.FatalError(ex.Message), "Sofabird", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    /// <summary>
    /// <c>--shutdown</c>: asks an already running instance (with the same data directory) to exit gracefully
    /// and waits up to 15 s. Used by the deploy script before updating the files.
    /// </summary>
    private static int RequestShutdown(string quitEventName)
    {
        if (!EventWaitHandle.TryOpenExisting(quitEventName, out var evt)) return 0; // not running
        using (evt) evt.Set();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var self = Environment.ProcessId;
        while (DateTime.UtcNow < deadline)
        {
            var others = System.Diagnostics.Process.GetProcessesByName("Sofabird").Where(p => p.Id != self).ToList();
            if (others.Count == 0) return 0;
            Thread.Sleep(250);
        }
        return 4; // did not exit in time
    }

    private static int Run(AppPaths paths, ILoggerFactory lf, Microsoft.Extensions.Logging.ILogger log, string quitEventName)
    {
        using var quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, quitEventName);
        LegacyCompanion.Migrate(paths, Hash, log); // before the settings are read: the first start inherits the old ones
        var settingsStore = new SettingsStore(paths.SettingsFile);
        var settings = settingsStore.Load();

        // Hidden control for marshalling to the UI thread (ApplicationContext has no window).
        var ui = new Control();
        _ = ui.Handle;

        var installation = VlcLocator.Find(settings.VlcPath);
        if (installation is null) log.LogWarning("VLC not found (registry/Program Files)");
        else log.LogInformation("VLC {Version} at {Path}", installation.Version, installation.ExePath);

        var lua = installation is null ? null
            : new LuaPagesInstaller(installation.LuaHttpDir, LuaPagesInstaller.DefaultTargetDir, EmbeddedLuaPages.Load(), lf.CreateLogger<LuaPagesInstaller>());

        var historyStore = new HistoryStore(paths.HistoryFile, logger: lf.CreateLogger<HistoryStore>());
        historyStore.StartMaintenance();
        var history = new HistoryTracker(historyStore, lf.CreateLogger<HistoryTracker>());
        var vlc = new VlcManager(settings, installation, lua, lf, history);
        var pairing = new PairingService(new JsonFileTokenStore(paths.PairedDevicesFile));
        var display = new DisplayService(lf.CreateLogger<DisplayService>());
        using var power = new PowerService(lf.CreateLogger<PowerService>(), a => { if (!ui.IsDisposed) ui.BeginInvoke(a); });
        var audio = new AudioService(lf.CreateLogger<AudioService>());
        var audioWatcher = new AudioDeviceWatcher(lf.CreateLogger<AudioDeviceWatcher>());
        var powerPlan = new PowerPlanService(lf.CreateLogger<PowerPlanService>());
        using var background = new BackgroundWork(lf.CreateLogger<BackgroundWork>());
        using var library = new LibraryService(settings, paths, vlc.History?.Store, background, lf.CreateLogger<LibraryService>());
        using var moonlight = new MoonlightService(lf.CreateLogger<MoonlightService>());
        // Media folders on a mapped drive letter are kept as network paths: the letter is gone after a restart unless
        // it was mapped to reconnect at sign-in.
        settingsStore.Update(s =>
        {
            var roots = s.MediaRoots.Select(Interop.NetworkDrives.ToNetworkPath).ToList();
            if (!roots.SequenceEqual(s.MediaRoots)) s.MediaRoots = roots;
        });
        using var tvAddons = new TvAddons(paths.DataDir, lf.CreateLogger<TvAddons>());
        var tvBrowser = new TvBrowser(paths.DataDir, settings.BrowserPath, tvAddons, settings.IsOn, lf.CreateLogger<TvBrowser>());
        tvBrowser.RemoveLaunchOnLogin(); // before the next sign-in, not only when YouTube opens
        if (tvBrowser.ExePath is not null && Environment.ProcessPath is { } exe)
            TvExtension.Register(exe, paths.DataDir, lf.CreateLogger<TvExtensionBridge>());
        using var tvPage = new TvExtensionBridge(lf.CreateLogger<TvExtensionBridge>());
        tvPage.Start();
        using var youtubeMedia = new YouTubeMediaSession(settings.YouTubeSessionId,
            id => settingsStore.Update(s => s.YouTubeSessionId = id), lf.CreateLogger<YouTubeMediaSession>());
        _ = youtubeMedia.StartAsync();
        using var youtubeHistory = new YouTubeHistory(Path.Combine(paths.DataDir, "youtube-history.json"),
            logger: lf.CreateLogger<YouTubeHistory>());
        using var youtube = new YouTubeService(tvBrowser, youtubeMedia, tvPage, tvAddons, new FirefoxInstaller(lf.CreateLogger<FirefoxInstaller>()),
            youtubeHistory, settings, settingsStore, paths.DataDir, lf.CreateLogger<YouTubeService>());
        using var wake = new WakeOnLanService(lf.CreateLogger<WakeOnLanService>());
        wake.Start();
        var host = new CompanionHost(settings, settingsStore, vlc, pairing, display, power, audio, audioWatcher, background,
            library, moonlight, youtube, youtubeHistory, wake, lf.CreateLogger<CompanionHost>());
        var sessions = new SessionManager(host, lf);
        vlc.StateChanged += sessions.PublishState;
        vlc.Event += sessions.PublishEvent;
        pairing.Paired += d => sessions.PublishEvent(new EventMessage { Name = EventNames.Paired, Device = d.DeviceName });
        // The phones drop the link at once and offer to wake the PC, instead of noticing a missed pong much later.
        power.Suspending += () => sessions.PublishEventAsync(new EventMessage { Name = EventNames.Sleeping }).Wait(SleepNoticeTimeout);
        power.Start();

        var web = new WebServer(settings, host, sessions, vlc, lf.CreateLogger<WebServer>());
        var beacon = new UdpBeaconResponder(IPAddress.Parse(settings.ListenAddress), settings.BeaconPort, host.BuildBeacon, lf.CreateLogger<UdpBeaconResponder>());
        var dnssd = new DnsSdAdvertiser(lf.CreateLogger<DnsSdAdvertiser>());
        var bluetooth = new BluetoothRfcommServer(settings, sessions, lf);
        host.Bluetooth = bluetooth;
        var setup = new SetupBackend(paths, settings, settingsStore, vlc, host, library, bluetooth, powerPlan, youtube, wake, lf.CreateLogger<SetupBackend>());

        TrayContext? tray = null;
        SetupForm? earlySetup = null;
        var quitRequested = false;
        var quitWait = ThreadPool.RegisterWaitForSingleObject(quitEvent, (_, _) =>
        {
            log.LogInformation("Shutdown requested by another process (--shutdown)");
            Volatile.Write(ref quitRequested, true);
            if (Volatile.Read(ref tray) is { } t) t.RequestExit();
            else
            {
                try { ui.BeginInvoke(() => earlySetup?.CloseForExit()); }
                catch (InvalidOperationException) { }
            }
        }, null, Timeout.Infinite, executeOnlyOnce: true);

        // First start (or a new setup item): the "Setup and permissions" window. If the firewall has no rules for the
        // companion/VLC yet, it is shown before the network listeners start: once its rules are applied, Windows does
        // not show its own "allow access" prompt (whose "Cancel" silently creates block rules).
        var showSetup = setup.ShouldShowOnStart;
        var firewall = showSetup ? setup.InspectFirewall() : null;
        var earlyShown = false;
        if (SetupPolicy.DeferListeners(showSetup, firewall))
        {
            log.LogInformation("Showing the setup window before starting the network listeners");
            var form = new SetupForm(setup, firstRun: true);
            form.Shown += (_, _) => form.BringToFrontActivated();
            earlySetup = form;
            // A modal loop, not Application.Run(form): when that main loop ends, WinForms destroys every window of the
            // thread, the hidden `ui` control too, and later marshalling to the UI thread silently runs on the caller.
            form.ShowDialog();
            earlySetup = null;
            form.Dispose();
            showSetup = false;
            earlyShown = true;
            if (Volatile.Read(ref quitRequested))
            {
                quitWait.Unregister(null);
                display.Dispose();
                historyStore.Dispose();
                ui.Dispose();
                log.LogInformation("Exited before start");
                return 0;
            }
        }

        var startupCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            web.StartAsync(startupCts.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            log.LogCritical(ex, "Kestrel failed to start on {Addr}:{Port}", settings.ListenAddress, settings.WsPort);
            MessageBox.Show(UiText.PortFailed(settings.WsPort, ex.Message, paths.SettingsFile),
                "Sofabird", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 3;
        }
        beacon.Start();
        bluetooth.Start(); // background; no adapter / radio off is not an error
        if (settings.AdvertiseDnsSd)
            _ = dnssd.RegisterAsync(Environment.MachineName, settings.WsPort, settings.InstallationId, startupCts.Token);

        vlc.Start();
        audioWatcher.Start();
        library.Start();
        if (settings.AutoLaunchVlc && installation is not null)
            background.Run("Auto-launch of VLC", ct => vlc.LaunchAsync(null, null, ct));

        async Task ShutdownAsync()
        {
            log.LogInformation("Shutting down");
            background.Stop();
            power.KeepAwakeEnabled = false;
            audioWatcher.Dispose();
            dnssd.Dispose();
            await beacon.DisposeAsync();
            await bluetooth.DisposeAsync();
            await sessions.CloseAllAsync();
            await web.DisposeAsync();
            await vlc.DisposeAsync();
            historyStore.Dispose(); // writes the last positions
            host.Displays.Dispose();
            display.Dispose();
            sessions.Dispose();
        }

        // Not while something plays on the TV: a movie in VLC or a video on YouTube.
        var updates = new UpdateService(settings, settingsStore,
            () => vlc.LastState?.Playback.State == "playing" || youtube.Info()?.Playing == true,
            () => Volatile.Read(ref tray)?.RequestExit(), background, lf.CreateLogger<UpdateService>());
        var previousVersion = settings.LastRunVersion;
        var updated = previousVersion is not null && previousVersion != AppVersion.Informational;
        if (previousVersion != AppVersion.Informational) settingsStore.Update(s => s.LastRunVersion = AppVersion.Informational);
        if (UpdatePolicy.ChannelToRemember(settings.UpdateChannel, AppVersion.Informational) is { } channel)
            settingsStore.Update(s => s.UpdateChannel = channel);
        try { AutostartService.RepairPath(); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            log.LogWarning(ex, "Repairing the autostart path failed");
        }

        using var trayContext = new TrayContext(ui, paths, settings, vlc, pairing, display, sessions, host,
            bluetooth, setup, powerPlan, updates, ShutdownAsync, lf.CreateLogger<TrayContext>());
        Volatile.Write(ref tray, trayContext);
        if (updated) trayContext.ShowUpdated();
        updates.Start();
        if (Volatile.Read(ref quitRequested)) trayContext.RequestExit();
        else if (showSetup) trayContext.ShowSetupOnStart();
        else if (!earlyShown) trayContext.CheckFirewallOnStart();
        trayContext.WatchMediaStationPower();
        Application.Run(trayContext);
        quitWait.Unregister(null);
        ui.Dispose();
        log.LogInformation("Exited");
        return 0;
    }

    private static string Hash(string s)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s.ToLowerInvariant()));
        return Convert.ToHexStringLower(bytes)[..12];
    }
}
