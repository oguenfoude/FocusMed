using FocusMed.Launcher.Services;
using System.Text.Json;

namespace FocusMed.Tests;

/// <summary>
/// SiteConfig validation (S23): bad AE/ports/backup values revert to defaults (never-fail
/// boot); suspicious IPs only warn (value kept); empty printer IP is the autodetect
/// sentinel (explicit default IP no longer triggers detection rewrites).
/// Sequential suite: writes config.json into an isolated temp appDir.
/// </summary>
public sealed class SiteConfigValidationTests : IDisposable
{
    private readonly TestInfra _infra = new();
    private readonly string _appDir;

    public SiteConfigValidationTests()
    {
        _appDir = Path.Combine(_infra.DataDir, "appdir");
        Directory.CreateDirectory(_appDir);
    }

    private SiteConfig LoadWith(Action<SiteConfig> mutate)
    {
        var cfg = new SiteConfig();
        mutate(cfg);
        File.WriteAllText(Path.Combine(_appDir, "config.json"),
            JsonSerializer.Serialize(cfg));
        return SiteConfig.Load(TestInfra.NullLogger<SiteConfigValidationTests>(), _appDir);
    }

    [Fact]
    public void InvalidAeTitle_RevertsToDefault()
    {
        var loaded = LoadWith(c => { c.AETitle = "WAYTOOLONGFORANAETITLE\\\\"; c.RawPrinterIp = "10.0.0.9"; });
        Assert.Equal("FOCUSMED", loaded.AETitle);
    }

    [Fact]
    public void InvalidPorts_RevertToDefaults()
    {
        var loaded = LoadWith(c => { c.DicomPort = 0; c.WebPort = 99999; c.RawPrinterPort = -1; c.RawPrinterIp = "10.0.0.9"; });
        Assert.Equal(11112, loaded.DicomPort);
        Assert.Equal(5000, loaded.WebPort);
        Assert.Equal(9100, loaded.RawPrinterPort);
    }

    [Fact]
    public void InvalidBackupInts_RevertToDefaults()
    {
        var loaded = LoadWith(c => { c.BackupIntervalHours = 0; c.BackupRetentionDays = 99999; c.RawPrinterIp = "10.0.0.9"; });
        Assert.Equal(24, loaded.BackupIntervalHours);
        Assert.Equal(7, loaded.BackupRetentionDays);
    }

    [Fact]
    public void UnparseableIp_IsKeptWithWarning()
    {
        var loaded = LoadWith(c => c.RawPrinterIp = "not-an-ip");
        Assert.Equal("not-an-ip", loaded.RawPrinterIp);
    }

    [Fact]
    public void ApipaIp_IsKeptWithWarning()
    {
        var loaded = LoadWith(c => c.RawPrinterIp = "169.254.10.20");
        Assert.Equal("169.254.10.20", loaded.RawPrinterIp);
    }

    [Fact]
    public void ExplicitDefaultIp_DoesNotRewriteFile()
    {
        var path = Path.Combine(_appDir, "config.json");
        var cfg = new SiteConfig { RawPrinterIp = "192.168.1.160" };
        File.WriteAllText(path, JsonSerializer.Serialize(cfg));
        var before = File.ReadAllText(path);
        var loaded = SiteConfig.Load(TestInfra.NullLogger<SiteConfigValidationTests>(), _appDir);
        Assert.Equal("192.168.1.160", loaded.RawPrinterIp);
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public void EmptyIp_SentinelProducesNonEmptyValue()
    {
        var loaded = LoadWith(c => c.RawPrinterIp = "");
        Assert.False(string.IsNullOrWhiteSpace(loaded.RawPrinterIp));
    }

    public void Dispose() => _infra.Dispose();
}
