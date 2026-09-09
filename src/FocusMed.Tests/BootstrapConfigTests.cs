using FocusMed.Launcher.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace FocusMed.Tests;

/// <summary>
/// Self-setup: BootstrapService.EnsureSharedAppSettings regenerates one merged
/// appsettings.json from config values. Proves the children always get a
/// coherent config even on a fresh machine with no appsettings.json present.
/// Writes into the test bin dir (AppContext.BaseDirectory) and cleans up.
/// </summary>
public sealed class BootstrapConfigTests : IDisposable
{
    private readonly TestInfra _infra = new();
    private readonly string _target;

    public BootstrapConfigTests()
    {
        _target = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
    }

    [Fact]
    public void EnsureSharedAppSettings_WritesCoherentMergedConfig()
    {
        var cfg = new SiteConfig
        {
            AETitle = "TESTAE",
            DicomPort = 11112,
            WebPort = 5000,
            DataDirectory = _infra.DataDir,
            RawPrinterIp = "192.168.9.9",
            RawPrinterPort = 9100
        };
        var services = new ServiceCollection().BuildServiceProvider();
        var bootstrap = new BootstrapService(services, TestInfra.NullLogger<BootstrapService>(), cfg);

        bootstrap.EnsureSharedAppSettings();

        Assert.True(File.Exists(_target));
        using var doc = JsonDocument.Parse(File.ReadAllText(_target));
        var root = doc.RootElement;
        Assert.Equal("TESTAE", root.GetProperty("DicomNetworking").GetProperty("AETitle").GetString());
        Assert.Equal(11112, root.GetProperty("DicomNetworking").GetProperty("DicomPort").GetInt32());
        Assert.True(root.TryGetProperty("RawPrinters", out _));
    }

    public void Dispose()
    {
        try { File.Delete(_target); File.Delete(_target + ".tmp"); } catch { }
        _infra.Dispose();
    }
}
