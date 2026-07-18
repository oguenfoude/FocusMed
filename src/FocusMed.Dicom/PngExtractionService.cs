using System.Collections.Concurrent;
using FellowOakDicom;
using FocusMed.Data;
using FocusMed.Data.Entities;
using FocusMed.Dicom.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FocusMed.Dicom;

public class PngExtractionService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PngExtractionService> _logger;
    private readonly string _imagesPath;
    private readonly bool _enabled;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _studyLocks = new();
    private static readonly ConcurrentDictionary<string, int> _studyRefCount = new();

    public PngExtractionService(
        IServiceScopeFactory scopeFactory,
        ILogger<PngExtractionService> logger,
        IOptions<PngExtractionOptions> options)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _enabled = options.Value.Enabled;
        var dataDir = Environment.GetEnvironmentVariable("FOCUSMED_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusMed");
        _imagesPath = Path.Combine(dataDir, "images");
        Directory.CreateDirectory(_imagesPath);
    }

    public async Task<IReadOnlyList<FrameResult>> GetOrExtractFramesAsync(int studyId, CancellationToken ct = default)
    {
        if (!_enabled)
            return [];

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FocusMedDbContext>();

        var images = await db.DicomImages
            .Include(i => i.Series)
            .ThenInclude(s => s.Study)
                .ThenInclude(s => s!.Patient)
            .Include(i => i.Frames)
            .AsSplitQuery()
            .Where(i => i.Series.StudyId == studyId)
            .OrderBy(i => i.Series!.SeriesInstanceUid)
            .ThenBy(i => i.SopInstanceUid)
            .ToListAsync(ct);

        if (images.Count == 0)
            return [];

        var studyUid = images[0].Series?.Study?.StudyInstanceUid;
        if (string.IsNullOrEmpty(studyUid))
            return [];

        _studyRefCount.AddOrUpdate(studyUid, 1, (_, old) => old + 1);

        var results = new List<FrameResult>();

        foreach (var image in images)
        {
            var currentImage = image;
            if (string.IsNullOrEmpty(currentImage.PngPath))
            {
                await ExtractForImageAsync(currentImage, ct);
                var updated = await db.DicomImages
                    .Include(i => i.Frames)
                    .FirstOrDefaultAsync(i => i.Id == currentImage.Id, ct);
                if (updated != null)
                    currentImage = updated;
            }

            var frames = currentImage.Frames
                .OrderBy(f => f.FrameIndex)
                .Select(f => new FrameResult(
                    currentImage.SopInstanceUid,
                    f.FrameIndex,
                    f.PngPath,
                    f.PngPath != null && File.Exists(f.PngPath)))
                .ToList();

            results.AddRange(frames);
        }

        return results;
    }

    public void ReleaseStudyPng(int studyId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FocusMedDbContext>();

        var study = db.Studies.Find(studyId);
        if (study == null) return;

        var studyUid = study.StudyInstanceUid;
        if (string.IsNullOrEmpty(studyUid)) return;

        _studyRefCount.AddOrUpdate(studyUid, 0, (_, old) => Math.Max(0, old - 1));
    }

    internal bool IsStudyInUse(string studyUid)
    {
        return _studyRefCount.TryGetValue(studyUid, out var count) && count > 0;
    }

    public async Task ExtractForStudyAsync(int studyId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FocusMedDbContext>();

        var images = await db.DicomImages
            .Include(i => i.Series)
            .ThenInclude(s => s.Study)
                .ThenInclude(s => s!.Patient)
            .Where(i => i.Series.StudyId == studyId && i.PngPath == null)
            .ToListAsync(ct);

        foreach (var image in images)
        {
            await ExtractForImageAsync(image, ct);
        }
    }

    public async Task ExtractForImageAsync(DicomImage dicomImage, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(dicomImage.PngPath))
            return;

        if (string.IsNullOrEmpty(dicomImage.FilePath) || !File.Exists(dicomImage.FilePath))
        {
            _logger.LogWarning("Cannot extract PNG: file missing at {FilePath}", dicomImage.FilePath);
            return;
        }

        var studyUid = dicomImage.Series?.Study?.StudyInstanceUid;
        if (string.IsNullOrEmpty(studyUid))
        {
            _logger.LogWarning("Cannot extract PNG: study UID unknown for image {Id}", dicomImage.Id);
            return;
        }

        var studyLock = _studyLocks.GetOrAdd(studyUid, _ => new SemaphoreSlim(1, 1));
        await studyLock.WaitAsync(ct);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FocusMedDbContext>();
            var fresh = await db.DicomImages
                .Include(i => i.Series)
                .ThenInclude(s => s.Study)
                    .ThenInclude(s => s!.Patient)
                .FirstOrDefaultAsync(i => i.Id == dicomImage.Id, ct);

            if (fresh == null || !string.IsNullOrEmpty(fresh.PngPath))
                return;

            var patientName = fresh.Series?.Study?.Patient?.PatientName ?? "";
            var modality = fresh.Series?.Modality ?? "OT";
            var studyDate = fresh.Series?.Study?.StudyDate;
            var seriesUid = fresh.Series?.SeriesInstanceUid ?? "";

            var studyHash = DicomHelpers.GetFnv1aHash(studyUid);
            var safePatientName = DicomHelpers.SanitizeFileName(patientName);
            var safeModality = DicomHelpers.SanitizeFileName(modality);
            var datePart = studyDate?.ToString("yyyyMMdd") ?? "nodate";
            var studyDirName = $"{safePatientName}_{safeModality}_{datePart}_{studyHash}";

            var imagesDir = Path.Combine(_imagesPath, studyDirName, seriesUid);
            Directory.CreateDirectory(imagesDir);

            var dicomFile = await DicomFile.OpenAsync(fresh.FilePath);
            var dataset = dicomFile.Dataset;
            var image = new FellowOakDicom.Imaging.DicomImage(dataset);

            for (int frameIndex = 0; frameIndex < image.NumberOfFrames; frameIndex++)
            {
                var pngPath = Path.Combine(imagesDir, $"{fresh.SopInstanceUid}_{frameIndex:D4}.png");

                using var renderedImage = image.RenderImage(frameIndex);
                using var sharpImage = renderedImage.As<SixLabors.ImageSharp.Image>();
                await SixLabors.ImageSharp.ImageExtensions.SaveAsPngAsync(sharpImage, pngPath, ct);

                if (frameIndex == 0)
                    fresh.PngPath = pngPath;

                fresh.Frames.Add(new DicomFrame
                {
                    FrameIndex = frameIndex,
                    PngPath = pngPath
                });
            }

            await db.SaveChangesAsync(ct);
            _logger.LogInformation("PNG extracted: {SopUid} ({Frames} frames)", fresh.SopInstanceUid, image.NumberOfFrames);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PNG extraction failed for {SopUid}", dicomImage.SopInstanceUid);
        }
        finally
        {
            studyLock.Release();
            _studyRefCount.AddOrUpdate(studyUid, 0, (_, old) => Math.Max(0, old - 1));
            if (!_studyRefCount.TryGetValue(studyUid, out var remaining) || remaining <= 0)
            {
                _studyLocks.TryRemove(studyUid, out _);
                _studyRefCount.TryRemove(studyUid, out _);
            }
        }
    }
}

public record FrameResult(string SopInstanceUid, int FrameIndex, string? PngPath, bool FileExists);
