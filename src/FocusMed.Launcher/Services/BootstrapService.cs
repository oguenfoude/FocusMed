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

    /// <summary>
    /// Runs all steps, collecting non-fatal health issues for the operator balloon.
    /// Every step is independent and never-fail: errors are logged and boot continues.
    /// </summary>
    public async Task<List<string>> BootstrapAsync(CancellationToken ct)
    {
        var issues = new List<string>();
        CreateDataFolders(ct);
        EnsureSharedAppSettings();
        if (_cfg.AutostartEnabled) RegisterAutostart(ct);
        if (!AddFirewallRule(ct)) issues.Add("Pare-feu (règle DICOM non créée — vérifiez le pare-feu Windows)");
        if (!await EnsureVirtualPrinterAsync(ct)) issues.Add("Imprimante virtuelle FocusMed (capture d'impression indisponible)");
        await RunMigrationAsync(ct);
        return issues;
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

            // Merge-with-existing: a blind overwrite would drop operator tuning made
            // directly in appsettings.json (forward targets, AE whitelist, stabilization
            // delay, backup). Preserve those keys when the file already exists.
            var preserved = ReadPreservedAppSettings(target);

            var net = new Dictionary<string, object?>
            {
                ["AETitle"] = _cfg.AETitle,
                ["DicomPort"] = _cfg.DicomPort,
                ["MaxPduSize"] = 65536,
                ["BindAddress"] = "0.0.0.0",
                ["EnforceAeWhitelist"] = preserved.TryGetValue("DicomNetworking.EnforceAeWhitelist", out var wl) ? wl : (object?)false,
                ["SupportedTransferSyntaxes"] = new[]
                {
                    "ImplicitVRLittleEndian", "ExplicitVRLittleEndian", "JPEGLSLossless",
                    "JPEG2000Lossless", "RLELossless", "JPEGProcess1", "JPEGProcess2_4",
                    "JPEGProcess14", "MPEG2", "MPEG4AVCH264HighProfileLevel41"
                },
                ["AllowedCallingAETitles"] = preserved.TryGetValue("DicomNetworking.AllowedCallingAETitles", out var ae) ? ae : Array.Empty<object>(),
                ["StorageCommitmentScuMapping"] = preserved.TryGetValue("DicomNetworking.StorageCommitmentScuMapping", out var scm) ? scm : new Dictionary<string, object?>(),
                ["StorageForwardTargets"] = preserved.TryGetValue("DicomNetworking.StorageForwardTargets", out var fwd) ? fwd : Array.Empty<object>()
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
                ["StudyStabilizationSeconds"] = preserved.TryGetValue("StudyStabilizationSeconds", out var stab) ? stab : (object?)60,
                ["Backup"] = new Dictionary<string, object?>
                {
                    ["Enabled"] = _cfg.BackupEnabled,
                    ["IntervalHours"] = _cfg.BackupIntervalHours,
                    ["RetentionDays"] = _cfg.BackupRetentionDays,
                    ["Folder"] = "backups"
                },
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
            try { System.IO.File.Move(tmp, target, overwrite: true); }
            catch { try { System.IO.File.Delete(tmp); } catch { } throw; }

            _logger.LogInformation("Shared appsettings.json regenerated ({Target})", target);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not regenerate shared appsettings.json (continuing)");
        }
    }

    private Dictionary<string, object?> ReadPreservedAppSettings(string target)
    {
        var preserved = new Dictionary<string, object?>(StringComparer.Ordinal);
        try
        {
            if (!System.IO.File.Exists(target)) return preserved;
            using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(target));
            var root = doc.RootElement;
            if (root.TryGetProperty("StudyStabilizationSeconds", out var stab))
                preserved["StudyStabilizationSeconds"] = stab.GetInt32();
            if (root.TryGetProperty("DicomNetworking", out var net) && net.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var key in new[] { "EnforceAeWhitelist", "AllowedCallingAETitles", "StorageCommitmentScuMapping", "StorageForwardTargets" })
                {
                    if (net.TryGetProperty(key, out var el))
                        preserved[$"DicomNetworking.{key}"] = System.Text.Json.JsonSerializer.Deserialize<object?>(el.GetRawText());
                }
            }
            if (preserved.Count > 0)
                _logger.LogInformation("Preserving {Count} operator-tuned appsettings keys ({Keys})", preserved.Count, string.Join(",", preserved.Keys));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Existing appsettings.json not merged (will regenerate defaults)");
        }
        return preserved;
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
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            p.WaitForExit(15_000);
            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();

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

    // Returns true when the current-port rule exists (added or already present).
    private bool AddFirewallRule(CancellationToken ct)
    {
        try
        {
            // Reconcile by name: a port change would otherwise leave a stale rule for
            // the old port behind. Remove every prior "FocusMed DICOM TCP *" rule first.
            ReconcileStaleFirewallRules();

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
                return false;
            }
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            p.WaitForExit(15_000);
            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();

            if (p.ExitCode == 0)
            {
                _logger.LogInformation("Firewall rule '{Rule}' added for TCP {Port}", ruleName, _cfg.DicomPort);
                return true;
            }
            _logger.LogWarning("Firewall rule add returned {Code}: {Out} {Err}", p.ExitCode, stdout.Trim(), stderr.Trim());
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Firewall rule add failed (continuing)");
            return false;
        }
    }

    private void ReconcileStaleFirewallRules()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = "-Command \"Get-NetFirewallRule -DisplayName 'FocusMed DICOM TCP*' -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p == null) return;
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            p.WaitForExit(15_000);
            _logger.LogDebug("Firewall reconcile swept stale rules (out: {Out})", stdoutTask.GetAwaiter().GetResult().Trim());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Firewall reconcile sweep failed (non-fatal)");
        }
    }

    private async Task<bool> EnsureVirtualPrinterAsync(CancellationToken ct)
    {
        try
        {
            var printer = _services.GetRequiredService<PrinterSetupService>();
            await printer.EnsurePrinterExistsAsync().WaitAsync(ct);
            return true;
        }
        catch (OperationCanceledException) { _logger.LogDebug("Virtual printer setup cancelled during shutdown"); return true; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Virtual printer setup failed (continuing)");
            return false;
        }
    }

    private async Task RunMigrationAsync(CancellationToken ct)
    {
        try
        {
            var db = _services.GetRequiredService<DatabaseService>();
            await db.RunMigrationAsync().WaitAsync(ct);
        }
        catch (OperationCanceledException) { _logger.LogDebug("Migration cancelled during shutdown"); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Database migration failed (continuing)");
        }
    }
}
