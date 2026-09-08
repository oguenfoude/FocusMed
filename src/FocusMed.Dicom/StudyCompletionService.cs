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
    private readonly int _printMergeWindowSeconds;
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
        _printMergeWindowSeconds = configuration.GetValue<int>("DicomNetworking:PrintMergeWindowSeconds", 300);
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

                // Reverse merge: when this CT/OT study completes, absorb recent anonymous
                // print studies from the same calling AE so films land in the right study.
                await MergeRecentPrintsIntoStudyAsync(db, study, stoppingToken);

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

    private async Task MergeRecentPrintsIntoStudyAsync(FocusMedDbContext db, Study targetStudy, CancellationToken ct)
    {
        var windowStart = DateTime.UtcNow.AddSeconds(-_printMergeWindowSeconds);

        // Find recent PRINT studies within the merge window (identified by images with
        // Source == "PRINT"). Absorb Receiving prints AND prints that already completed
        // (60s stabilization) when they share the same CallingAeTitle — otherwise a print
        // that finished stabilizing before the CT study completes is stranded as an orphan.
        // This deliberately does NOT match other C-STORE studies of a different patient —
        // only film prints and same-patient studies merge here, so we never absorb an
        // unrelated CT/OT study.
        var toMerge = await db.Studies
            .Include(s => s.Patient)
            .Include(s => s.Series).ThenInclude(s => s.Images)
            .AsSplitQuery()
            .Where(s => s.Id != targetStudy.Id
                && (s.Status == StudyStatus.Receiving || s.Status == StudyStatus.Complete)
                && s.LastUpdatedAt >= windowStart
                && s.Series.Any(sr => sr.Images.Any(i => i.Source == "PRINT")))
            .OrderByDescending(s => s.LastUpdatedAt)
            .ToListAsync(ct);

        // Also merge same-patient studies (even if Complete), regardless of timing.
        // CT/OT/SC for the same patient consolidate into one study — no time window.
        if (targetStudy.PatientId > 0)
        {
            var samePatient = await db.Studies
                .Include(s => s.Patient)
                .Include(s => s.Series).ThenInclude(s => s.Images)
                .AsSplitQuery()
                .Where(s => s.Id != targetStudy.Id
                    && s.PatientId == targetStudy.PatientId
                    && (s.Status == StudyStatus.Receiving || s.Status == StudyStatus.Complete))
                .OrderByDescending(s => s.LastUpdatedAt)
                .ToListAsync(ct);

            foreach (var sp in samePatient)
            {
                if (!toMerge.Any(p => p.Id == sp.Id))
                    toMerge.Add(sp);
            }
        }

        foreach (var printStudy in toMerge)
        {
            // Re-point all series to the target study.
            foreach (var series in printStudy.Series)
                series.StudyId = targetStudy.Id;

            // Re-point print jobs.
            var printJobs = await db.PrintJobs.Where(p => p.StudyId == printStudy.Id).ToListAsync(ct);
            foreach (var pj in printJobs)
            {
                pj.StudyId = targetStudy.Id;
                pj.PatientId = targetStudy.PatientId;
            }

            // Move archive directory under target study's dir.
            var printImage = printStudy.Series.SelectMany(s => s.Images).FirstOrDefault();
            var targetImage = targetStudy.Series.SelectMany(s => s.Images).FirstOrDefault();
            if (printImage != null && targetImage != null)
            {
                var printStudyDir = Directory.GetParent(Path.GetDirectoryName(printImage.FilePath) ?? "")?.FullName;
                var targetStudyDir = Directory.GetParent(Path.GetDirectoryName(targetImage.FilePath) ?? "")?.FullName;
                if (!string.IsNullOrEmpty(printStudyDir) && !string.IsNullOrEmpty(targetStudyDir)
                    && Directory.Exists(printStudyDir) && Directory.Exists(targetStudyDir))
                {
                    try
                    {
                        var dirName = Path.GetFileName(printStudyDir);
                        var newDir = Path.Combine(targetStudyDir, dirName);
                        if (Directory.Exists(newDir))
                            newDir = Path.Combine(targetStudyDir, dirName + "_merged_" + DateTime.UtcNow.ToString("HHmmss"));

                        Directory.Move(printStudyDir, newDir);

                        foreach (var img in printStudy.Series.SelectMany(s => s.Images))
                        {
                            if (!string.IsNullOrEmpty(img.FilePath) && img.FilePath.StartsWith(printStudyDir, StringComparison.OrdinalIgnoreCase))
                                img.FilePath = newDir + img.FilePath.Substring(printStudyDir.Length);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to move print archive dir into target study {TargetId}", targetStudy.Id);
                    }
                }
            }

            // Remove the orphaned print study. Series were already re-pointed to target,
            // so they no longer reference this Study — cascade won't delete them.
            // Do NOT Remove series individually here — that would delete the just-merged content.
            var printPatientId = printStudy.PatientId;
            db.Studies.Remove(printStudy);

            // Clean up the now-orphaned Patient row (e.g. anonymous PatientId="")
            // if no other study still references it — prevents phantom patient records.
            if (printPatientId != targetStudy.PatientId)
            {
                var stillUsed = await db.Studies.AnyAsync(s => s.PatientId == printPatientId && s.Id != printStudy.Id, ct);
                if (!stillUsed)
                {
                    var orphanPatient = await db.Patients.FindAsync([printPatientId], ct);
                    if (orphanPatient != null)
                        db.Patients.Remove(orphanPatient);
                }
            }

            _logger.LogInformation("Reverse-merged print study {PrintStudyId} (patient='{Patient}') into CT study {TargetId} (AE={Ae})",
                printStudy.Id, printPatientId, targetStudy.Id, targetStudy.CallingAeTitle);
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
