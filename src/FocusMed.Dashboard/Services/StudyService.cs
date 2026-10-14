using FocusMed.Data;
using FocusMed.Data.Entities;
using FocusMed.Dicom;
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

    public async Task<bool> MergeStudyAsync(int sourceStudyId, int targetStudyId)
    {
        if (sourceStudyId == targetStudyId) return false;

        var sourceStudy = await _db.Studies.FindAsync(sourceStudyId);
        var targetStudy = await _db.Studies.FindAsync(targetStudyId);
        if (sourceStudy == null || targetStudy == null || targetStudy.Status == StudyStatus.Deleted)
            return false;
        // Stale-row guard: re-checked at confirm time. The merge picker row may predate a
        // soft-delete (another tab, auto-cleanup); merging FROM a Deleted study would
        // resurrect trashed images into a live study. Refuse instead.
        if (sourceStudy.Status == StudyStatus.Deleted)
        {
            _logger.LogWarning("Refusing merge: source study {SourceId} is Deleted (stale picker row)", sourceStudyId);
            return false;
        }

        // Named-wins direction guard: SV prints always carry empty demographics, so a click
        // merging named -> anonymous would silently orphan the patient identity. Swap so the
        // named study is always the merge target. (If the effective target is Deleted/Archived,
        // the restore below promotes it to Complete.)
        var sourcePatient = await _db.Patients.FindAsync(sourceStudy.PatientId);
        var targetPatient = await _db.Patients.FindAsync(targetStudy.PatientId);
        var sourceNamed = sourcePatient != null
            && (!string.IsNullOrWhiteSpace(sourcePatient.PatientId) || !string.IsNullOrWhiteSpace(sourcePatient.PatientName));
        var targetAnonymous = targetPatient == null
            || (string.IsNullOrWhiteSpace(targetPatient.PatientId) && string.IsNullOrWhiteSpace(targetPatient.PatientName));
        if (sourceNamed && targetAnonymous)
        {
            _logger.LogInformation("Merge direction swapped: named study {NamedId} kept as target (requested {SourceId} -> {TargetId})",
                sourceStudyId, sourceStudyId, targetStudyId);
            (sourceStudy, targetStudy) = (targetStudy, sourceStudy);
            (sourceStudyId, targetStudyId) = (targetStudyId, sourceStudyId);
        }

        // Capture archive paths BEFORE re-pointing anything.
        var sourceImagePath = await _db.DicomImages
            .Where(i => i.Series.StudyId == sourceStudyId)
            .Select(i => i.FilePath)
            .FirstOrDefaultAsync();
        var targetImagePath = await _db.DicomImages
            .Where(i => i.Series.StudyId == targetStudyId)
            .Select(i => i.FilePath)
            .FirstOrDefaultAsync();

        // Capture source Series IDs BEFORE re-pointing: after the FK update the moved
        // images are only identifiable by their original SeriesId set.
        var sourceSeriesIds = await _db.Series.Where(s => s.StudyId == sourceStudyId).Select(s => s.Id).ToListAsync();

        // Re-point all series to the target study.
        var sourceSeries = await _db.Series.Where(s => s.StudyId == sourceStudyId).ToListAsync();
        foreach (var seriesItem in sourceSeries)
            seriesItem.StudyId = targetStudyId;

        // Re-point print jobs (and their patient link) to the target study.
        var printJobs = await _db.PrintJobs.Where(p => p.StudyId == sourceStudyId).ToListAsync();
        foreach (var printJob in printJobs)
        {
            printJob.StudyId = targetStudyId;
            printJob.PatientId = targetStudy.PatientId;
        }

        // Best-effort: move the source archive directory under the target study directory.
        // DB stays consistent if the move is skipped or fails (paths still point at existing files).
        if (string.IsNullOrEmpty(sourceImagePath) || string.IsNullOrEmpty(targetImagePath))
        {
            _logger.LogDebug("Skipping archive dir move for merged study {SourceId} -> {TargetId}: {Side} has no images",
                sourceStudyId, targetStudyId, string.IsNullOrEmpty(sourceImagePath) ? "source" : "target");
        }
        else
        {
            var dataDir = Environment.GetEnvironmentVariable("FOCUSMED_DATA")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusMed");
            var archiveRoot = Path.Combine(dataDir, "archive");
            var sourceStudyDir = Directory.GetParent(Path.GetDirectoryName(sourceImagePath) ?? string.Empty)?.FullName;
            var targetStudyDir = Directory.GetParent(Path.GetDirectoryName(targetImagePath) ?? string.Empty)?.FullName;
            if (string.IsNullOrEmpty(sourceStudyDir) || string.IsNullOrEmpty(targetStudyDir)
                || !DicomHelpers.IsSubdirectoryOf(sourceStudyDir, archiveRoot)
                || !DicomHelpers.IsSubdirectoryOf(targetStudyDir, archiveRoot)
                || !Directory.Exists(sourceStudyDir) || !Directory.Exists(targetStudyDir))
            {
                _logger.LogWarning("Skipping archive dir move for merged study {SourceId} -> {TargetId}: unsafe or missing dirs (src={Src}, dst={Dst})",
                    sourceStudyId, targetStudyId, sourceStudyDir, targetStudyDir);
            }
            else if (string.Equals(sourceStudyDir, targetStudyDir, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Skipping archive dir move for merged study {SourceId} -> {TargetId}: same directory", sourceStudyId, targetStudyId);
            }
            else
            {
                try
                {
                    var dirName = Path.GetFileName(sourceStudyDir);
                    var newSourceDir = Path.Combine(targetStudyDir, dirName);
                    if (Directory.Exists(newSourceDir))
                        newSourceDir = Path.Combine(targetStudyDir, dirName + "_merged_" + DateTime.UtcNow.ToString("HHmmss"));

                    Directory.Move(sourceStudyDir, newSourceDir);

                    // Re-point by original SeriesId set (captured before the FK update), not by
                    // target StudyId -- the target may already hold images that must not be touched.
                    var movedImages = await _db.DicomImages
                        .Where(i => sourceSeriesIds.Contains(i.SeriesId))
                        .ToListAsync();
                    var touchedImages = 0;
                    var rewrittenPaths = 0;
                    foreach (var img in movedImages)
                    {
                        var touched = false;
                        if (!string.IsNullOrEmpty(img.FilePath) && img.FilePath.StartsWith(sourceStudyDir, StringComparison.OrdinalIgnoreCase))
                        {
                            img.FilePath = newSourceDir + img.FilePath.Substring(sourceStudyDir.Length);
                            rewrittenPaths++;
                            touched = true;
                        }
                        if (!string.IsNullOrEmpty(img.PngPath) && img.PngPath.StartsWith(sourceStudyDir, StringComparison.OrdinalIgnoreCase))
                        {
                            img.PngPath = newSourceDir + img.PngPath.Substring(sourceStudyDir.Length);
                            rewrittenPaths++;
                            touched = true;
                        }
                        if (touched) touchedImages++;
                    }
                    _logger.LogInformation("Merged study {SourceId} into {TargetId}: moved archive dir, rewrote {Paths} paths across {Images} images",
                        sourceStudyId, targetStudyId, rewrittenPaths, touchedImages);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to move archive dir for merged study {SourceId} -> {TargetId}", sourceStudyId, targetStudyId);
                }
            }
        }

        // Remove the source study. Its series were re-pointed to the target above, so
        // they no longer reference this Study and the cascade delete will NOT touch them.
        // Do NOT RemoveRange the source series — that would cascade-delete the moved images.
        _db.Studies.Remove(sourceStudy);

        targetStudy.LastUpdatedAt = DateTime.UtcNow;
        if (targetStudy.Status == StudyStatus.Archived || targetStudy.Status == StudyStatus.Deleted)
            targetStudy.Status = StudyStatus.Complete;

        await _db.SaveChangesAsync();
        _logger.LogInformation("Merged study {SourceId} into {TargetId}", sourceStudyId, targetStudyId);
        return true;
    }

    /// <summary>
    /// Persists the operator-defined image order (StudyDetails drag-and-drop). The client sends the
    /// full SOP UID list in display order; values are normalized to 0..n so later moves
    /// never collide. Images missing from the list keep their relative tail order.
    /// </summary>
    public async Task<bool> SetImageOrderAsync(int studyId, List<string> orderedSopUids)
    {
        var images = await _db.DicomImages
            .Include(i => i.Series)
            .Where(i => i.Series.StudyId == studyId)
            .ToListAsync();
        if (images.Count == 0 || orderedSopUids.Count == 0) return false;

        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var k = 0; k < orderedSopUids.Count; k++)
            rank.TryAdd(orderedSopUids[k], k);

        foreach (var img in images)
            img.SortOrder = rank.TryGetValue(img.SopInstanceUid, out var r) ? r : int.MaxValue;

        var ordered = images.OrderBy(i => i.SortOrder).ThenBy(i => i.Id).ToList();
        for (var k = 0; k < ordered.Count; k++)
            ordered[k].SortOrder = k;

        await _db.SaveChangesAsync();
        _logger.LogInformation("Reordered {Count} images in study {StudyId}", ordered.Count, studyId);
        return true;
    }

    /// <summary>
    /// Persists the operator's frame selection (StudyDetails checkboxes) so it survives
    /// reloads and future visits: selected SOPs get IsSelected=true, the rest false.
    /// Null (never touched) keeps the default rule until the first explicit change.
    /// </summary>
    public async Task<bool> SetImageSelectionAsync(int studyId, HashSet<string> selectedSopUids)
    {
        var images = await _db.DicomImages
            .Include(i => i.Series)
            .Where(i => i.Series.StudyId == studyId)
            .ToListAsync();
        if (images.Count == 0) return false;

        foreach (var img in images)
            img.IsSelected = selectedSopUids.Contains(img.SopInstanceUid);
        await _db.SaveChangesAsync();
        _logger.LogDebug("Saved selection for study {StudyId}: {Selected}/{Total} selected", studyId, selectedSopUids.Count, images.Count);
        return true;
    }

    /// <summary>
    /// Renames the study's patient (ID and/or name), e.g. to fill in identity for an
    /// anonymous SonoVision print study. Never touches other studies: if the current
    /// Patient row is shared, a dedicated row is created for this study (and the old
    /// row is removed when orphaned). If another patient already carries the target ID,
    /// the study is re-pointed to it (explicit operator patient-merge; name updated only
    /// when the existing one is empty). Archive dir is renamed best-effort with the same
    /// guards as merge (DB stays consistent if the move fails).
    /// </summary>
    public async Task<bool> RenamePatientAsync(int studyId, string newPatientId, string newPatientName)
    {
        newPatientId = (newPatientId ?? "").Trim();
        newPatientName = (newPatientName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(newPatientId) && string.IsNullOrWhiteSpace(newPatientName))
            return false;

        var study = await _db.Studies.Include(s => s.Patient).FirstOrDefaultAsync(s => s.Id == studyId);
        if (study?.Patient == null) return false;

        var current = study.Patient;
        var finalPatientId = string.IsNullOrWhiteSpace(newPatientId) ? current.PatientId : newPatientId;
        var finalPatientName = string.IsNullOrWhiteSpace(newPatientName) ? current.PatientName : newPatientName;
        if (finalPatientId == current.PatientId && finalPatientName == current.PatientName)
            return true;

        var oldPatientId = current.Id;
        var existing = await _db.Patients.FirstOrDefaultAsync(p => p.PatientId == finalPatientId && p.Id != current.Id);
        if (existing != null)
        {
            study.PatientId = existing.Id;
            if (string.IsNullOrWhiteSpace(existing.PatientName) && !string.IsNullOrWhiteSpace(finalPatientName))
                existing.PatientName = finalPatientName;
        }
        else
        {
            var shared = await _db.Studies.AnyAsync(s => s.PatientId == current.Id && s.Id != studyId);
            if (shared)
            {
                var clone = new Patient { PatientId = finalPatientId, PatientName = finalPatientName, BirthDate = current.BirthDate, Sex = current.Sex };
                _db.Patients.Add(clone);
                await _db.SaveChangesAsync();
                study.PatientId = clone.Id;
            }
            else
            {
                current.PatientId = finalPatientId;
                current.PatientName = finalPatientName;
            }
        }

        study.LastUpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        if (await _db.Studies.AnyAsync(s => s.PatientId == oldPatientId) == false)
        {
            var orphan = await _db.Patients.FindAsync(oldPatientId);
            if (orphan != null)
            {
                _db.Patients.Remove(orphan);
                await _db.SaveChangesAsync();
            }
        }

        RenameStudyDirectory(studyId, finalPatientName);
        _logger.LogInformation("Renamed patient for study {StudyId} to '{PatientId}' / '{PatientName}'", studyId, finalPatientId, finalPatientName);
        return true;
    }

    private void RenameStudyDirectory(int studyId, string patientName)
    {
        try
        {
            var dataDir = Environment.GetEnvironmentVariable("FOCUSMED_DATA")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusMed");
            var archiveRoot = Path.Combine(dataDir, "archive");
            var firstPath = _db.DicomImages
                .Where(i => i.Series.StudyId == studyId)
                .Select(i => i.FilePath)
                .FirstOrDefault();
            if (string.IsNullOrEmpty(firstPath)) return;
            var studyDir = Directory.GetParent(Path.GetDirectoryName(firstPath) ?? string.Empty)?.FullName;
            if (string.IsNullOrEmpty(studyDir) || !DicomHelpers.IsSubdirectoryOf(studyDir, archiveRoot) || !Directory.Exists(studyDir))
                return;
            var safe = DicomHelpers.SanitizeFileName(patientName);
            if (string.IsNullOrWhiteSpace(safe)) return;
            var parent = Directory.GetParent(studyDir)?.FullName;
            if (string.IsNullOrEmpty(parent)) return;
            // Keep modality + date + hash suffix, swap only the patient prefix (before first '_').
            var dirName = Path.GetFileName(studyDir);
            var cut = dirName.IndexOf('_');
            var newDirName = cut > 0 ? safe + dirName.Substring(cut) : safe + "_" + dirName;
            var newDir = Path.Combine(parent, newDirName);
            if (string.Equals(newDir, studyDir, StringComparison.OrdinalIgnoreCase) || Directory.Exists(newDir))
                return;
            Directory.Move(studyDir, newDir);
            var images = _db.DicomImages.Include(i => i.Series).Where(i => i.Series.StudyId == studyId).ToList();
            foreach (var img in images)
            {
                if (!string.IsNullOrEmpty(img.FilePath) && img.FilePath.StartsWith(studyDir, StringComparison.OrdinalIgnoreCase))
                    img.FilePath = newDir + img.FilePath.Substring(studyDir.Length);
                if (!string.IsNullOrEmpty(img.PngPath) && img.PngPath.StartsWith(studyDir, StringComparison.OrdinalIgnoreCase))
                    img.PngPath = newDir + img.PngPath.Substring(studyDir.Length);
            }
            _db.SaveChanges();
            var infoPath = Path.Combine(newDir, "study-info.json");
            if (File.Exists(infoPath))
            {
                try
                {
                    var info = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, object>>(File.ReadAllText(infoPath));
                    if (info != null && info.ContainsKey("PatientName"))
                    {
                        info["PatientName"] = patientName;
                        File.WriteAllText(infoPath, System.Text.Json.JsonSerializer.Serialize(info));
                    }
                }
                catch (Exception ex) { _logger.LogDebug(ex, "study-info.json patient refresh skipped (non-fatal)"); }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Study dir rename failed for study {StudyId} (DB rename kept)", studyId);
        }
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
        var pngDirs = new HashSet<string>();
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
            if (!string.IsNullOrEmpty(img.PngPath))
            {
                var pngDir = Path.GetDirectoryName(img.PngPath);
                if (pngDir != null) pngDirs.Add(pngDir);
            }
            _db.DicomFrames.RemoveRange(img.Frames);
        }
        _db.DicomImages.RemoveRange(images);

        var series = await _db.Series.Where(s => s.StudyId == studyId).ToListAsync();
        _db.Series.RemoveRange(series);

        var study = await _db.Studies.FindAsync(studyId);
        var patientId = study?.PatientId;
        var resumeToDelete = study?.ResumePdfPath;
        if (study != null) _db.Studies.Remove(study);

        await _db.SaveChangesAsync();

        var dataDir = Environment.GetEnvironmentVariable("FOCUSMED_DATA")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusMed");
        var archiveRoot = Path.Combine(dataDir, "archive");
        var imagesRoot = Path.Combine(dataDir, "images");
        foreach (var dir in archiveDirs)
        {
            try { if (Directory.Exists(dir) && DicomHelpers.IsSubdirectoryOf(dir, archiveRoot)) Directory.Delete(dir, recursive: true); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete archive dir {Dir}", dir); }
        }
        foreach (var dir in pngDirs)
        {
            try { if (Directory.Exists(dir) && DicomHelpers.IsSubdirectoryOf(dir, imagesRoot)) Directory.Delete(dir, recursive: true); }
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

    /// <summary>
    /// Manual Termine: the ONLY exit to Complete now that auto-complete is removed.
    /// Allowed from any status, but refused (false) when the study is missing or has
    /// zero images - there is nothing to terminate. On success sets Complete, bumps
    /// LastUpdatedAt and logs the triage line (moved from the removed auto path).
    /// </summary>
    public async Task<bool> MarkCompleteAsync(int studyId)
    {
        var study = await _db.Studies.FindAsync(studyId);
        if (study == null)
            return false;

        var imageCount = await _db.DicomImages.CountAsync(i => i.Series.StudyId == studyId);
        if (imageCount == 0)
        {
            _logger.LogWarning("Refusing to complete study {StudyId}: no images", studyId);
            return false;
        }

        study.Status = StudyStatus.Complete;
        study.LastUpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var patientName = await _db.Patients
            .Where(p => p.Id == study.PatientId)
            .Select(p => p.PatientName)
            .FirstOrDefaultAsync();
        _logger.LogInformation("Study complete: {PatientName} | {StudyDate} | {StudyUid} ({ImageCount} images)",
            patientName ?? "Unknown",
            study.StudyDate?.ToString("yyyy-MM-dd") ?? "N/A",
            study.StudyInstanceUid,
            imageCount);
        return true;
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
                        try { Directory.Delete(seriesDir, true); } catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete empty series dir {Dir}", seriesDir); }
                    }
                }
                if (Directory.Exists(studyDir) && !Directory.EnumerateFileSystemEntries(studyDir).Any())
                {
                    try { Directory.Delete(studyDir, true); } catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete empty study dir {Dir}", studyDir); }
                }
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to cleanup image directories"); }
    }
}
