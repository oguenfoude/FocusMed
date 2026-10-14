using FocusMed.Data;
using FocusMed.Data.Entities;
using FocusMed.Dicom;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FocusMed.Dashboard.Services;

/// <summary>
/// Background PDF pre-warm: renders the DEFAULT preview (same request StudyDetails would
/// build on open, via PdfPreviewDefaults) for stable Receiving studies (LastUpdatedAt older
/// than the StudyStabilizationSeconds window, default 60 - same rule the Worker auto-complete
/// used), so opening a study the operator is about to terminate is a cache hit (~instant)
/// instead of a 10-30s render. Also warms PNGs as a side effect (GetOrExtractFramesAsync
/// renders missing frames). Polls every 10s, max 3 full warms per pass; tracks warmed
/// (studyId to LastUpdatedAt seen) and re-warms when the study changes (merge/absorb bumps
/// LastUpdatedAt). Failures are logged and skipped.
/// </summary>
public class PdfPrewarmService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PdfPrewarmService> _logger;
    private readonly Dictionary<int, DateTime> _warmedAt = new();
    private readonly int _stabilizationSeconds;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private const int MaxWarmsPerPass = 3;

    public PdfPrewarmService(IServiceScopeFactory scopeFactory, ILogger<PdfPrewarmService> logger, IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _stabilizationSeconds = configuration.GetValue<int>("StudyStabilizationSeconds", 60);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PrewarmPassAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "PDF pre-warm pass failed (non-fatal)");
            }
            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PrewarmPassAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FocusMedDbContext>();
        var png = scope.ServiceProvider.GetRequiredService<PngExtractionService>();
        var pdf = scope.ServiceProvider.GetRequiredService<PdfService>();

        // Manual-Termine world: auto-complete no longer moves studies to Complete, so warm
        // the previews operators are about to open - Receiving studies idle past the
        // stabilization window (same cutoff the Worker auto-complete used).
        var cutoff = DateTime.UtcNow.AddSeconds(-_stabilizationSeconds);

        var candidates = await db.Studies
            .AsNoTracking()
            .Where(s => s.Status == StudyStatus.Receiving && s.LastUpdatedAt <= cutoff)
            .OrderByDescending(s => s.LastUpdatedAt)
            .Select(s => new { s.Id, s.LastUpdatedAt })
            .ToListAsync(ct);

        // Prune entries for studies that are gone or no longer Receiving-stable.
        var liveIds = new HashSet<int>(candidates.Select(s => s.Id));
        foreach (var id in _warmedAt.Keys.Where(k => !liveIds.Contains(k)).ToList())
            _warmedAt.Remove(id);

        var warmed = 0;
        foreach (var entry in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (warmed >= MaxWarmsPerPass) break;
            if (_warmedAt.TryGetValue(entry.Id, out var seen) && seen >= entry.LastUpdatedAt)
                continue;

            try
            {
                var study = await db.Studies
                    .Include(s => s.Patient)
                    .Include(s => s.Series).ThenInclude(s => s.Images)
                    .AsSplitQuery()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(s => s.Id == entry.Id, ct);
                if (study == null) continue;

                var frames = await png.GetOrExtractFramesAsync(study.Id, ct);
                var request = PdfPreviewDefaults.BuildDefault(study, frames);
                if (request == null) continue;
                // Torn-preview guard: snapshot the exact image list BEFORE releasing the
                // PNG refcount. After the render, any expected file that vanished means
                // GeneratePrintPdf rendered a partial set under the full key — drop the
                // cache file so a partial preview is never served as complete.
                var expectedPaths = request.ImagePaths.ToList();
                try
                {
                    png.ReleaseStudyPng(study.StudyInstanceUid);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Pre-warm refcount release skipped for study {StudyId}", study.Id);
                }

                var warmedUrl = await pdf.GeneratePrintPdf(
                    request.PatientName, request.StudyDate, request.Description,
                    request.ImagePaths, request.ResumePdfPath, request.PageSize,
                    request.IsBooklet, request.ImagesPerPage, request.GapPx, request.MarginPx, ct);
                if (!string.IsNullOrEmpty(warmedUrl) && expectedPaths.Any(p => !File.Exists(p)))
                {
                    _logger.LogWarning("Pre-warm for study {StudyId} raced file deletion ({Missing}/{Total} missing); dropping torn preview",
                        study.Id, expectedPaths.Count(p => !File.Exists(p)), expectedPaths.Count);
                    try { await pdf.DeletePdfAsync(warmedUrl); } catch (Exception ex) { _logger.LogDebug(ex, "Torn preview delete failed (non-fatal)"); }
                    continue;
                }
                warmed++;
                _logger.LogDebug("Pre-warmed PDF preview for study {StudyId} ({Images} images)", study.Id, request.ImagePaths.Count);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Pre-warm failed for study {StudyId} (non-fatal)", entry.Id);
            }
            finally
            {
                _warmedAt[entry.Id] = DateTime.UtcNow;
            }
        }

        if (warmed > 0)
            _logger.LogInformation("PDF pre-warm: warmed {Count} studies", warmed);
    }
}
