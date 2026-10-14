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
    private readonly int _archiveRetentionDays;
    private readonly int _printMergeWindowSeconds;

    public StudyCompletionService(
        IServiceScopeFactory scopeFactory,
        ILogger<StudyCompletionService> logger,
        IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _archiveRetentionDays = configuration.GetValue<int>("ArchiveRetentionDays", 0);
        var window = configuration.GetValue<int>("DicomNetworking:PrintMergeWindowSeconds", 300);
        _printMergeWindowSeconds = window > 0 ? window : 300;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await AbsorbAnonymousPrintsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Print absorb error");
            }

            if (_archiveRetentionDays > 0)
            {
                try { await CleanupOldArchivesAsync(stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Archive cleanup error");
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    /// <summary>
    /// Print-first auto-join: a SonoVision print carries no identity, so when it arrives
    /// before the C-STORE there is no named study to attach to yet. When the named study
    /// from the same AE shows up (still Receiving), absorb recent anonymous SC-only
    /// studies from that AE into it. Runs with its own save per target.
    /// Study termination is manual-only; this service never flips Receiving to Complete. Direction is always anonymous to named.
    /// </summary>
    private async Task AbsorbAnonymousPrintsAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FocusMedDbContext>();

        var now = DateTime.UtcNow;
        var cutoff = now.AddSeconds(-_printMergeWindowSeconds);

        // Cheap idle early-exit: no Include — only Study scalars are read below and the
        // PatientId/PatientName filter translates to a JOIN. Zero Receiving rows returns
        // here after a single indexed Status query, before any candidate/dir work.
        var targets = await db.Studies
            .AsSplitQuery()
            .Where(s => s.Status == StudyStatus.Receiving
                && !string.IsNullOrWhiteSpace(s.CallingAeTitle)
                && s.Patient != null
                && (!string.IsNullOrWhiteSpace(s.Patient.PatientId) || !string.IsNullOrWhiteSpace(s.Patient.PatientName)))
            .OrderBy(s => s.Id)
            .Take(50)
            .ToListAsync(stoppingToken);
        if (targets.Count == 0)
            return;

        var dataDir = Environment.GetEnvironmentVariable("FOCUSMED_DATA")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusMed");
        var archiveRoot = Path.Combine(dataDir, "archive");

        var absorbedPatients = new HashSet<int>();
        var totalAbsorbed = 0;

        foreach (var target in targets)
        {
            stoppingToken.ThrowIfCancellationRequested();

            var candidates = await db.Studies
                .Include(s => s.Patient)
                .Include(s => s.Series)
                .AsSplitQuery()
                .Where(s => s.Id != target.Id
                    && s.CallingAeTitle == target.CallingAeTitle
                    && (s.Status == StudyStatus.Receiving || s.Status == StudyStatus.Complete)
                    && s.CreatedAt >= cutoff)
                .OrderBy(s => s.CreatedAt)
                .Take(10)
                .ToListAsync(stoppingToken);

            foreach (var source in candidates)
            {
                var examGap = Math.Abs((source.CreatedAt - target.CreatedAt).TotalSeconds);
                if (examGap > _printMergeWindowSeconds)
                    continue;
                var anonymous = source.Patient == null
                    || (string.IsNullOrWhiteSpace(source.Patient.PatientId) && string.IsNullOrWhiteSpace(source.Patient.PatientName));
                if (!anonymous)
                    continue;
                if (source.Series.Count != 0 && source.Series.Any(x => x.Modality != "SC"))
                    continue;

                var sourceSeriesIds = source.Series.Select(x => x.Id).ToList();
                var sourceImagePath = await db.DicomImages
                    .Where(i => sourceSeriesIds.Contains(i.SeriesId))
                    .Select(i => i.FilePath)
                    .FirstOrDefaultAsync(stoppingToken);
                var targetImagePath = await db.DicomImages
                    .Where(i => i.Series.StudyId == target.Id)
                    .Select(i => i.FilePath)
                    .FirstOrDefaultAsync(stoppingToken);

                foreach (var seriesItem in source.Series)
                    seriesItem.StudyId = target.Id;

                var printJobs = await db.PrintJobs.Where(p => p.StudyId == source.Id).ToListAsync(stoppingToken);
                foreach (var printJob in printJobs)
                {
                    printJob.StudyId = target.Id;
                    printJob.PatientId = target.PatientId;
                }

                await MoveStudyDirAsync(db, archiveRoot, sourceImagePath, targetImagePath, sourceSeriesIds, source.Id, target.Id, stoppingToken);

                absorbedPatients.Add(source.PatientId);
                db.Studies.Remove(source);
                target.LastUpdatedAt = now;
                totalAbsorbed++;
                _logger.LogInformation("Print absorb: anonymous SC study {SourceId} -> named study {TargetId} (ae={Ae}, gap={Gap:F0}s, {Series} series, {Jobs} print jobs)",
                    source.Id, target.Id, target.CallingAeTitle, examGap, sourceSeriesIds.Count, printJobs.Count);
            }

            // Save per target (not once at the end): each absorbed merge
            // must be committed before the next target is evaluated.
            if (totalAbsorbed > 0)
                await db.SaveChangesAsync(stoppingToken);
        }

        if (totalAbsorbed == 0)
            return;

        foreach (var pid in absorbedPatients)
        {
            if (await db.Studies.AnyAsync(s => s.PatientId == pid, stoppingToken)) continue;
            var patient = await db.Patients.FindAsync([pid], stoppingToken);
            if (patient != null) db.Patients.Remove(patient);
        }
        await db.SaveChangesAsync(stoppingToken);

        _logger.LogInformation("Print absorb: merged {Count} anonymous SC studies into named same-AE studies", totalAbsorbed);
    }

    private async Task MoveStudyDirAsync(FocusMedDbContext db, string archiveRoot, string? sourceImagePath, string? targetImagePath, List<int> sourceSeriesIds, int sourceId, int targetId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(sourceImagePath) || string.IsNullOrEmpty(targetImagePath))
        {
            _logger.LogDebug("Absorb skips dir move {SourceId} -> {TargetId}: one side has no images yet", sourceId, targetId);
            return;
        }
        var sourceStudyDir = Directory.GetParent(Path.GetDirectoryName(sourceImagePath) ?? string.Empty)?.FullName;
        var targetStudyDir = Directory.GetParent(Path.GetDirectoryName(targetImagePath) ?? string.Empty)?.FullName;
        if (string.IsNullOrEmpty(sourceStudyDir) || string.IsNullOrEmpty(targetStudyDir)
            || !DicomHelpers.IsSubdirectoryOf(sourceStudyDir, archiveRoot)
            || !DicomHelpers.IsSubdirectoryOf(targetStudyDir, archiveRoot))
        {
            _logger.LogWarning("Absorb skips dir move {SourceId} -> {TargetId}: unsafe or missing dirs", sourceId, targetId);
            return;
        }
        if (string.Equals(sourceStudyDir, targetStudyDir, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            var dirName = Path.GetFileName(sourceStudyDir);
            var newSourceDir = Path.Combine(targetStudyDir, dirName);
            if (Directory.Exists(newSourceDir))
                newSourceDir = Path.Combine(targetStudyDir, dirName + "_merged_" + DateTime.UtcNow.ToString("HHmmss"));
            Directory.Move(sourceStudyDir, newSourceDir);

            var movedImages = await db.DicomImages.Where(i => sourceSeriesIds.Contains(i.SeriesId)).ToListAsync(ct);
            var rewritten = 0;
            // Trailing separator: sibling dirs sharing a string prefix (StudyA vs
            // StudyAB) must never have paths rewritten into the wrong study.
            var prefix = sourceStudyDir + Path.DirectorySeparatorChar;
            foreach (var img in movedImages)
            {
                if (!string.IsNullOrEmpty(img.FilePath) && img.FilePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    img.FilePath = newSourceDir + img.FilePath.Substring(prefix.Length - 1);
                    rewritten++;
                }
                if (!string.IsNullOrEmpty(img.PngPath) && img.PngPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    img.PngPath = newSourceDir + img.PngPath.Substring(prefix.Length - 1);
                    rewritten++;
                }
            }
            _logger.LogDebug("Absorb moved dir {SourceId} -> {TargetId}: rewrote {Paths} paths", sourceId, targetId, rewritten);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Absorb dir move failed {SourceId} -> {TargetId} (DB rows still consistent)", sourceId, targetId);
        }
    }

    private async Task CleanupOldArchivesAsync(CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddDays(-_archiveRetentionDays);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FocusMedDbContext>();

        var dataDir = Environment.GetEnvironmentVariable("FOCUSMED_DATA")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusMed");
        var archiveRoot = Path.Combine(dataDir, "archive");
        var imagesRoot = Path.Combine(dataDir, "images");

        var oldStudies = await db.Studies
            .Include(s => s.Series).ThenInclude(s => s.Images)
            .AsSplitQuery()
            .Where(s => (s.Status == StudyStatus.Archived || s.Status == StudyStatus.Deleted)
                && s.LastUpdatedAt < cutoff)
            .ToListAsync(ct);

        if (oldStudies.Count == 0) return;

        var archiveDirs = new HashSet<string>();
        var pngDirs = new HashSet<string>();
        var resumes = new List<string>();
        var patientIds = new HashSet<int>();
        foreach (var study in oldStudies)
        {
            foreach (var img in study.Series.SelectMany(s => s.Images))
            {
                if (!string.IsNullOrEmpty(img.FilePath))
                {
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
            if (!string.IsNullOrEmpty(study.ResumePdfPath)) resumes.Add(study.ResumePdfPath);
            if (study.PatientId != 0) patientIds.Add(study.PatientId);
            db.Studies.Remove(study);
        }

        await db.SaveChangesAsync(ct);

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
        foreach (var resume in resumes)
        {
            var resumeFull = Path.GetFullPath(Path.Combine(dataDir, resume));
            if (!resumeFull.StartsWith(dataDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            try { if (File.Exists(resumeFull)) File.Delete(resumeFull); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete resume PDF {Path}", resumeFull); }
        }

        foreach (var pid in patientIds)
        {
            if (await db.Studies.AnyAsync(s => s.PatientId == pid, ct)) continue;
            var patient = await db.Patients.FindAsync(pid);
            if (patient != null) db.Patients.Remove(patient);
        }
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Archive cleanup: removed {Count} studies older than {Days}d ({Dirs} dirs)",
            oldStudies.Count, _archiveRetentionDays, archiveDirs.Count);
    }
}
