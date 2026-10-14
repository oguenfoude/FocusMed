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
    // Global\ (not Local\): one launcher per MACHINE, not per session. A Local\ mutex
    // allowed a second launcher in another RDP/user session, which then fought over
    // the same ports and locked FocusMed.Launcher.exe across sessions (MSB3027).
    // Creation is guarded (AcquireSingleInstance try/catches), so non-elevated
    // contexts that cannot create Global\ objects still boot.
    private const string MutexName = @"Global\FocusMed.Launcher";

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
            // Early logger on the pre-config data dir (env or default). Rebound to the
            // RESOLVED dir after SiteConfig.Load below: DataDirectory in config.json may
            // point elsewhere, and logging must follow it (log-dir ordering).
            var earlyDataDir = Environment.GetEnvironmentVariable("FOCUSMED_DATA")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusMed");
            Directory.CreateDirectory(Path.Combine(earlyDataDir, "logs"));

            Log.Logger = new LoggerConfiguration()
                .WriteTo.File(Path.Combine(earlyDataDir, "logs", "launcher-.log"),
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
            // Env snapshot note: children read FOCUSMED_DATA/FOCUSMED_DB_CONNECTION once at
            // spawn. Changing config.json or env vars needs a Launcher restart — there is
            // intentionally no live watcher (documented here + in the tray tooltip cycle).
            Log.Information("Env snapshot: FOCUSMED_DATA={DataDir} (restart required after config/env changes — no live reload)", resolvedDataDir);

            if (!string.Equals(
                    Path.GetFullPath(Path.Combine(earlyDataDir, "logs")),
                    Path.GetFullPath(Path.Combine(resolvedDataDir, "logs")),
                    StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Path.Combine(resolvedDataDir, "logs"));
                Log.CloseAndFlush();
                Log.Logger = new LoggerConfiguration()
                    .WriteTo.File(Path.Combine(resolvedDataDir, "logs", "launcher-.log"),
                        rollingInterval: RollingInterval.Day,
                        outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                    .CreateLogger();
                Log.Information("Logger rebound to resolved data dir: {DataDir}", resolvedDataDir);
            }

            if (!PreflightCheck(_cfg))
            {
                try { Shutdown(1); } catch { }
                return;
            }

            _resumesFolder = _cfg.ResumesFolder;
            try
            {
                Directory.CreateDirectory(_cfg.PrintJobsFolder);
            }
            catch (Exception ex)
            {
                // Unwritable-folders degrade: capture is unavailable, but the DICOM
                // listener + dashboard must still run. Never Shutdown() over this.
                Log.Warning(ex, "Print-jobs folder '{Folder}' not writable; print capture degraded (continuing)", _cfg.PrintJobsFolder);
            }

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
                    var issues = await bootstrap.BootstrapAsync(CancellationToken.None);
                    if (issues.Count > 0)
                    {
                        var text = "Attention : " + string.Join(" ; ", issues);
                        Log.Warning("Bootstrap health: {Issues}", text);
                        try
                        {
                            _ = Dispatcher.BeginInvoke(() => ShowBalloon("FocusMed — vérifiez l'installation", text));
                        }
                        catch (Exception ex)
                        {
                            Log.Debug(ex, "Bootstrap balloon failed (non-fatal)");
                        }
                    }
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
            _supervisor.ChildStatusChanged += (_, _) => RefreshTooltipSoon();
            var supervisorTask = _supervisor.StartAsync(CancellationToken.None);
            _ = supervisorTask.ContinueWith(t =>
            {
                if (t.IsFaulted)
                    Log.Error(t.Exception, "Supervisor task faulted (app continues)");
            }, TaskScheduler.Default);

            SetupTrayIcon(autostart);
            StartTooltipUpdater();

            if (!autostart)
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
            if (!createdNew)
            {
                try
                {
                    // Second instance: reuse the same never-fail loader (validation +
                    // autodetect defaults included) instead of a parallel hand-rolled parse.
                    var port = 5000;
                    var host = "127.0.0.1";
                    try
                    {
                        using var qlf = new SerilogLoggerFactory(Log.Logger);
                        var cfg = SiteConfig.Load(qlf.CreateLogger("FocusMed.Launcher.SecondInstance"), AppDomain.CurrentDomain.BaseDirectory);
                        if (cfg.WebPort is > 0 and <= 65535) port = cfg.WebPort;
                        if (!string.IsNullOrWhiteSpace(cfg.LocalIp)) host = cfg.LocalIp;
                    }
                    catch { }

                    Process.Start(new ProcessStartInfo
                    {
                        FileName = $"http://{host}:{port}",
                        UseShellExecute = true
                    });
                }
                catch { }
                return false;
            }
            return true;
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

    // Fatal preflight: port collisions and privileged ports refuse to boot with a
    // balloon + exit code 1 (never a silent crash-loop). DICOM==Web would make the
    // children fight over one socket; ports <1024 need privileges the app may lack.
    private bool PreflightCheck(SiteConfig cfg)
    {
        string? problem = null;
        if (cfg.DicomPort == cfg.WebPort)
            problem = $"DICOM ({cfg.DicomPort}) et Web ({cfg.WebPort}) utilisent le même port. Modifiez config.json (DicomPort/WebPort) puis relancez.";
        else if (cfg.DicomPort < 1024 || cfg.WebPort < 1024)
            problem = $"Ports réservés (<1024) refusés : DICOM={cfg.DicomPort}, Web={cfg.WebPort}. Utilisez des ports >= 1024.";
        if (problem == null) return true;
        Log.Fatal("Preflight refused: {Problem}", problem);
        try { ShowBalloon("FocusMed — démarrage impossible", problem); } catch { }
        return false;
    }

    private void ShowBalloon(string title, string text)
    {
        try
        {
            _trayIcon?.ShowBalloonTip(8000, title, text, System.Windows.Forms.ToolTipIcon.Warning);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Balloon failed (non-fatal)");
            return;
        }
        // No tray yet during preflight: fall back to a message box so the operator
        // still sees the refusal reason.
        if (_trayIcon == null)
        {
            try { System.Windows.MessageBox.Show(text, title, MessageBoxButton.OK, MessageBoxImage.Warning); } catch { }
        }
    }

    private void RefreshTooltipSoon()
    {
        try { Dispatcher.BeginInvoke((Action)RefreshTooltip); } catch { }
    }

    private void RefreshTooltip()
    {
        try
        {
            if (_trayIcon == null || _supervisor == null) return;
            var text = SmartCut(_supervisor.GetStatusText(), 63);
            if (text == _lastTooltip) return;
            _lastTooltip = text;
            _trayIcon.Text = text;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Tooltip update failed");
        }
    }

    // NotifyIcon.Text caps at 63 chars: cut at the last separator/space, never mid-word.
    private static string SmartCut(string text, int max)
    {
        if (text.Length <= max) return text;
        var cut = text.LastIndexOf('|', max - 1);
        if (cut < 0) cut = text.LastIndexOf(' ', max - 1);
        if (cut <= 0) cut = max - 1;
        return text.Substring(0, cut).TrimEnd() + "...";
    }

    private void StartTooltipUpdater()
    {
        // Timer thread must not touch the WinForms NotifyIcon directly: marshal to the
        // WPF dispatcher (UI thread) on every tick.
        _tooltipTimer = new System.Threading.Timer(_ =>
        {
            try { Dispatcher.BeginInvoke((Action)RefreshTooltip); }
            catch (Exception ex)
            {
                Log.Warning(ex, "Tooltip update marshal failed");
            }
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));
    }

    private void OpenDashboard()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = DashboardUrl(),
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open dashboard");
        }
    }

    private string DashboardUrl()
    {
        var port = _cfg?.WebPort ?? 5000;
        // Prefer the confirmed-working LAN IP, then fall back to IPv4 loopback.
        var host = !string.IsNullOrWhiteSpace(_cfg?.LocalIp) ? _cfg.LocalIp : "127.0.0.1";
        return $"http://{host}:{port}";
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
            var window = new Windows.ResumePickerWindow(_databaseService!, _serviceProvider!.GetRequiredService<ILogger<FocusMed.Launcher.Windows.ResumePickerWindow>>(), pdfPath, _resumesFolder);
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
