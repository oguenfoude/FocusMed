using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FocusMed.Launcher.Services;

/// <summary>
/// One-time machine setup (folders, firewall, virtual printer, DB migration).
/// Every step is independent and never-fail: errors are logged and the boot continues.
/// </summary>
public class BootstrapService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<BootstrapService> _logger;
    private readonly SiteConfig _cfg;

    public BootstrapService(IServiceProvider services, ILogger<BootstrapService> logger, SiteConfig cfg)
    {
        _services = services;
        _logger = logger;
        _cfg = cfg;
    }

    public async Task BootstrapAsync(CancellationToken ct)
    {
        CreateDataFolders(ct);
        EnsureSharedAppSettings();
        if (_cfg.AutostartEnabled) RegisterAutostart(ct);
        AddFirewallRule(ct);
        await EnsureVirtualPrinterAsync(ct);
        await RunMigrationAsync(ct);
    }

    /// <summary>
    /// Publishes Worker + Dashboard + Launcher into one folder => the three appsettings.json
    /// files would clobber each other. This regenerates a SINGLE merged appsettings.json
    /// (from config.json defaults) in the exe dir, so both children always read the same,
    /// coherent config from their CWD. Idempotent, self-healing, never-fail.
    /// Called synchronously at startup BEFORE children are spawned, and re-asserted by
    /// BootstrapAsync. The Worker reads appsettings.json from its CWD at launch, so this
    /// MUST be written before the supervisor starts any child.
    /// </summary>
    public void EnsureSharedAppSettings()
    {
        try
        {
            var exeDir = AppContext.BaseDirectory;
            var target = Path.Combine(exeDir, "appsettings.json");
            var tmp = target + ".tmp";

            var net = new Dictionary<string, object?>
            {
                ["AETitle"] = _cfg.AETitle,
                ["DicomPort"] = _cfg.DicomPort,
                ["MaxPduSize"] = 65536,
                ["BindAddress"] = "0.0.0.0",
                ["EnforceAeWhitelist"] = false,
                ["SupportedTransferSyntaxes"] = new[]
                {
                    "ImplicitVRLittleEndian", "ExplicitVRLittleEndian", "JPEGLSLossless",
                    "JPEG2000Lossless", "RLELossless", "JPEGProcess1", "JPEGProcess2_4",
                    "JPEGProcess14", "MPEG2", "MPEG4AVCH264HighProfileLevel41"
                },
                ["AllowedCallingAETitles"] = Array.Empty<object>(),
                ["StorageCommitmentScuMapping"] = new Dictionary<string, object?>(),
                ["StorageForwardTargets"] = Array.Empty<object>()
            };

            var rawPresets = new[]
            {
                new Dictionary<string, object?>
                {
                    ["Name"] = "KONICA BOOK",
                    ["Ip"] = _cfg.RawPrinterIp,
                    ["Port"] = _cfg.RawPrinterPort,
                    ["PaperSize"] = "A3",
                    ["WindowsPrinterName"] = _cfg.KonicaWindowsPrinterName
                },
                new Dictionary<string, object?>
                {
                    ["Name"] = "KONICA A3",
                    ["Ip"] = _cfg.RawPrinterIp,
                    ["Port"] = _cfg.RawPrinterPort,
                    ["PaperSize"] = "A3",
                    ["WindowsPrinterName"] = _cfg.KonicaWindowsPrinterName
                },
                new Dictionary<string, object?>
                {
                    ["Name"] = "KONICA A4",
                    ["Ip"] = _cfg.RawPrinterIp,
                    ["Port"] = _cfg.RawPrinterPort,
                    ["PaperSize"] = "A4",
                    ["WindowsPrinterName"] = _cfg.KonicaWindowsPrinterName
                }
            };

            var merged = new Dictionary<string, object?>
            {
                ["Serilog"] = new Dictionary<string, object?>
                {
                    ["Using"] = new[] { "Serilog.Sinks.Console", "Serilog.Sinks.File" },
                    ["MinimumLevel"] = new Dictionary<string, object?>
                    {
                        ["Default"] = "Information",
                        ["Override"] = new Dictionary<string, object?>
                        {
                            ["Microsoft"] = "Warning",
                            ["Microsoft.EntityFrameworkCore"] = "Warning",
                            ["System"] = "Warning",
                            ["FellowOakDicom"] = "Warning"
                        }
                    },
                    ["WriteTo"] = new[]
                    {
                        new Dictionary<string, object?> { ["Name"] = "Console" },
                        new Dictionary<string, object?>
                        {
                            ["Name"] = "File",
                            ["Args"] = new Dictionary<string, object?>
                            {
                                ["path"] = "%FOCUSMED_DATA%/logs/focusmed-.log",
                                ["rollingInterval"] = "Day"
                            }
                        },
                        new Dictionary<string, object?>
                        {
                            ["Name"] = "File",
                            ["Args"] = new Dictionary<string, object?>
                            {
                                ["path"] = "%FOCUSMED_DATA%/logs/dicom_associations.log",
                                ["rollingInterval"] = "Day",
                                ["outputTemplate"] = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}"
                            }
                        }
                    }
                },
                ["StudyStabilizationSeconds"] = 60,
                ["PngExtraction"] = new Dictionary<string, object?> { ["Enabled"] = true },
                ["DicomNetworking"] = net,
                ["Logging"] = new Dictionary<string, object?>
                {
                    ["LogLevel"] = new Dictionary<string, object?>
                    {
                        ["Default"] = "Information",
                        ["Microsoft.AspNetCore"] = "Warning"
                    }
                },
                ["AllowedHosts"] = "*",
                ["RawPrinters"] = new Dictionary<string, object?> { ["Printers"] = rawPresets }
            };

            var json = System.Text.Json.JsonSerializer.Serialize(merged, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            });
            System.IO.File.WriteAllText(tmp, json);
            System.IO.File.Copy(tmp, target, overwrite: true);
            System.IO.File.Delete(tmp);

            _logger.LogInformation("Shared appsettings.json regenerated ({Target})", target);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not regenerate shared appsettings.json (continuing)");
        }
    }

    private void CreateDataFolders(CancellationToken ct)
    {
        try
        {
            var dataDir = _cfg.ResolvedDataDirectory;
            foreach (var sub in new[] { "", "logs", "images", "pdf-cache", "resumes", "archive" })
                Directory.CreateDirectory(Path.Combine(dataDir, sub));
            _logger.LogInformation("Data folders ensured under {DataDir}", dataDir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not create data folders (continuing)");
        }
    }

    private void RegisterAutostart(CancellationToken ct)
    {
        try
        {
            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(exe))
            {
                _logger.LogWarning("Autostart skipped: could not resolve current exe path");
                return;
            }

            var args = $"schtasks /Create /TN \"FocusMed\" /TR \"\\\"{exe}\\\" --autostart\" /SC ONLOGON /RL HIGHEST /F";
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/C {args}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p == null)
            {
                _logger.LogWarning("Autostart: schtasks did not start");
                return;
            }
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(15_000);

            if (p.ExitCode == 0)
                _logger.LogInformation("Autostart task 'FocusMed' registered (ONLOGON, HIGHEST)");
            else
                _logger.LogWarning("Autostart task registration returned {Code}: {Out} {Err}", p.ExitCode, stdout.Trim(), stderr.Trim());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Autostart registration failed (continuing)");
        }
    }

    private void AddFirewallRule(CancellationToken ct)
    {
        try
        {
            var ruleName = $"FocusMed DICOM TCP {_cfg.DicomPort}";
            var args = $"netsh advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow protocol=TCP localport={_cfg.DicomPort}";
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/C {args}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p == null)
            {
                _logger.LogWarning("Firewall: netsh did not start");
                return;
            }
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(15_000);

            if (p.ExitCode == 0)
                _logger.LogInformation("Firewall rule '{Rule}' added for TCP {Port}", ruleName, _cfg.DicomPort);
            else
                _logger.LogWarning("Firewall rule add returned {Code}: {Out} {Err}", p.ExitCode, stdout.Trim(), stderr.Trim());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Firewall rule add failed (continuing)");
        }
    }

    private async Task EnsureVirtualPrinterAsync(CancellationToken ct)
    {
        try
        {
            var printer = _services.GetRequiredService<PrinterSetupService>();
            await printer.EnsurePrinterExistsAsync().WaitAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Virtual printer setup failed (continuing)");
        }
    }

    private async Task RunMigrationAsync(CancellationToken ct)
    {
        try
        {
            var db = _services.GetRequiredService<DatabaseService>();
            await db.RunMigrationAsync().WaitAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Database migration failed (continuing)");
        }
    }
}
