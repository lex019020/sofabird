using System.Net;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;
using VlcRemote.Companion.Net;
using VlcRemote.Companion.Services;
using VlcRemote.Companion.Tray;
using VlcRemote.Companion.Vlc;
using VlcRemote.Core.Lua;
using VlcRemote.Core.Pairing;
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
    /// <c>--shutdown</c>: просит уже запущенный экземпляр (с тем же каталогом данных) корректно завершиться
    /// и ждёт до 15 с. Используется скриптом развёртывания перед обновлением файлов.
    /// </summary>
    private static int RequestShutdown(string quitEventName)
    {
        if (!EventWaitHandle.TryOpenExisting(quitEventName, out var evt)) return 0; // не запущен
        using (evt) evt.Set();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var self = Environment.ProcessId;
        while (DateTime.UtcNow < deadline)
        {
            var others = System.Diagnostics.Process.GetProcessesByName("VlcRemote.Companion").Where(p => p.Id != self).ToList();
            if (others.Count == 0) return 0;
            Thread.Sleep(250);
        }
        return 4; // не успел завершиться
    }

    private static int Run(AppPaths paths, ILoggerFactory lf, Microsoft.Extensions.Logging.ILogger log, string quitEventName)
    {
        using var quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, quitEventName);
        var settingsStore = new SettingsStore(paths.SettingsFile);
        var settings = settingsStore.Load();

        // Скрытый контрол для маршалинга в UI-поток (у ApplicationContext нет окна).
        var ui = new Control();
        _ = ui.Handle;

        var installation = VlcLocator.Find(settings.VlcPath);
        if (installation is null) log.LogWarning("VLC not found (registry/Program Files)");
        else log.LogInformation("VLC {Version} at {Path}", installation.Version, installation.ExePath);

        var lua = installation is null ? null
            : new LuaPagesInstaller(installation.LuaHttpDir, LuaPagesInstaller.DefaultTargetDir, EmbeddedLuaPages.Load(), lf.CreateLogger<LuaPagesInstaller>());

        var vlc = new VlcManager(settings, installation, lua, lf);
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
            display.Dispose();
            sessions.Dispose();
        }

        using var tray = new TrayContext(ui, paths, settings, settingsStore, vlc, pairing, display, sessions, host, power,
            bluetooth, ShutdownAsync, lf.CreateLogger<TrayContext>());
        var quitWait = ThreadPool.RegisterWaitForSingleObject(quitEvent, (_, _) =>
        {
            log.LogInformation("Shutdown requested by another process (--shutdown)");
            tray.RequestExit();
        }, null, Timeout.Infinite, executeOnlyOnce: true);
        Application.Run(tray);
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
