using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using FocusMed.Data;
using FocusMed.PrintCapture.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace FocusMed.PrintCapture;

public partial class App : System.Windows.Application
{
    private ServiceProvider? _serviceProvider;
    private PrintJobMonitorService? _monitor;
    private DatabaseService? _databaseService;
    private System.Windows.Forms.NotifyIcon? _trayIcon;

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
            var dataDir = Environment.GetEnvironmentVariable("FOCUSMED_DATA")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusMed");
            Directory.CreateDirectory(Path.Combine(dataDir, "logs"));

            Log.Logger = new LoggerConfiguration()
                .WriteTo.File(Path.Combine(dataDir, "logs", "print-capture-.log"),
                    rollingInterval: RollingInterval.Day,
                    outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();

            Log.Information("===========================================");
            Log.Information("  FocusMed PrintCapture");
            Log.Information("===========================================");

            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false)
                .Build();

            var connectionString = configuration["ConnectionString"]
                ?? Environment.GetEnvironmentVariable("FOCUSMED_DB_CONNECTION")
                ?? "Host=localhost;Port=5432;Database=focusmed;Username=postgres;Password=admin";

            var printerName = configuration["PrinterName"] ?? "FocusMed";
            var driverName = configuration["OutputDriverName"] ?? "Microsoft Print To PDF";
            var printJobsFolder = configuration["PrintJobsFolder"]
                ?? Path.Combine(dataDir, "print-jobs");
            var resumesFolder = configuration["ResumesFolder"] ?? "resumes";

            Environment.SetEnvironmentVariable("FOCUSMED_DB_CONNECTION", connectionString);
            Directory.CreateDirectory(printJobsFolder);

            Log.Information("Database:  {Conn}", connectionString.Replace("Password=admin", "Password=*****"));
            Log.Information("Printer:   {Name} ({Driver})", printerName, driverName);
            Log.Information("Watch:     {Folder}", printJobsFolder);
            Log.Information("Resumes:   {Folder}", Path.Combine(dataDir, resumesFolder));
            Log.Information("Logs:      {Folder}", Path.Combine(dataDir, "logs"));

            var services = new ServiceCollection();
            services.AddFocusMedData(connectionString);
            services.AddLogging(b => b.AddSerilog());
            services.AddSingleton<PrinterSetupService>(sp =>
                new PrinterSetupService(
                    sp.GetRequiredService<ILogger<PrinterSetupService>>(),
                    printerName, driverName, printJobsFolder));
            services.AddSingleton<PrintJobMonitorService>(sp =>
                new PrintJobMonitorService(
                    sp.GetRequiredService<ILogger<PrintJobMonitorService>>(),
                    printJobsFolder, resumesFolder));
            services.AddSingleton<DatabaseService>();

            _serviceProvider = services.BuildServiceProvider();
            _databaseService = _serviceProvider.GetRequiredService<DatabaseService>();

            Task.Run(async () =>
            {
                try { await _databaseService.RunMigrationAsync(); } catch (Exception ex) { Log.Warning(ex, "Migration failed (continuing)"); }
                try { await _serviceProvider.GetRequiredService<PrinterSetupService>().EnsurePrinterExistsAsync(); } catch (Exception ex) { Log.Warning(ex, "Printer setup failed (continuing)"); }
            });

            _monitor = _serviceProvider.GetRequiredService<PrintJobMonitorService>();
            _monitor.OnNewResumePdf += OnNewResumePdf;
            _monitor.Start();

            SetupTrayIcon();

            Log.Information("Status:    Running (system tray)");
            Log.Information("===========================================");
            Log.Information("Print any document to '{Printer}' to capture it.", printerName);
            Log.Information("===========================================");
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Startup failed");
            Shutdown(1);
        }
    }

    private void SetupTrayIcon()
    {
        var iconStream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("FocusMed.PrintCapture.icon.ico");

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
            Text = "FocusMed PrintCapture",
            ContextMenuStrip = contextMenu,
            Visible = true
        };

        _trayIcon.DoubleClick += (_, _) => OpenDashboard();

        _trayIcon.ShowBalloonTip(
            3000,
            "FocusMed PrintCapture",
            "En attente d'impressions...",
            System.Windows.Forms.ToolTipIcon.Info);
    }

    private void OpenDashboard()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "http://localhost:5000",
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
            var window = new Windows.ResumePickerWindow(_databaseService!, pdfPath);
            window.Show();
            window.Activate();
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("Shutting down...");
        _monitor?.Dispose();
        _serviceProvider?.Dispose();

        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }

        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
