using System.Drawing.Printing;
using FocusMed.PrintService.Abstractions;
using FocusMed.PrintService.Configuration;
using FocusMed.PrintService.Endpoints;
using FocusMed.PrintService.Services;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

var configuredUrls = builder.Configuration["Urls"];
if (!string.IsNullOrWhiteSpace(configuredUrls))
{
    Environment.SetEnvironmentVariable("ASPNETCORE_URLS", configuredUrls);
    builder.WebHost.UseUrls(configuredUrls);
}

builder.Services.AddOptions<PhysicalPrinterOptions>()
    .Configure<IConfiguration>((options, config) =>
    {
        var list = config.GetSection("PhysicalPrinters").Get<List<PhysicalPrinterConfig>>()
            ?? new List<PhysicalPrinterConfig>();
        options.PhysicalPrinters = list;
    });

builder.Services.AddSingleton<JobStateTracker>();
builder.Services.AddSingleton<IPhysicalPrintService, WindowsDriverPrintService>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("DashboardOnly", policy =>
    {
        policy.WithOrigins("http://localhost:5000", "https://localhost:5000")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();

app.UseCors("DashboardOnly");

StartupValidator.Validate(app.Services, builder.Configuration);

app.MapPrint();
app.MapJobStatus();
app.MapPrinters();
app.MapGet("/", () => "FocusMed.PrintService - localhost only");

app.Run();

internal static class StartupValidator
{
    public static void Validate(IServiceProvider services, IConfiguration configuration)
    {
        var logger = services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("FocusMed.PrintService.StartupValidator");

        var configuredWindowsNames = PrinterSettings.InstalledPrinters.Cast<string>().ToArray();
        logger.LogInformation(
            "FocusMed.PrintService demarre. Imprimantes Windows detectees : {Count} -> {List}",
            configuredWindowsNames.Length, string.Join(", ", configuredWindowsNames));

        var configuredPrinters = configuration.GetSection("PhysicalPrinters")
            .Get<List<PhysicalPrinterConfig>>() ?? new List<PhysicalPrinterConfig>();

        logger.LogInformation(
            "Imprimantes configurees dans appsettings.json : {Count}",
            configuredPrinters.Count);

        foreach (var printer in configuredPrinters)
        {
            var matches = configuredWindowsNames.Any(
                n => string.Equals(n, printer.WindowsQueueName, StringComparison.OrdinalIgnoreCase));
            if (matches)
            {
                logger.LogInformation(
                    "Imprimante configuree OK : {Name} -> WindowsQueueName='{Queue}', Protocol={Protocol}, Enabled={Enabled}",
                    printer.Name, printer.WindowsQueueName, printer.Protocol, printer.Enabled);
            }
            else
            {
                logger.LogError(
                    "Imprimante configuree INTROUVABLE : {Name} -> WindowsQueueName='{Queue}', Protocol={Protocol}. " +
                    "Nom exact introuvable parmi les imprimantes Windows detectees. " +
                    "Corrigez appsettings.json (propriete WindowsQueueName). " +
                    "Imprimantes disponibles : {List}",
                    printer.Name, printer.WindowsQueueName, printer.Protocol,
                    string.Join(", ", configuredWindowsNames));
            }
        }
    }
}
