using FocusMed.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FocusMed.Dicom;

// Daily SQLite backup via VACUUM INTO: produces a consistent snapshot even
// with WAL mode + concurrent readers (a raw file copy could tear mid-write).
// Timestamped copies live in <dataDir>/backups/, pruned to RetentionDays.
// Runs only in the Worker (registered via AddFocusMedDicom).
public class BackupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BackupService> _logger;
    private readonly bool _enabled;
    private readonly int _intervalHours;
    private readonly int _retentionDays;
    private readonly string _backupDir;

    public BackupService(
        IServiceScopeFactory scopeFactory,
        ILogger<BackupService> logger,
        IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _enabled = configuration.GetValue<bool>("Backup:Enabled", true);
        _intervalHours = configuration.GetValue<int>("Backup:IntervalHours", 24);
        _retentionDays = configuration.GetValue<int>("Backup:RetentionDays", 7);
        var dataDir = Environment.GetEnvironmentVariable("FOCUSMED_DATA")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusMed");
        var folder = configuration.GetValue<string>("Backup:Folder") ?? "backups";
        _backupDir = Path.IsPathFullyQualified(folder) ? folder : Path.Combine(dataDir, folder);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogDebug("Database backup disabled (Backup:Enabled=false)");
            return;
        }
        if (_intervalHours <= 0 || _retentionDays < 0)
        {
            _logger.LogWarning("Invalid Backup config (IntervalHours={Hours}, RetentionDays={Days}); backups disabled",
                _intervalHours, _retentionDays);
            return;
        }

        Directory.CreateDirectory(_backupDir);
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunBackupAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Database backup failed");
            }
            try
            {
                await Task.Delay(TimeSpan.FromHours(_intervalHours), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunBackupAsync(CancellationToken ct)
    {
        var target = Path.Combine(_backupDir, $"focusmed-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db");
        // VACUUM INTO takes a filename LITERAL — bound parameters are rejected by
        // SQLite, so this must be plain-string concatenation (never FormattableString).
        // The filename is generated; only the configured folder is escaped.
        var sql = "VACUUM INTO '" + target.Replace("'", "''") + "'";
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FocusMedDbContext>();
        await db.Database.ExecuteSqlRawAsync(sql, ct);

        _logger.LogInformation("Database backup written: {Path} ({Size} bytes)",
            target, new FileInfo(target).Length);

        var cutoff = DateTime.UtcNow.AddDays(-_retentionDays);
        foreach (var f in Directory.GetFiles(_backupDir, "focusmed-*.db"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(f) < cutoff)
                {
                    File.Delete(f);
                    _logger.LogDebug("Deleted old backup {Path}", f);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete old backup {Path}", f);
            }
        }
    }
}
