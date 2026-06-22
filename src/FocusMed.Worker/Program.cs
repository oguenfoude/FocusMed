using FellowOakDicom;
using FocusMed.Data;
using FocusMed.Dicom;
using FocusMed.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
            var dbPathRaw = hostContext.Configuration.GetValue<string>("DatabasePath") ?? "%FOCUSMED_DATA%/db/focusmed.db";
            var dbPath = Environment.ExpandEnvironmentVariables(dbPathRaw);
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            
            // Register Data & Dicom logic
            services.AddFocusMedData(dbPath);
            services.AddFocusMedDicom();

            // Register fo-dicom DI integration
            services.AddFellowOakDicom();
            
            services.AddHostedService<DicomListenerService>();
        })
        .Build();

    // Initialize fo-dicom to use our DI container
    DicomSetupBuilder.UseServiceProvider(host.Services);

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
