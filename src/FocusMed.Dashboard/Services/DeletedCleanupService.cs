using FocusMed.Data;
using FocusMed.Data.Entities;
using FocusMed.Dicom;
using Microsoft.EntityFrameworkCore;

namespace FocusMed.Dashboard.Services;

public class DeletedCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DeletedCleanupService> _logger;

    public DeletedCleanupService(IServiceScopeFactory scopeFactory, ILogger<DeletedCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FocusMedDbContext>();

                var cutoff = DateTime.UtcNow.AddDays(-30);
                var oldDeleted = await db.Studies
                    .Where(s => s.Status == StudyStatus.Deleted && s.LastUpdatedAt < cutoff)
                    .ToListAsync(stoppingToken);

                if (oldDeleted.Count > 0)
                {
                    _logger.LogInformation("Auto-deleting {Count} studies older than 30 days", oldDeleted.Count);
                    var dataDir = Environment.GetEnvironmentVariable("FOCUSMED_DATA")
                        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusMed");
                    var archiveRoot = Path.Combine(dataDir, "archive");
                    var imagesRoot = Path.Combine(dataDir, "images");
                    foreach (var study in oldDeleted)
                    {
                        try
                        {
                            var images = await db.DicomImages
                                .Include(i => i.Frames)
                                .Include(i => i.Series)
                                .AsSplitQuery()
                                .Where(i => i.Series.StudyId == study.Id)
                                .ToListAsync(stoppingToken);

                            var archiveDirs = new HashSet<string>();
                            var pngDirs = new HashSet<string>();
                            foreach (var img in images)
                            {
                                if (!string.IsNullOrEmpty(img.FilePath))
                                {
                                    // FilePath = archive/<study>/<series>/<file>.dcm, so the
                                    // study dir is exactly ONE level above the series dir.
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
                            var resumeToDelete = study.ResumePdfPath;

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
                            if (!string.IsNullOrEmpty(resumeToDelete))
                            {
                                var resumeFull = Path.GetFullPath(Path.Combine(dataDir, resumeToDelete));
                                if (resumeFull.StartsWith(dataDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                                {
                                    try { if (File.Exists(resumeFull)) File.Delete(resumeFull); }
                                    catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete resume PDF {Path}", resumeFull); }
                                }
                            }

                            foreach (var img in images)
                                db.DicomFrames.RemoveRange(img.Frames);
                            db.DicomImages.RemoveRange(images);

                            var series = await db.Series.Where(s => s.StudyId == study.Id).ToListAsync(stoppingToken);
                            db.Series.RemoveRange(series);

                            var patientId = study.PatientId;
                            db.Studies.Remove(study);
                            await db.SaveChangesAsync(stoppingToken);

                            if (patientId != 0)
                            {
                                var hasOtherStudies = await db.Studies.AnyAsync(s => s.PatientId == patientId, stoppingToken);
                                if (!hasOtherStudies)
                                {
                                    var patient = await db.Patients.FindAsync(patientId);
                                    if (patient != null)
                                    {
                                        db.Patients.Remove(patient);
                                        await db.SaveChangesAsync(stoppingToken);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to auto-delete study {StudyId}", study.Id);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during deleted studies cleanup");
            }

            await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
        }
    }
}
