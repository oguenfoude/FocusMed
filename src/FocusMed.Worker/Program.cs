using FellowOakDicom;
using FocusMed.Data;
using FocusMed.Dicom;
using FocusMed.Dicom.Options;
using FocusMed.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;

var dataDir = PathHelper.GetDataDirectory();
Environment.SetEnvironmentVariable("FOCUSMED_DATA", dataDir);

var configuration = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .Build();

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(configuration)
    .CreateLogger();

try
{
    Log.Information("Starting FocusMed Worker");

    var host = Host.CreateDefaultBuilder(args)
        .UseSerilog()
        .ConfigureServices((hostContext, services) =>
        {
            var connectionString = hostContext.Configuration.GetValue<string>("ConnectionString")
                ?? "Host=localhost;Port=5432;Database=focusmed;Username=postgres;Password=postgres";

            // Register Data & Dicom logic
            services.AddFocusMedData(connectionString);
            services.AddFocusMedDicom(hostContext.Configuration);

            // Register fo-dicom DI integration
            services.AddFellowOakDicom()
                .AddImageManager<FellowOakDicom.Imaging.ImageSharpImageManager>()
                .AddTranscoderManager<FellowOakDicom.Imaging.NativeCodec.NativeTranscoderManager>();
            
            services.Configure<FellowOakDicom.Network.DicomServiceOptions>(options => 
            {
                var dicomNet = hostContext.Configuration
                    .GetSection(DicomNetworkingOptions.SectionName)
                    .Get<DicomNetworkingOptions>()!;
                options.MaxPDULength = (uint)dicomNet.MaxPduSize;
            });

            services.AddHostedService<DicomListenerService>();
        })
        .Build();

    // Initialize fo-dicom to use our DI container
    DicomSetupBuilder.UseServiceProvider(host.Services);

    var dicomOpts = host.Services.GetRequiredService<IOptions<DicomNetworkingOptions>>().Value;
    Log.Information("FocusMed starting -- AE: {AeTitle} Port: {Port}", dicomOpts.AETitle, dicomOpts.DicomPort);

    var enabledPrinters = dicomOpts.FilmPrinters.Where(p => p.Enabled).ToList();
    Log.Information("Film Printers configured: {Count}", enabledPrinters.Count);
    foreach (var p in enabledPrinters)
        Log.Information("  Printer: {Name} ({Type}) -> {Ae} @ {Ip}:{Port}", p.Name, p.PrinterType, p.PrinterAe, p.PrinterIp, p.PrinterPort);

    var enabledTargets = dicomOpts.StorageForwardTargets.Where(t => t.Enabled).ToList();
    Log.Information("Storage Forward Targets configured: {Count}", enabledTargets.Count);
    foreach (var t in enabledTargets)
        Log.Information("  Forward Target: {Name} -> {Ae} @ {Ip}:{Port}", t.Name, t.AeTitle, t.Ip, t.Port);

    if (enabledPrinters.Count == 0)
        Log.Warning(
            "No enabled FilmPrinters configured. All incoming print jobs will be REJECTED. " +
            "Add at least one FilmPrinter entry to DicomNetworking:FilmPrinters in appsettings.json.");

    if (enabledTargets.Count == 0)
        Log.Warning("No enabled Storage Forward Targets. Images will be stored locally only.");

    using (var scope = host.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<FocusMedDbContext>();
        Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.Migrate(db.Database);
        Log.Information("Database migrations applied successfully.");
    }

    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
