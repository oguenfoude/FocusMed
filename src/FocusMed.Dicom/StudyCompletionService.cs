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
        _logger.LogInformation("Study Completion Service is starting. Stabilization Window: {Seconds}s", _stabilizationSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessReceivingStudiesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while processing receiving studies.");
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
            .Include(s => s.Series)
            .Where(s => s.Status == StudyStatus.Receiving && s.LastUpdatedAt <= cutoffTime)
            .ToListAsync(stoppingToken);

        foreach (var study in readyStudies)
        {
            study.Status = StudyStatus.Complete;
            _logger.LogInformation("Study {StudyUid} has stabilized and is now Complete.", study.StudyInstanceUid);
        }

        if (readyStudies.Any())
        {
            await db.SaveChangesAsync(stoppingToken);
        }
    }
}
