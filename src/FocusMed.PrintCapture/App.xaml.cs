using System.IO;
using System.Windows;
using FocusMed.Data;
using FocusMed.PrintCapture.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace FocusMed.PrintCapture;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;
    private PrintJobMonitorService? _monitor;
    private DatabaseService? _databaseService;

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

            Log.Information("Status:    Running (background)");
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

    private void OnNewResumePdf(string pdfPath)
    {
        Log.Information("Print detected, opening popup: {Path}", pdfPath);

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
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
