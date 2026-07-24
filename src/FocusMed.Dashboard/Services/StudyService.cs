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
