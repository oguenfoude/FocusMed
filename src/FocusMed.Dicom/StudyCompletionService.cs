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
    private readonly int _stabilizationSeconds;

    public StudyCompletionService(IServiceScopeFactory scopeFactory, ILogger<StudyCompletionService> logger, IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _stabilizationSeconds = configuration.GetValue<int>("StudyStabilizationSeconds", 60);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessReceivingStudiesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Study completion error");
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
            var imageCount = study.Series.SelectMany(s => s.Images).Count();

            // Re-check: verify no new images arrived since query (C-STORE race)
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
        }

        await db.SaveChangesAsync(stoppingToken);
    }
}
