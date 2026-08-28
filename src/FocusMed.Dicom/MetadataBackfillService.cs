using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FocusMed.Dicom;

/// <summary>
/// Runs DicomUpsertService.BackfillMetadataAsync as a background task AFTER the host starts,
/// so the DICOM listener binds its port immediately instead of being blocked by a potentially
/// minutes-long metadata sweep on first boot of a large archive.
/// </summary>
public sealed class MetadataBackfillService(
    IServiceScopeFactory scopeFactory,
    ILogger<MetadataBackfillService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            logger.LogInformation("Background metadata backfill starting");
            using var scope = scopeFactory.CreateScope();
            var upsertService = scope.ServiceProvider.GetRequiredService<DicomUpsertService>();
            await upsertService.BackfillMetadataAsync(stoppingToken);
            logger.LogInformation("Background metadata backfill finished");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Background metadata backfill failed (non-fatal)");
        }
    }
}
