using FocusMed.Data;
using FocusMed.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FocusMed.Dicom;

public class StudyCompletionService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<StudyCompletionService> _logger;
    private readonly PngExtractionService _pngExtractionService;
    private readonly int _stabilizationSeconds;
    private readonly int _archiveRetentionDays;

    public StudyCompletionService(
        IServiceScopeFactory scopeFactory,
        ILogger<StudyCompletionService> logger,
        IConfiguration configuration,
        PngExtractionService pngExtractionService)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _pngExtractionService = pngExtractionService;
        _stabilizationSeconds = configuration.GetValue<int>("StudyStabilizationSeconds", 60);
        _archiveRetentionDays = configuration.GetValue<int>("ArchiveRetentionDays", 0);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessReceivingStudiesAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Study completion error");
            }

            if (_archiveRetentionDays > 0)
            {
                try { await CleanupOldArchivesAsync(stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Archive cleanup error");
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private async Task ProcessReceivingStudiesAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FocusMedDbContext>();

        var cutoffTime = DateTime.UtcNow.AddSeconds(-_stabilizationSeconds);

        var readyStudies = await db.Studies
            .Include(s => s.Patient)
            .Include(s => s.Series).ThenInclude(s => s.Images)
            .AsSplitQuery()
            .Where(s => s.Status == StudyStatus.Receiving && s.LastUpdatedAt <= cutoffTime)
            .ToListAsync(stoppingToken);

        if (readyStudies.Count == 0)
            return;

        foreach (var study in readyStudies)
        {
            try
            {
                var imageCount = study.Series.SelectMany(s => s.Images).Count();

                var freshImageCount = await db.DicomImages
                    .CountAsync(i => i.Series.StudyId == study.Id, stoppingToken);
                if (freshImageCount != imageCount)
                {
                    study.LastUpdatedAt = DateTime.UtcNow;
                    continue;
                }

                study.Status = StudyStatus.Complete;
                _logger.LogInformation("Study complete: {PatientName} | {StudyDate} | {StudyUid} ({ImageCount} images)",
                    study.Patient?.PatientName ?? "Unknown",
                    study.StudyDate?.ToString("yyyy-MM-dd") ?? "N/A",
                    study.StudyInstanceUid,
                    imageCount);

                // Pre-extract PNGs in the background so the Dashboard opens this study
                // instantly instead of re-rendering every DICOM frame on first view.
                _ = _pngExtractionService.PreExtractStudyAsync(study.Id, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to complete study {StudyId}", study.Id);
            }
        }

        try
        {
            await db.SaveChangesAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save study completion changes");
        }
    }

    private async Task CleanupOldArchivesAsync(CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddDays(-_archiveRetentionDays);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FocusMedDbContext>();

        var dataDir = Environment.GetEnvironmentVariable("FOCUSMED_DATA")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusMed");
        var archiveRoot = Path.Combine(dataDir, "archive");
        var imagesRoot = Path.Combine(dataDir, "images");

        var oldStudies = await db.Studies
            .Include(s => s.Series).ThenInclude(s => s.Images)
            .AsSplitQuery()
            .Where(s => (s.Status == StudyStatus.Archived || s.Status == StudyStatus.Deleted)
                && s.LastUpdatedAt < cutoff)
            .ToListAsync(ct);

        if (oldStudies.Count == 0) return;

        var archiveDirs = new HashSet<string>();
        var pngDirs = new HashSet<string>();
        var resumes = new List<string>();
        var patientIds = new HashSet<int>();
        foreach (var study in oldStudies)
        {
            foreach (var img in study.Series.SelectMany(s => s.Images))
            {
                if (!string.IsNullOrEmpty(img.FilePath))
                {
                    var studyDir = Directory.GetParent(Path.GetDirectoryName(img.FilePath) ?? "")?.FullName;
                    if (studyDir != null && DicomHelpers.IsSubdirectoryOf(studyDir, archiveRoot))
                        archiveDirs.Add(studyDir);
                }
                if (!string.IsNullOrEmpty(img.PngPath))
                {
                    var pngDir = Path.GetDirectoryName(img.PngPath);
                    if (pngDir != null && DicomHelpers.IsSubdirectoryOf(pngDir, imagesRoot))
                        pngDirs.Add(pngDir);
                }
            }
            if (!string.IsNullOrEmpty(study.ResumePdfPath)) resumes.Add(study.ResumePdfPath);
            if (study.PatientId != 0) patientIds.Add(study.PatientId);
            db.Studies.Remove(study);
        }

        await db.SaveChangesAsync(ct);

        foreach (var dir in archiveDirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete archive dir {Dir}", dir); }
        }
        foreach (var dir in pngDirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete PNG dir {Dir}", dir); }
        }
        foreach (var resume in resumes)
        {
            var resumeFull = Path.GetFullPath(Path.Combine(dataDir, resume));
            if (!resumeFull.StartsWith(dataDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            try { if (File.Exists(resumeFull)) File.Delete(resumeFull); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete resume PDF {Path}", resumeFull); }
        }

        foreach (var pid in patientIds)
        {
            if (await db.Studies.AnyAsync(s => s.PatientId == pid, ct)) continue;
            var patient = await db.Patients.FindAsync(pid);
            if (patient != null) db.Patients.Remove(patient);
        }
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Archive cleanup: removed {Count} studies older than {Days}d ({Dirs} dirs)",
            oldStudies.Count, _archiveRetentionDays, archiveDirs.Count);
    }
}
