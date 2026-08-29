using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using FocusMed.Data;
using FocusMed.Launcher.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

namespace FocusMed.Launcher;

public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\FocusMed.Launcher";

    private ServiceProvider? _serviceProvider;
    private PrintJobMonitorService? _monitor;
    private DatabaseService? _databaseService;
    private SiteConfig? _cfg;
    private ChildProcessSupervisor? _supervisor;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private string _resumesFolder = "resumes";
    private Mutex? _singleInstanceMutex;
    private System.Threading.Timer? _tooltipTimer;
    private string _lastTooltip = "";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Fatal(args.Exception, "Unhandled exception");
            args.Handled = true;
        };

        try
        {
            if (!AcquireSingleInstance())
            {
                try
                {
                    using var icon = System.Drawing.SystemIcons.Application;
                    var notify = new System.Windows.Forms.NotifyIcon
                    {
                        Icon = icon,
                        Visible = true
                    };
                    notify.ShowBalloonTip(3000, "FocusMed", "FocusMed est deja en cours d'execution.", System.Windows.Forms.ToolTipIcon.Info);
                    notify.Dispose();
                }
                catch { /* best-effort balloon; shutdown proceeds regardless */ }
                Shutdown(0);
                return;
            }

            var autostart = e.Args.Any(a => string.Equals(a, "--autostart", StringComparison.OrdinalIgnoreCase));

            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            var dataDir = Environment.GetEnvironmentVariable("FOCUSMED_DATA")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusMed");
            Directory.CreateDirectory(Path.Combine(dataDir, "logs"));

            Log.Logger = new LoggerConfiguration()
                .WriteTo.File(Path.Combine(dataDir, "logs", "launcher-.log"),
                    rollingInterval: RollingInterval.Day,
                    outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();

            Log.Information("===========================================");
            Log.Information("  FocusMed Launcher");
            Log.Information("===========================================");

            var configuration = new ConfigurationBuilder()
                .SetBasePath(appDir)
                .AddJsonFile("appsettings.json", optional: true)
                .Build();

            using var siteLogFactory = new SerilogLoggerFactory(Log.Logger);
            var siteLogger = siteLogFactory.CreateLogger("FocusMed.Launcher.SiteConfig");
            _cfg = SiteConfig.Load(siteLogger, appDir);

            var resolvedDataDir = _cfg.ResolvedDataDirectory;
            var resolvedDb = _cfg.ResolvedDbConnection;
            Environment.SetEnvironmentVariable("FOCUSMED_DATA", resolvedDataDir);
            Environment.SetEnvironmentVariable("FOCUSMED_DB_CONNECTION", resolvedDb);

            _resumesFolder = _cfg.ResumesFolder;
            Directory.CreateDirectory(_cfg.PrintJobsFolder);

            Log.Information("Data dir: {DataDir}", resolvedDataDir);
            Log.Information("Web port: {Port}", _cfg.WebPort);
            Log.Information("DICOM:    {Ae} on {Port}", _cfg.AETitle, _cfg.DicomPort);
            Log.Information("Printer:  {Name} ({Driver})", _cfg.VirtualPrinterName, _cfg.OutputDriverName);
            Log.Information("Watch:    {Folder}", _cfg.PrintJobsFolder);

            var services = new ServiceCollection();
            services.AddFocusMedData(resolvedDb);
            services.AddLogging(b => b.AddSerilog());
            services.AddSingleton(_cfg);
            services.AddSingleton<PrinterSetupService>(sp =>
                new PrinterSetupService(
                    sp.GetRequiredService<ILogger<PrinterSetupService>>(),
                    _cfg.VirtualPrinterName, _cfg.OutputDriverName, _cfg.PrintJobsFolder));
            services.AddSingleton<PrintJobMonitorService>(sp =>
                new PrintJobMonitorService(
                    sp.GetRequiredService<ILogger<PrintJobMonitorService>>(),
                    _cfg.PrintJobsFolder, _resumesFolder));
            services.AddSingleton<DatabaseService>();
            services.AddSingleton<BootstrapService>();

            _serviceProvider = services.BuildServiceProvider();
            _databaseService = _serviceProvider.GetRequiredService<DatabaseService>();

            // Write the merged appsettings.json BEFORE any child starts (Worker reads it from
            // its CWD at launch). The async bootstrap re-asserts it, but this call guarantees
            // ordering even if the supervisor races ahead.
            try
            {
                _serviceProvider.GetRequiredService<BootstrapService>().EnsureSharedAppSettings();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not ensure shared appsettings at startup (continuing)");
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    var bootstrap = _serviceProvider.GetRequiredService<BootstrapService>();
                    await bootstrap.BootstrapAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Bootstrap failed (continuing)");
                }
            });

            _monitor = _serviceProvider.GetRequiredService<PrintJobMonitorService>();
            _monitor.OnNewResumePdf += OnNewResumePdf;
            _monitor.Start();

            _supervisor = new ChildProcessSupervisor(
                _serviceProvider.GetRequiredService<ILogger<ChildProcessSupervisor>>(),
                _cfg);
            var supervisorTask = _supervisor.StartAsync(CancellationToken.None);
            _ = supervisorTask.ContinueWith(t =>
            {
                if (t.IsFaulted)
                    Log.Error(t.Exception, "Supervisor task faulted (app continues)");
            }, TaskScheduler.Default);

            SetupTrayIcon(autostart);
            StartTooltipUpdater();

            if (_cfg.AutoOpenDashboardOnStart)
                OpenDashboard();

            Log.Information("Status:    Running (system tray)");
            Log.Information("===========================================");
            Log.Information("Print any document to '{Printer}' to capture it.", _cfg.VirtualPrinterName);
            Log.Information("===========================================");
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Startup failed");
            try { Shutdown(1); } catch { /* suppress re-entry during shutdown */ }
        }
    }

    private bool AcquireSingleInstance()
    {
        try
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            return createdNew;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Single-instance mutex check failed (proceeding)");
            return true;
        }
    }

    private void SetupTrayIcon(bool autostart)
    {
        var iconStream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("FocusMed.Launcher.icon.ico");

        var icon = iconStream != null
            ? new System.Drawing.Icon(iconStream)
            : System.Drawing.SystemIcons.Application;

        var contextMenu = new System.Windows.Forms.ContextMenuStrip();

        var openItem = new System.Windows.Forms.ToolStripMenuItem("Ouvrir le Dashboard");
        openItem.Click += (_, _) => OpenDashboard();
        contextMenu.Items.Add(openItem);

        contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        var quitItem = new System.Windows.Forms.ToolStripMenuItem("Quitter");
        quitItem.Click += (_, _) =>
        {
            Log.Information("User quit from tray menu");
            Shutdown();
        };
        contextMenu.Items.Add(quitItem);

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = icon,
            Text = "FocusMed Launcher",
            ContextMenuStrip = contextMenu,
            Visible = true
        };

        _trayIcon.DoubleClick += (_, _) => OpenDashboard();

        if (!autostart)
        {
            _trayIcon.ShowBalloonTip(
                3000,
                "FocusMed Launcher",
                "En attente d'impressions... Worker et Dashboard surveilles.",
                System.Windows.Forms.ToolTipIcon.Info);
        }
    }

    private void StartTooltipUpdater()
    {
        _tooltipTimer = new System.Threading.Timer(_ =>
        {
            try
            {
                if (_trayIcon == null || _supervisor == null) return;
                var text = _supervisor.GetStatusText();
                if (text.Length > 63) text = text.Substring(0, 63);
                if (text == _lastTooltip) return;
                _lastTooltip = text;
                _trayIcon.Text = text;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Tooltip update failed");
            }
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));
    }

    private void OpenDashboard()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = $"http://localhost:{_cfg?.WebPort ?? 5000}",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open dashboard");
        }
    }

    private void OnNewResumePdf(string pdfPath)
    {
        Log.Information("Print detected, opening popup: {Path}", pdfPath);

        _trayIcon?.ShowBalloonTip(
            5000,
            "FocusMed — Document imprimé",
            "Un document a été capturé. Sélectionnez une étude.",
            System.Windows.Forms.ToolTipIcon.Info);

        Dispatcher.BeginInvoke(() =>
        {
            var window = new Windows.ResumePickerWindow(_databaseService!, pdfPath, _resumesFolder);
            window.Show();
            window.Activate();
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("Shutting down...");

        _tooltipTimer?.Dispose();

        if (_supervisor != null)
        {
            try
            {
                _supervisor.StopAsync(TimeSpan.FromSeconds(5)).Wait(TimeSpan.FromSeconds(8));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Supervisor stop had issues (continuing shutdown)");
            }
        }

        _monitor?.Dispose();
        _serviceProvider?.Dispose();

        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }

        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();

        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
