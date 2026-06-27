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
            var connectionString = hostContext.Configuration.GetValue<string>("ConnectionString")
                ?? "Host=localhost;Port=5432;Database=focusmed;Username=postgres;Password=postgres";

            // Register Data & Dicom logic
            services.AddFocusMedData(connectionString);
            services.AddFocusMedDicom();

            // Register fo-dicom DI integration
            services.AddFellowOakDicom()
                .AddImageManager<FellowOakDicom.Imaging.ImageSharpImageManager>()
                .AddTranscoderManager<FellowOakDicom.Imaging.NativeCodec.NativeTranscoderManager>();
            
            services.AddHostedService<DicomListenerService>();
        })
        .Build();

    // Initialize fo-dicom to use our DI container
    DicomSetupBuilder.UseServiceProvider(host.Services);

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
