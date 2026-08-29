using FocusMed.Data;
using FocusMed.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FocusMed.Dashboard.Services;

public class StudyService
{
    private readonly FocusMedDbContext _db;
    private readonly ILogger<StudyService> _logger;

    public StudyService(FocusMedDbContext db, ILogger<StudyService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<bool> MergeStudyAsync(int sourceStudyId, int targetStudyId)
    {
        if (sourceStudyId == targetStudyId) return false;

        var sourceStudy = await _db.Studies.FindAsync(sourceStudyId);
        var targetStudy = await _db.Studies.FindAsync(targetStudyId);
        if (sourceStudy == null || targetStudy == null || targetStudy.Status == StudyStatus.Deleted)
            return false;

        // Capture archive paths BEFORE re-pointing anything.
        var sourceImagePath = await _db.DicomImages
            .Where(i => i.Series.StudyId == sourceStudyId)
            .Select(i => i.FilePath)
            .FirstOrDefaultAsync();
        var targetImagePath = await _db.DicomImages
            .Where(i => i.Series.StudyId == targetStudyId)
            .Select(i => i.FilePath)
            .FirstOrDefaultAsync();

        // Re-point all series to the target study.
        var sourceSeries = await _db.Series.Where(s => s.StudyId == sourceStudyId).ToListAsync();
        foreach (var seriesItem in sourceSeries)
            seriesItem.StudyId = targetStudyId;

        // Re-point print jobs (and their patient link) to the target study.
        var printJobs = await _db.PrintJobs.Where(p => p.StudyId == sourceStudyId).ToListAsync();
        foreach (var printJob in printJobs)
        {
            printJob.StudyId = targetStudyId;
            printJob.PatientId = targetStudy.PatientId;
        }

        // Best-effort: move the source archive directory under the target study directory.
        if (!string.IsNullOrEmpty(sourceImagePath) && !string.IsNullOrEmpty(targetImagePath))
        {
            var sourceStudyDir = Directory.GetParent(Path.GetDirectoryName(sourceImagePath) ?? string.Empty)?.FullName;
            var targetStudyDir = Directory.GetParent(Path.GetDirectoryName(targetImagePath) ?? string.Empty)?.FullName;
            if (!string.IsNullOrEmpty(sourceStudyDir) && !string.IsNullOrEmpty(targetStudyDir)
                && Directory.Exists(sourceStudyDir) && Directory.Exists(targetStudyDir))
            {
                try
                {
                    var dirName = Path.GetFileName(sourceStudyDir);
                    var newSourceDir = Path.Combine(targetStudyDir, dirName);
                    if (Directory.Exists(newSourceDir))
                        newSourceDir = Path.Combine(targetStudyDir, dirName + "_merged_" + DateTime.UtcNow.ToString("HHmmss"));

                    Directory.Move(sourceStudyDir, newSourceDir);

                    var movedImages = await _db.DicomImages
                        .Where(i => i.Series.StudyId == targetStudyId && !string.IsNullOrEmpty(i.FilePath))
                        .ToListAsync();
                    foreach (var img in movedImages)
                    {
                        if (img.FilePath.StartsWith(sourceStudyDir, StringComparison.OrdinalIgnoreCase))
                            img.FilePath = newSourceDir + img.FilePath.Substring(sourceStudyDir.Length);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to move archive dir for merged study {SourceId} -> {TargetId}", sourceStudyId, targetStudyId);
                }
            }
        }

        // Remove the source study. Its series were re-pointed to the target above, so
        // they no longer reference this Study and the cascade delete will NOT touch them.
        // Do NOT RemoveRange the source series — that would cascade-delete the moved images.
        _db.Studies.Remove(sourceStudy);

        targetStudy.LastUpdatedAt = DateTime.UtcNow;
        if (targetStudy.Status == StudyStatus.Archived || targetStudy.Status == StudyStatus.Deleted)
            targetStudy.Status = StudyStatus.Complete;

        await _db.SaveChangesAsync();
        _logger.LogInformation("Merged study {SourceId} into {TargetId}", sourceStudyId, targetStudyId);
        return true;
    }

    public async Task DeleteStudyAsync(int studyId)
    {
        var images = await _db.DicomImages
            .Include(i => i.Frames)
            .Include(i => i.Series)
            .AsSplitQuery()
            .Where(i => i.Series.StudyId == studyId)
            .ToListAsync();

        var archiveDirs = new HashSet<string>();
        foreach (var img in images)
        {
            if (!string.IsNullOrEmpty(img.FilePath))
            {
                var dir = Path.GetDirectoryName(img.FilePath);
                if (dir != null)
                {
                    var studyDir = Directory.GetParent(dir)?.FullName;
                    if (studyDir != null) archiveDirs.Add(studyDir);
                }
            }
            _db.DicomFrames.RemoveRange(img.Frames);
        }
        _db.DicomImages.RemoveRange(images);

        var series = await _db.Series.Where(s => s.StudyId == studyId).ToListAsync();
        _db.Series.RemoveRange(series);

        var study = await _db.Studies.FindAsync(studyId);
        var patientId = study?.PatientId;
        if (study != null) _db.Studies.Remove(study);

        await _db.SaveChangesAsync();

        foreach (var dir in archiveDirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete archive dir {Dir}", dir); }
        }

        CleanupImageDirectories();

        if (patientId.HasValue)
        {
            var hasOtherStudies = await _db.Studies.AnyAsync(s => s.PatientId == patientId.Value);
            if (!hasOtherStudies)
            {
                var patient = await _db.Patients.FindAsync(patientId.Value);
                if (patient != null) _db.Patients.Remove(patient);
                await _db.SaveChangesAsync();
            }
        }
    }

    public async Task SoftDeleteStudyAsync(int studyId)
    {
        var study = await _db.Studies.FindAsync(studyId);
        if (study != null)
        {
            study.Status = StudyStatus.Deleted;
            study.LastUpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
    }

    public async Task RestoreStudyAsync(int studyId)
    {
        var study = await _db.Studies.FindAsync(studyId);
        if (study != null)
        {
            study.Status = StudyStatus.Complete;
            study.LastUpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
    }

    public async Task MarkCompleteAsync(int studyId)
    {
        var study = await _db.Studies.FindAsync(studyId);
        if (study != null)
        {
            study.Status = StudyStatus.Complete;
            study.LastUpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
    }

    public async Task ArchiveStudyAsync(int studyId)
    {
        var study = await _db.Studies.FindAsync(studyId);
        if (study != null)
        {
            study.Status = StudyStatus.Archived;
            study.LastUpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
    }

    public async Task<bool> UnlinkResumeAsync(int studyId)
    {
        var study = await _db.Studies.FindAsync(studyId);
        if (study == null || string.IsNullOrEmpty(study.ResumePdfPath))
            return false;

        var dataDir = Environment.GetEnvironmentVariable("FOCUSMED_DATA")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusMed");
        var absolutePath = Path.Combine(dataDir, study.ResumePdfPath);
        if (File.Exists(absolutePath))
        {
            try
            {
                File.Delete(absolutePath);
                _logger.LogInformation("Deleted resume PDF: {Path}", absolutePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete resume PDF {Path}", absolutePath);
            }
        }

        study.ResumePdfPath = null;
        study.LastUpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return true;
    }

    private void CleanupImageDirectories()
    {
        try
        {
            var dataDir = Environment.GetEnvironmentVariable("FOCUSMED_DATA")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusMed");
            var imagesBase = Path.Combine(dataDir, "images");
            if (!Directory.Exists(imagesBase)) return;

            foreach (var studyDir in Directory.GetDirectories(imagesBase))
            {
                foreach (var seriesDir in Directory.GetDirectories(studyDir))
                {
                    if (File.Exists(Path.Combine(seriesDir, ".keep"))) continue;
                    var pngs = Directory.GetFiles(seriesDir, "*.png");
                    if (pngs.Length == 0)
                    {
                        try { Directory.Delete(seriesDir, true); } catch { }
                    }
                }
                if (Directory.Exists(studyDir) && !Directory.EnumerateFileSystemEntries(studyDir).Any())
                {
                    try { Directory.Delete(studyDir, true); } catch { }
                }
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to cleanup image directories"); }
    }
}
