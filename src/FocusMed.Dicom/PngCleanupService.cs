using FocusMed.Data;
using FocusMed.Dicom.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FocusMed.Dicom;

public class PngCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PngExtractionService _pngService;
    private readonly ILogger<PngCleanupService> _logger;
    private readonly int _cleanupIntervalMinutes;
    private readonly int _maxAgeMinutes;
    private readonly bool _enabled;

    public PngCleanupService(
        IServiceScopeFactory scopeFactory,
        PngExtractionService pngService,
        ILogger<PngCleanupService> logger,
        IOptions<PngExtractionOptions> options)
    {
        _scopeFactory = scopeFactory;
        _pngService = pngService;
        _logger = logger;
        _cleanupIntervalMinutes = options.Value.CleanupIntervalMinutes;
        _maxAgeMinutes = options.Value.MaxAgeMinutes;
        _enabled = options.Value.Enabled;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("PNG cleanup disabled");
            return;
        }

        _logger.LogInformation("PNG cleanup started: interval={Interval}min, maxAge={MaxAge}min",
            _cleanupIntervalMinutes, _maxAgeMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupStalePngsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PNG cleanup error");
            }

            await Task.Delay(TimeSpan.FromMinutes(_cleanupIntervalMinutes), stoppingToken);
        }
    }

    private async Task CleanupStalePngsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FocusMedDbContext>();

        var cutoff = DateTime.UtcNow.AddMinutes(-_maxAgeMinutes);

        var staleFrames = await db.DicomFrames
            .Include(f => f.DicomImage)
                .ThenInclude(i => i!.Series)
                    .ThenInclude(s => s!.Study)
            .Where(f => f.PngPath != null && f.ExtractedAt < cutoff)
            .ToListAsync(ct);

        if (staleFrames.Count == 0)
            return;

        var framesByStudy = staleFrames
            .GroupBy(f => f.DicomImage!.Series!.Study!.StudyInstanceUid)
            .ToList();

        var deletedCount = 0;
        var skippedInUse = 0;

        foreach (var studyGroup in framesByStudy)
        {
            var studyUid = studyGroup.Key;

            if (_pngService.IsStudyInUse(studyUid))
            {
                skippedInUse += studyGroup.Count();
                continue;
            }

            foreach (var frame in studyGroup)
            {
                if (string.IsNullOrEmpty(frame.PngPath))
                    continue;

                try
                {
                    if (File.Exists(frame.PngPath))
                        File.Delete(frame.PngPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete PNG {Path}", frame.PngPath);
                    continue;
                }

                frame.PngPath = null;
                deletedCount++;
            }

            var affectedImageIds = studyGroup.Select(f => f.DicomImageId).Distinct().ToList();
            var imagesWithRemainingPngs = await db.DicomFrames
                .Where(f => affectedImageIds.Contains(f.DicomImageId) && f.PngPath != null)
                .GroupBy(f => f.DicomImageId)
                .Select(g => g.Key)
                .ToListAsync(ct);

            var imagesWithoutPngs = affectedImageIds.Except(imagesWithRemainingPngs).ToList();
            if (imagesWithoutPngs.Count > 0)
            {
                var images = await db.DicomImages
                    .Where(i => imagesWithoutPngs.Contains(i.Id))
                    .ToListAsync(ct);

                foreach (var image in images)
                {
                    image.PngPath = null;
                }
            }
        }

        if (deletedCount > 0)
        {
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("PNG cleanup: {Deleted} frames deleted, {Skipped} skipped (in use)",
                deletedCount, skippedInUse);
        }
    }
}
