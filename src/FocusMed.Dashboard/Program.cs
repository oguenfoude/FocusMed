using FocusMed.Dashboard.Components;
using FocusMed.Data;
using FocusMed.Dicom;
using FocusMed.Dicom.Options;
using Microsoft.Extensions.FileProviders;

using FellowOakDicom;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetValue<string>("ConnectionString")
    ?? Environment.GetEnvironmentVariable("FOCUSMED_DB_CONNECTION")
    ?? "Host=localhost;Port=5432;Database=focusmed;Username=postgres;Password=admin";

builder.Services.AddFocusMedData(connectionString);

// Register fo-dicom DI integration for PNG Extraction
builder.Services.AddFellowOakDicom()
    .AddImageManager<FellowOakDicom.Imaging.ImageSharpImageManager>()
    .AddTranscoderManager<FellowOakDicom.Imaging.NativeCodec.NativeTranscoderManager>();

builder.Services.Configure<PngExtractionOptions>(options => options.Enabled = true);
builder.Services.AddSingleton<PngExtractionService>();

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

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(imagesPath),
    RequestPath = "/images"
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
