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

namespace VlcRemote.Companion;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var paths = AppPaths.Resolve(args);
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
            MessageBox.Show("VLC Remote Companion уже запущен (иконка в трее).", "VLC Remote", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }

        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => Log.Error(e.Exception, "Unhandled UI exception");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Warning(e.Exception, "Unobserved task exception"); e.SetObserved(); };

        using var lf = new SerilogLoggerFactory(Log.Logger, dispose: false);
        var log = lf.CreateLogger("Program");
        log.LogInformation("VLC Remote Companion {Version} starting; data dir {Dir}",
            typeof(Program).Assembly.GetName().Version, paths.DataDir);

        try
        {
            return Run(paths, lf, log);
        }
        catch (Exception ex)
        {
            log.LogCritical(ex, "Fatal error");
            MessageBox.Show("VLC Remote Companion: критическая ошибка.\n" + ex.Message, "VLC Remote", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static int Run(AppPaths paths, ILoggerFactory lf, Microsoft.Extensions.Logging.ILogger log)
    {
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

        var startupCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            web.StartAsync(startupCts.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            log.LogCritical(ex, "Kestrel failed to start on {Addr}:{Port}", settings.ListenAddress, settings.WsPort);
            MessageBox.Show($"Не удалось открыть порт {settings.WsPort}: {ex.Message}\nИзмените wsPort в {paths.SettingsFile}.",
                "VLC Remote", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 3;
        }
        beacon.Start();
        if (settings.AdvertiseDnsSd)
            _ = dnssd.RegisterAsync(Environment.MachineName, settings.WsPort, settings.InstallationId, startupCts.Token);

        vlc.Start();
        if (settings.AutoLaunchVlc && installation is not null)
            _ = Task.Run(async () =>
            {
                try { await vlc.LaunchAsync(null, settings.PreferredDisplay, CancellationToken.None); }
                catch (Exception ex) { log.LogWarning(ex, "Auto-launch of VLC failed"); }
            });

        async Task ShutdownAsync()
        {
            log.LogInformation("Shutting down");
            power.KeepAwakeEnabled = false;
            dnssd.Dispose();
            await beacon.DisposeAsync();
            await sessions.CloseAllAsync();
            await web.DisposeAsync();
            await vlc.DisposeAsync();
            sessions.Dispose();
        }

        using var tray = new TrayContext(ui, paths, settings, settingsStore, vlc, pairing, display, sessions, host, power,
            ShutdownAsync, lf.CreateLogger<TrayContext>());
        Application.Run(tray);
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
