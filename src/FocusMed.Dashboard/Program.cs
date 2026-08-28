using FocusMed.Dashboard.Components;
using FocusMed.Dashboard.Services;
using FocusMed.Data;
using FocusMed.Dicom;
using FocusMed.Dicom.Options;
using FocusMed.Printing;
using FocusMed.Printing.Jobs;
using Microsoft.Extensions.FileProviders;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Infrastructure;

using FellowOakDicom;

var builder = WebApplication.CreateBuilder(args);

QuestPDF.Settings.License = LicenseType.Community;

var connectionString = builder.Configuration.GetValue<string>("ConnectionString")
    ?? Environment.GetEnvironmentVariable("FOCUSMED_DB_CONNECTION")
    ?? $@"Data Source={Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusMed", "focusmed.db")}";

builder.Services.AddFocusMedData(connectionString);

// Register fo-dicom DI integration for PNG Extraction
builder.Services.AddFellowOakDicom()
    .AddImageManager<FellowOakDicom.Imaging.ImageSharpImageManager>()
    .AddTranscoderManager<FellowOakDicom.Imaging.NativeCodec.NativeTranscoderManager>();

builder.Services.Configure<PngExtractionOptions>(options => options.Enabled = true);
builder.Services.Configure<RawPrinterConfig>(builder.Configuration.GetSection(RawPrinterConfig.SectionName));
builder.Services.AddSingleton<IStudyNotificationService, StudyNotificationService>();
builder.Services.AddSingleton<PngExtractionService>();
builder.Services.AddScoped<StudyService>();
builder.Services.AddScoped<PdfService>();


builder.Services.AddHostedService<DeletedCleanupService>();

builder.Services.AddFocusMedPrinting();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Initialize fo-dicom to use our DI container
DicomSetupBuilder.UseServiceProvider(app.Services);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();
app.MapStaticAssets();

var dataDir = Environment.GetEnvironmentVariable("FOCUSMED_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusMed");
var imagesPath = Path.Combine(dataDir, "images");
Directory.CreateDirectory(imagesPath);

var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
foreach (var asset in new[] { "cover.docx", "cover-logo.jpg" })
{
    var src = Path.Combine(wwwroot, asset);
    var dst = Path.Combine(dataDir, asset);
    if (!File.Exists(dst) && File.Exists(src))
    {
        try { File.Copy(src, dst); }
        catch (Exception ex) { Console.WriteLine($"Cover asset provision failed ({asset}): {ex.Message}"); }
    }
}

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(imagesPath),
    RequestPath = "/images",
    ServeUnknownFileTypes = false
});

var pdfCachePath = Path.Combine(dataDir, "pdf-cache");
Directory.CreateDirectory(pdfCachePath);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(pdfCachePath),
    RequestPath = "/pdf-cache",
    ServeUnknownFileTypes = true,
    DefaultContentType = "application/pdf"
});

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<FocusMedDbContext>();
    db.Database.Migrate();
}

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
