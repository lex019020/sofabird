using System.Net;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;
using VlcRemote.Companion.Net;
using VlcRemote.Companion.Services;
using VlcRemote.Companion.Setup;
using VlcRemote.Companion.Tray;
using VlcRemote.Companion.Vlc;
using VlcRemote.Core.History;
using VlcRemote.Core.Lua;
using VlcRemote.Core.Pairing;
using VlcRemote.Core.Setup;
using VlcRemote.Companion.Localization;

namespace VlcRemote.Companion;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var paths = AppPaths.Resolve(args);
        var quitEventName = @"Local\VlcRemoteCompanion_Quit_" + Hash(paths.DataDir);
        if (args.Contains("--shutdown", StringComparer.OrdinalIgnoreCase))
            return RequestShutdown(quitEventName);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(Path.Combine(paths.LogsDir, "companion-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        // Elevated firewall helper (started by the setup window with one UAC prompt): runs next to the main instance.
        if (args.Contains(FirewallElevation.HelperArg, StringComparer.OrdinalIgnoreCase))
        {
            using var helperLf = new SerilogLoggerFactory(Log.Logger, dispose: false);
            try { return FirewallElevation.RunHelper(args, helperLf.CreateLogger("FirewallHelper")); }
            finally { Log.CloseAndFlush(); }
        }

        using var mutex = new Mutex(initiallyOwned: true, "Local\\VlcRemoteCompanion_" + Hash(paths.DataDir), out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(UiText.AlreadyRunning, "VLC Remote", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }

        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => Log.Error(e.Exception, "Unhandled UI exception");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Warning(e.Exception, "Unobserved task exception"); e.SetObserved(); };

        using var lf = new SerilogLoggerFactory(Log.Logger, dispose: false);
        var log = lf.CreateLogger("Program");
        log.LogInformation("VLC Remote Companion {Version} starting; data dir {Dir}",
            AppVersion.Informational, paths.DataDir);

        try
        {
            return Run(paths, lf, log, quitEventName);
        }
        catch (Exception ex)
        {
            log.LogCritical(ex, "Fatal error");
            MessageBox.Show(UiText.FatalError(ex.Message), "VLC Remote", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
            var others = System.Diagnostics.Process.GetProcessesByName("VlcRemote.Companion").Where(p => p.Id != self).ToList();
            if (others.Count == 0) return 0;
            Thread.Sleep(250);
        }
        return 4; // did not exit in time
    }

    private static int Run(AppPaths paths, ILoggerFactory lf, Microsoft.Extensions.Logging.ILogger log, string quitEventName)
    {
        using var quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, quitEventName);
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
        var power = new PowerService(lf.CreateLogger<PowerService>(), a => { if (!ui.IsDisposed) ui.BeginInvoke(a); });
        var audio = new AudioService(lf.CreateLogger<AudioService>());
        var host = new CompanionHost(settings, settingsStore, vlc, pairing, display, power, audio, lf.CreateLogger<CompanionHost>());
        var sessions = new SessionManager(host, lf);
        vlc.StateChanged += sessions.PublishState;
        vlc.Event += sessions.PublishEvent;

        var web = new WebServer(settings, host, sessions, vlc, lf.CreateLogger<WebServer>());
        var beacon = new UdpBeaconResponder(IPAddress.Parse(settings.ListenAddress), settings.BeaconPort, host.BuildBeacon, lf.CreateLogger<UdpBeaconResponder>());
        var dnssd = new DnsSdAdvertiser(lf.CreateLogger<DnsSdAdvertiser>());
        var bluetooth = new BluetoothRfcommServer(settings, sessions, lf);
        host.Bluetooth = bluetooth;
        var setup = new SetupBackend(paths, settings, settingsStore, vlc, power, bluetooth, lf.CreateLogger<SetupBackend>());

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
            Application.Run(form);
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
                "VLC Remote", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 3;
        }
        beacon.Start();
        bluetooth.Start(); // background; no adapter / radio off is not an error
        if (settings.AdvertiseDnsSd)
            _ = dnssd.RegisterAsync(Environment.MachineName, settings.WsPort, settings.InstallationId, startupCts.Token);

        vlc.Start();
        if (settings.AutoLaunchVlc && installation is not null)
            _ = Task.Run(async () =>
            {
                try { await vlc.LaunchAsync(null, null, CancellationToken.None); }
                catch (Exception ex) { log.LogWarning(ex, "Auto-launch of VLC failed"); }
            });

        async Task ShutdownAsync()
        {
            log.LogInformation("Shutting down");
            power.KeepAwakeEnabled = false;
            dnssd.Dispose();
            await beacon.DisposeAsync();
            await bluetooth.DisposeAsync();
            await sessions.CloseAllAsync();
            await web.DisposeAsync();
            await vlc.DisposeAsync();
            historyStore.Dispose(); // writes the last positions
            display.Dispose();
            sessions.Dispose();
        }

        using var trayContext = new TrayContext(ui, paths, settings, settingsStore, vlc, pairing, display, sessions, host, power,
            bluetooth, setup, ShutdownAsync, lf.CreateLogger<TrayContext>());
        Volatile.Write(ref tray, trayContext);
        if (Volatile.Read(ref quitRequested)) trayContext.RequestExit();
        else if (showSetup) trayContext.ShowSetupOnStart();
        else if (!earlyShown) trayContext.CheckFirewallOnStart();
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
