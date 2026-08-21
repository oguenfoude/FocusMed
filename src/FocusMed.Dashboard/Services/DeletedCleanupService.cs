using FocusMed.Data;
using FocusMed.Data.Entities;
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
                            foreach (var img in images)
                            {
                                if (!string.IsNullOrEmpty(img.FilePath))
                                {
                                    var dir = Path.GetDirectoryName(img.FilePath);
                                    if (dir != null)
                                    {
                                        var seriesDir = Directory.GetParent(dir)?.FullName;
                                        if (seriesDir != null)
                                        {
                                            var studyDir = Directory.GetParent(seriesDir)?.FullName;
                                            if (studyDir != null) archiveDirs.Add(studyDir);
                                        }
                                    }
                                }
                            }

                            foreach (var dir in archiveDirs)
                            {
                                try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
                                catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete archive dir {Dir}", dir); }
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
