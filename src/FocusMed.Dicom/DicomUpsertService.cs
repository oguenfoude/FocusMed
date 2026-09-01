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

public class DicomUpsertService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DicomUpsertService> _logger;
    private readonly IStorageForwardQueue _forwardQueue;
    private readonly IStudyNotificationService _notificationService;
    private readonly int _printMergeWindowSeconds;
    private readonly string _archivePath;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _studyLocks = new();
    private static readonly ConcurrentDictionary<string, int> _studyLockCounts = new();

    // Gate makes acquire (GetOrAdd + increment) atomic with release-check (decrement + remove).
    // Without it, a thread re-acquiring between decrement and TryRemove gets its fresh entries
    // deleted by the retiring thread, letting two threads run the "serialized" section at once.
    private static readonly object _lockGate = new();

    private static SemaphoreSlim AcquireStudyLockRef(string studyUid)
    {
        lock (_lockGate)
        {
            var semaphore = _studyLocks.GetOrAdd(studyUid, _ => new SemaphoreSlim(1, 1));
            _studyLockCounts.AddOrUpdate(studyUid, 1, (_, c) => c + 1);
            return semaphore;
        }
    }

    private static void ReleaseStudyLockRef(string studyUid, SemaphoreSlim semaphore)
    {
        lock (_lockGate)
        {
            var remaining = _studyLockCounts.AddOrUpdate(studyUid, 0, (_, c) => c - 1);
            semaphore.Release();
            if (remaining <= 0)
            {
                _studyLockCounts.TryRemove(studyUid, out _);
                _studyLocks.TryRemove(studyUid, out _);
            }
        }
    }
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

public DicomUpsertService(
        IServiceScopeFactory scopeFactory,
        ILogger<DicomUpsertService> logger,
        IStorageForwardQueue forwardQueue,
        IStudyNotificationService notificationService,
        IOptions<DicomNetworkingOptions> networkingOptions)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _forwardQueue = forwardQueue;
        _notificationService = notificationService;
        _printMergeWindowSeconds = networkingOptions.Value.PrintMergeWindowSeconds;
        var dataDir = Environment.GetEnvironmentVariable("FOCUSMED_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusMed");
        _archivePath = Path.Combine(dataDir, "archive");
        Directory.CreateDirectory(_archivePath);
    }


public async Task StoreFileOnlyAsync(DicomFile dicomFile, string? callingAeTitle = null)
    {
        var dataset = dicomFile.Dataset;
        var patientId = dataset.GetSingleValueOrDefault(DicomTag.PatientID, string.Empty);
        if (string.IsNullOrWhiteSpace(patientId))
        {
            patientId = "";
            dataset.AddOrUpdate(DicomTag.PatientID, patientId);
        }

var studyUid = dataset.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, string.Empty);
        if (string.IsNullOrWhiteSpace(studyUid))
            studyUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
        studyUid = TruncateUid(studyUid);
        dataset.AddOrUpdate(DicomTag.StudyInstanceUID, studyUid);

        var seriesUid = dataset.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, string.Empty);
        if (string.IsNullOrWhiteSpace(seriesUid))
            seriesUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
        seriesUid = TruncateUid(seriesUid);
        dataset.AddOrUpdate(DicomTag.SeriesInstanceUID, seriesUid);

        var sopUid = dataset.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, string.Empty);
        if (string.IsNullOrWhiteSpace(sopUid))
            sopUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
        sopUid = TruncateUid(sopUid);
        dataset.AddOrUpdate(DicomTag.SOPInstanceUID, sopUid);

        // Resolve the final study UID up front so the per-study lock is keyed on the true
        // target: either the same UID (existing dedup) or the patient's existing active study
        // (CT/OT/SC consolidation). Without this, a lock taken on the incoming UID would not
        // serialize concurrent inserts into the consolidated target study.
        using (var resolveScope = _scopeFactory.CreateScope())
        {
            var resolveDb = resolveScope.ServiceProvider.GetRequiredService<FocusMedDbContext>();
            var existingPatient = resolveDb.Patients.FirstOrDefault(p => p.PatientId == patientId);
            studyUid = await ResolveStoreTargetUidAsync(resolveDb, studyUid, existingPatient?.Id ?? 0);
        }
        dataset.AddOrUpdate(DicomTag.StudyInstanceUID, studyUid);

        var studyLock = AcquireStudyLockRef(studyUid);
        await studyLock.WaitAsync();
        string filePath = "";
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FocusMedDbContext>();

            var patientName = dataset.GetSingleValueOrDefault(DicomTag.PatientName, "");
            var patientBirthDate = dataset.GetSingleValueOrDefault(DicomTag.PatientBirthDate, "");
            var patientSex = dataset.GetSingleValueOrDefault(DicomTag.PatientSex, "");
            var studyDate = DicomHelpers.GetDicomDate(dataset, DicomTag.StudyDate);
            var modality = dataset.GetSingleValueOrDefault(DicomTag.Modality, "OT");
            var accessionNumber = dataset.GetSingleValueOrDefault(DicomTag.AccessionNumber, string.Empty);
            var studyDescription = dataset.GetSingleValueOrDefault(DicomTag.StudyDescription, string.Empty);
            var institutionName = dataset.GetSingleValueOrDefault(DicomTag.InstitutionName, string.Empty);
            var manufacturer = dataset.GetSingleValueOrDefault(DicomTag.Manufacturer, string.Empty);
            var referringPhysician = dataset.GetSingleValueOrDefault(DicomTag.ReferringPhysicianName, string.Empty);

            var patient = db.Patients.FirstOrDefault(p => p.PatientId == patientId);
            if (patient == null)
            {
                patient = new Patient
                {
                    PatientId = patientId,
                    PatientName = patientName,
                    BirthDate = string.IsNullOrWhiteSpace(patientBirthDate) ? null : patientBirthDate,
                    Sex = string.IsNullOrWhiteSpace(patientSex) ? null : patientSex
                };
                db.Patients.Add(patient);
            }
            else
            {
                patient.PatientName = patientName;
                if (!string.IsNullOrWhiteSpace(patientBirthDate)) patient.BirthDate = patientBirthDate;
                if (!string.IsNullOrWhiteSpace(patientSex)) patient.Sex = patientSex;
            }

            var study = db.Studies.FirstOrDefault(s => s.StudyInstanceUid == studyUid && s.Status == StudyStatus.Receiving && s.LastUpdatedAt >= DateTime.UtcNow.AddSeconds(-15));

            // CT/OT/SC consolidation: if the same-UID lookup missed (target is Complete, or UID
            // differed before resolution), reuse the patient's most recent active study instead
            // of creating a parallel one. This guarantees two incomplete CTs, or a CT + OT for
            // the same patient, land in ONE study.
            if (study == null && patient.Id != 0)
            {
                study = await db.Studies
                    .Include(s => s.Patient)
                    .Where(s => s.PatientId == patient.Id
                        && (s.Status == StudyStatus.Receiving || s.Status == StudyStatus.Complete))
                    .OrderByDescending(s => s.LastUpdatedAt)
                    .FirstOrDefaultAsync();
                if (study != null)
                {
                    studyUid = study.StudyInstanceUid;
                    dataset.AddOrUpdate(DicomTag.StudyInstanceUID, studyUid);
                    _logger.LogInformation("C-STORE consolidated into existing patient study {TargetId} (uid={Uid}) AE={Ae}",
                        study.Id, studyUid, callingAeTitle ?? "(null)");
                }
            }

            if (study == null)
            {
                var activeUid = studyUid;
                if (db.Studies.Any(s => s.StudyInstanceUid == activeUid))
                {
                    activeUid = $"{studyUid}.{Guid.NewGuid():N}";
                }

study = new Study
                {
                    Patient = patient,
                    StudyInstanceUid = activeUid,
                    StudyDate = studyDate,
                    Description = string.IsNullOrWhiteSpace(studyDescription) ? null : studyDescription,
                    AccessionNumber = string.IsNullOrWhiteSpace(accessionNumber) ? null : accessionNumber,
                    InstitutionName = string.IsNullOrWhiteSpace(institutionName) ? null : institutionName,
                    Manufacturer = string.IsNullOrWhiteSpace(manufacturer) ? null : manufacturer,
                    ReferringPhysicianName = string.IsNullOrWhiteSpace(referringPhysician) ? null : referringPhysician,
                    CallingAeTitle = string.IsNullOrWhiteSpace(callingAeTitle) ? null : callingAeTitle,
                    Status = StudyStatus.Receiving
                };
                db.Studies.Add(study);
                db.SaveChanges();

                // CT arrived — immediately absorb any recent print studies within the merge window.
                await MergeRecentAnonymousStudiesAsync(db, study);
            }
else
            {
                study.Patient = patient;
                study.LastUpdatedAt = DateTime.UtcNow;
                if (string.IsNullOrWhiteSpace(study.CallingAeTitle))
                    study.CallingAeTitle = string.IsNullOrWhiteSpace(callingAeTitle) ? null : callingAeTitle;
                if (!string.IsNullOrWhiteSpace(studyDescription)) study.Description = studyDescription;
                if (!string.IsNullOrWhiteSpace(accessionNumber)) study.AccessionNumber = accessionNumber;
                if (!string.IsNullOrWhiteSpace(institutionName)) study.InstitutionName = institutionName;
                if (!string.IsNullOrWhiteSpace(manufacturer)) study.Manufacturer = manufacturer;
                if (!string.IsNullOrWhiteSpace(referringPhysician)) study.ReferringPhysicianName = referringPhysician;
            }

            var series = db.Series.FirstOrDefault(s => s.SeriesInstanceUid == seriesUid && s.StudyId == study.Id);
            if (series == null)
            {
                series = new Series { Study = study, SeriesInstanceUid = seriesUid, Modality = modality };
                db.Series.Add(series);
            }

            var existingImage = db.DicomImages.Include(d => d.Series).FirstOrDefault(d => d.SopInstanceUid == sopUid);
            if (existingImage != null)
            {
                if (existingImage.Series?.StudyId == study.Id)
                {
                    return;
                }
                sopUid = $"{sopUid}.{Guid.NewGuid():N}";
            }


            var studyHash = DicomHelpers.GetFnv1aHash(studyUid);
            var safePatientName = DicomHelpers.SanitizeFileName(patientName);
            var safeModality = DicomHelpers.SanitizeFileName(modality);
            var datePart = studyDate?.ToString("yyyyMMdd") ?? "nodate";
            var studyDirName = $"{safePatientName}_{safeModality}_{datePart}_{studyHash}";
            // Reuse the existing study's folder when consolidating (CT/OT/SC into the same
            // study) so a merged modality does NOT create a parallel folder.
            var existingStudyImagePath = await db.DicomImages
                .Where(i => i.Series.StudyId == study.Id)
                .Select(i => i.FilePath)
                .FirstOrDefaultAsync();
            string studyDir;
            if (!string.IsNullOrWhiteSpace(existingStudyImagePath))
            {
                var existingSeriesDir = Path.GetDirectoryName(existingStudyImagePath);
                studyDir = string.IsNullOrWhiteSpace(existingSeriesDir)
                    ? Path.Combine(_archivePath, studyDirName)
                    : (Path.GetDirectoryName(existingSeriesDir) ?? Path.Combine(_archivePath, studyDirName));
            }
            else
            {
                studyDir = Path.Combine(_archivePath, studyDirName);
            }
            Directory.CreateDirectory(studyDir);

            var infoPath = Path.Combine(studyDir, "study-info.json");
            if (!File.Exists(infoPath))
            {
                var info = new
                {
                    PatientId = patientId,
                    PatientName = patientName,
                    StudyInstanceUid = studyUid,
                    StudyDate = studyDate?.ToString("yyyyMMdd") ?? "",
                    StudyDescription = studyDescription,
                    Modality = modality,
                    AccessionNumber = accessionNumber,
                    Source = "C-STORE",
                    ReceivedAt = DateTime.UtcNow
                };
                File.WriteAllText(infoPath, System.Text.Json.JsonSerializer.Serialize(info, JsonOptions));
            }

            var seriesDir = Path.Combine(studyDir, seriesUid);
            Directory.CreateDirectory(seriesDir);

            filePath = Path.Combine(seriesDir, $"{sopUid}.dcm");
            await dicomFile.SaveAsync(filePath);

            var dicomImage = new DicomImage
            {
                Series = series,
                SopInstanceUid = sopUid,
                FilePath = filePath,
                SopClassUid = dataset.GetSingleValueOrDefault(DicomTag.SOPClassUID, string.Empty),
                Source = "C-STORE"
            };
            db.DicomImages.Add(dicomImage);

            await db.SaveChangesAsync();
            _notificationService.NotifyStudyChanged();


            try
            {
                _forwardQueue.Enqueue(new StorageForwardRequest(filePath, sopUid, dicomImage.SopClassUid));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Forward queue failed for {SopUid}", sopUid);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Store failed: {SopUid}", sopUid);
            try { if (File.Exists(filePath)) File.Delete(filePath); } catch { }
            throw;
        }
        finally
        {
            ReleaseStudyLockRef(studyUid, studyLock);
        }
    }

    /// <summary>
    /// Resolves the study UID that a C-STORE image should be locked on and written into.
    /// Returns the incoming UID unchanged when no consolidation applies, or the patient's
    /// most recent active (Receiving/Complete) study UID when one exists. This is the
    /// pre-lock twin of the in-lock patient-based lookup so the per-study semaphore is
    /// keyed on the true write target.
    /// </summary>
    private static async Task<string> ResolveStoreTargetUidAsync(FocusMedDbContext db, string studyUid, int patientDbId)
    {
        var sameUid = await db.Studies
            .FirstOrDefaultAsync(s => s.StudyInstanceUid == studyUid
                && s.Status == StudyStatus.Receiving
                && s.LastUpdatedAt >= DateTime.UtcNow.AddSeconds(-15));
        if (sameUid != null)
            return sameUid.StudyInstanceUid;

        if (patientDbId != 0)
        {
            var byPatient = await db.Studies
                .Where(s => s.PatientId == patientDbId
                    && (s.Status == StudyStatus.Receiving || s.Status == StudyStatus.Complete))
                .OrderByDescending(s => s.LastUpdatedAt)
                .FirstOrDefaultAsync();
            if (byPatient != null)
                return byPatient.StudyInstanceUid;
        }

        return studyUid;
    }

    public async Task BackfillMetadataAsync(CancellationToken cancellationToken = default)
    {
        const int batchSize = 200;
        var backfilled = 0;
        var lastId = 0;

        while (true)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FocusMedDbContext>();

            var images = await db.DicomImages
                .Include(i => i.Series)
                .ThenInclude(s => s.Study)
                    .ThenInclude(s => s!.Patient)
                .Where(i => i.Id > lastId && i.Series.Study != null &&
                    (i.Series.Study.Patient!.BirthDate == null ||
                     i.Series.Study.Patient.Sex == null ||
                     i.Series.Study.Description == null ||
                     i.Series.Study.AccessionNumber == null ||
                     i.Series.Study.InstitutionName == null ||
                     i.Series.Study.Manufacturer == null ||
                     i.Series.Study.ReferringPhysicianName == null))
                .OrderBy(i => i.Id)
                .Take(batchSize)
                .AsSplitQuery()
                .ToListAsync(cancellationToken);

            if (images.Count == 0)
                break;

            foreach (var image in images)
            {
                lastId = image.Id;

                if (string.IsNullOrEmpty(image.FilePath) || !File.Exists(image.FilePath))
                    continue;

                try
                {
                    var dicomFile = await DicomFile.OpenAsync(image.FilePath);
                    var ds = dicomFile.Dataset;

                    var patient = image.Series?.Study?.Patient;
                    if (patient != null)
                    {
                        var birthDate = ds.GetSingleValueOrDefault(DicomTag.PatientBirthDate, "");
                        var sex = ds.GetSingleValueOrDefault(DicomTag.PatientSex, "");
                        if (patient.BirthDate == null && !string.IsNullOrWhiteSpace(birthDate))
                            patient.BirthDate = birthDate;
                        if (patient.Sex == null && !string.IsNullOrWhiteSpace(sex))
                            patient.Sex = sex;
                    }

                    var study = image.Series?.Study;
                    if (study != null)
                    {
                        var desc = ds.GetSingleValueOrDefault(DicomTag.StudyDescription, "");
                        var accNum = ds.GetSingleValueOrDefault(DicomTag.AccessionNumber, "");
                        var inst = ds.GetSingleValueOrDefault(DicomTag.InstitutionName, "");
                        var mfr = ds.GetSingleValueOrDefault(DicomTag.Manufacturer, "");
                        var refDoc = ds.GetSingleValueOrDefault(DicomTag.ReferringPhysicianName, "");

                        if (study.Description == null && !string.IsNullOrWhiteSpace(desc))
                            study.Description = desc;
                        if (study.AccessionNumber == null && !string.IsNullOrWhiteSpace(accNum))
                            study.AccessionNumber = accNum;
                        if (study.InstitutionName == null && !string.IsNullOrWhiteSpace(inst))
                            study.InstitutionName = inst;
                        if (study.Manufacturer == null && !string.IsNullOrWhiteSpace(mfr))
                            study.Manufacturer = mfr;
                        if (study.ReferringPhysicianName == null && !string.IsNullOrWhiteSpace(refDoc))
                            study.ReferringPhysicianName = refDoc;
                    }

                    backfilled++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Backfill failed for {FilePath}", image.FilePath);
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (backfilled > 0)
            _logger.LogInformation("Backfilled metadata from {Count} DICOM files", backfilled);
    }

public async Task<DicomFile?> IngestPrintImageAsync(DicomDataset imageDataset, string patientId, string patientName, string? callingAeTitle = null)
    {
        var sopUid = TruncateUid(DicomUIDGenerator.GenerateDerivedFromUUID().UID);
        var studyUid = TruncateUid(DicomUIDGenerator.GenerateDerivedFromUUID().UID);
        var seriesUid = TruncateUid(DicomUIDGenerator.GenerateDerivedFromUUID().UID);

        var newDataset = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian)
        {
            { DicomTag.SOPClassUID, DicomUID.SecondaryCaptureImageStorage.UID },
            { DicomTag.SOPInstanceUID, sopUid },
            { DicomTag.StudyInstanceUID, studyUid },
            { DicomTag.SeriesInstanceUID, seriesUid },
            { DicomTag.PatientID, patientId },
            { DicomTag.PatientName, patientName },
            { DicomTag.StudyDate, DateTime.UtcNow.ToString("yyyyMMdd") },
            { DicomTag.Modality, "SC" },
        };

        if (imageDataset.TryGetSingleValue(DicomTag.SamplesPerPixel, out ushort spp))
            newDataset.Add(DicomTag.SamplesPerPixel, spp);
        if (imageDataset.TryGetSingleValue(DicomTag.PhotometricInterpretation, out string? photo) && photo != null)
            newDataset.Add(DicomTag.PhotometricInterpretation, photo);
        if (imageDataset.TryGetSingleValue(DicomTag.PlanarConfiguration, out ushort pc))
            newDataset.Add(DicomTag.PlanarConfiguration, pc);
        if (imageDataset.TryGetSingleValue(DicomTag.Rows, out ushort rows))
            newDataset.Add(DicomTag.Rows, rows);
        if (imageDataset.TryGetSingleValue(DicomTag.Columns, out ushort cols))
            newDataset.Add(DicomTag.Columns, cols);
        if (imageDataset.TryGetSingleValue(DicomTag.BitsAllocated, out ushort ba))
            newDataset.Add(DicomTag.BitsAllocated, ba);
        if (imageDataset.TryGetSingleValue(DicomTag.BitsStored, out ushort bs))
            newDataset.Add(DicomTag.BitsStored, bs);
        if (imageDataset.TryGetSingleValue(DicomTag.HighBit, out ushort hb))
            newDataset.Add(DicomTag.HighBit, hb);
        if (imageDataset.TryGetSingleValue(DicomTag.PixelRepresentation, out ushort pr))
            newDataset.Add(DicomTag.PixelRepresentation, pr);

        var pixelDataItem = imageDataset.GetDicomItem<DicomItem>(DicomTag.PixelData);
        if (pixelDataItem != null)
            newDataset.Add(pixelDataItem);

        // Merge target resolution must happen against a stable target UID, so resolve it
        // (and the study lock) up front instead of generating a fresh UID then discarding it.
        (Study? target, string? targetStudyUid) = await ResolvePrintMergeTargetAsync(patientId, callingAeTitle, imageDataset);
        if (target != null)
        {
            studyUid = TruncateUid(target.StudyInstanceUid);
            newDataset.AddOrUpdate(DicomTag.StudyInstanceUID, studyUid);
            _logger.LogInformation("Print merge resolved: source patient='{PatientId}' ae='{Ae}' -> target study {TargetId} (uid={TargetUid})",
                patientId, callingAeTitle ?? "(null)", target.Id, studyUid);
        }
        else
        {
            _logger.LogInformation("Print merge: no match found for patient='{PatientId}' ae='{Ae}', creating new study", patientId, callingAeTitle ?? "(null)");
        }

        var dicomFile = new DicomFile(newDataset);

        var studyLock = AcquireStudyLockRef(studyUid);
        await studyLock.WaitAsync();
        string? savedFilePath = null;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FocusMedDbContext>();

            var patient = db.Patients.FirstOrDefault(p => p.PatientId == patientId);
            if (patient == null)
            {
                patient = new Patient { PatientId = patientId, PatientName = patientName };
                db.Patients.Add(patient);
            }

            Study study;
            if (target != null)
            {
                study = await db.Studies.Include(s => s.Patient).FirstOrDefaultAsync(s => s.Id == target.Id) ?? target;
                study.LastUpdatedAt = DateTime.UtcNow;
                if (study.Status != StudyStatus.Receiving && study.Status != StudyStatus.Complete)
                    study.Status = StudyStatus.Receiving;
            }
            else
            {
                study = new Study
                {
                    Patient = patient,
                    StudyInstanceUid = studyUid,
                    StudyDate = DateTime.UtcNow,
                    CallingAeTitle = string.IsNullOrWhiteSpace(callingAeTitle) ? null : callingAeTitle,
                    Status = StudyStatus.Receiving
                };
                db.Studies.Add(study);
            }

            var series = new Series { Study = study, SeriesInstanceUid = seriesUid, Modality = "SC" };
            db.Series.Add(series);

            // Prefer the existing archive directory of the merged study so a print lands
            // inside the C-STORE study's human-readable folder, not a parallel _SC_ folder.
            string studyDir;
            var existingImagePath = await db.DicomImages
                .Where(i => i.Series.StudyId == study.Id)
                .Select(i => i.FilePath)
                .FirstOrDefaultAsync();
            if (string.IsNullOrWhiteSpace(existingImagePath))
            {
                var studyHash = DicomHelpers.GetFnv1aHash(studyUid);
                var safePatientName = DicomHelpers.SanitizeFileName(patientName);
                var datePart = DateTime.UtcNow.ToString("yyyyMMdd");
                var studyDirName = $"{safePatientName}_SC_{datePart}_{studyHash}";
                studyDir = Path.Combine(_archivePath, studyDirName);
            }
            else
            {
                var seriesDirOfExisting = Path.GetDirectoryName(existingImagePath);
                studyDir = string.IsNullOrWhiteSpace(seriesDirOfExisting)
                    ? Path.Combine(_archivePath, DicomHelpers.SanitizeFileName(patientName) + "_SC_" + DateTime.UtcNow.ToString("yyyyMMdd"))
                    : (Path.GetDirectoryName(seriesDirOfExisting) ?? Path.Combine(_archivePath, DicomHelpers.SanitizeFileName(patientName) + "_SC_" + DateTime.UtcNow.ToString("yyyyMMdd")));
            }
            Directory.CreateDirectory(studyDir);

            var infoPath = Path.Combine(studyDir, "study-info.json");
            if (!File.Exists(infoPath))
            {
                var info = new
                {
                    PatientId = patientId,
                    PatientName = patientName,
                    StudyInstanceUid = studyUid,
                    StudyDate = DateTime.UtcNow.ToString("yyyyMMdd"),
                    Modality = "SC",
                    Source = "PRINT",
                    ReceivedAt = DateTime.UtcNow
                };
                File.WriteAllText(infoPath, System.Text.Json.JsonSerializer.Serialize(info, JsonOptions));
            }

            var seriesDir = Path.Combine(studyDir, seriesUid);
            Directory.CreateDirectory(seriesDir);

            var filePath = Path.Combine(seriesDir, $"{sopUid}.dcm");
            await dicomFile.SaveAsync(filePath);
            savedFilePath = filePath;

            var dicomImage = new DicomImage
            {
                Series = series,
                SopInstanceUid = sopUid,
                FilePath = filePath,
                SopClassUid = DicomUID.SecondaryCaptureImageStorage.UID,
                Source = "PRINT"
            };
            db.DicomImages.Add(dicomImage);

            await db.SaveChangesAsync();
            _notificationService.NotifyStudyChanged();
            _logger.LogInformation("Print image ingested: {PatientName} | SOP={SopUid} | Study={StudyUid}{Merge}",
                patientName, sopUid, studyUid, target != null ? " (merged)" : "");

            return dicomFile;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Print image ingest failed");
            if (savedFilePath != null)
            {
                try { if (File.Exists(savedFilePath)) File.Delete(savedFilePath); }
                catch { }
            }
            return null;
        }
        finally
        {
            ReleaseStudyLockRef(studyUid, studyLock);
        }
    }

    private async Task<(Study? study, string? studyUid)> ResolvePrintMergeTargetAsync(
        string patientId, string? callingAeTitle, DicomDataset imageDataset)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FocusMedDbContext>();
        var windowStart = DateTime.UtcNow.AddSeconds(-_printMergeWindowSeconds);

        // 1) Explicit source StudyInstanceUID inside the image dataset (e.g. a print of an
        //    existing CT/OT study). Most reliable link — merge into that study directly.
        var sourceStudyUid = imageDataset.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, string.Empty);
        if (!string.IsNullOrWhiteSpace(sourceStudyUid))
        {
            var byUid = await db.Studies.FirstOrDefaultAsync(s => s.StudyInstanceUid == sourceStudyUid);
            if (byUid != null) return (byUid, byUid.StudyInstanceUid);
        }

        // 2) Same patient, Receiving or Complete. No time window — CT/OT/SC for the same
        //    patient always consolidate into ONE active study, regardless of timing.
        if (!string.IsNullOrWhiteSpace(patientId))
        {
            var patient = db.Patients.FirstOrDefault(p => p.PatientId == patientId);
            if (patient != null)
            {
                var byPatient = await db.Studies
                    .Where(s => s.PatientId == patient.Id
                        && (s.Status == StudyStatus.Receiving || s.Status == StudyStatus.Complete))
                    .OrderByDescending(s => s.LastUpdatedAt)
                    .FirstOrDefaultAsync();
                if (byPatient != null) return (byPatient, byPatient.StudyInstanceUid);
            }
        }

        // 3) Anonymous print: same calling AE within the window. Pairs a film with the
        //    CT/OT study that just arrived from the same device.
        if (string.IsNullOrWhiteSpace(patientId) && !string.IsNullOrWhiteSpace(callingAeTitle))
        {
            var byAe = await db.Studies
                .Where(s => s.CallingAeTitle == callingAeTitle
                    && (s.Status == StudyStatus.Receiving || s.Status == StudyStatus.Complete)
                    && s.LastUpdatedAt >= windowStart)
                .OrderByDescending(s => s.LastUpdatedAt)
                .FirstOrDefaultAsync();
            if (byAe != null) return (byAe, byAe.StudyInstanceUid);
        }

        // 4) Last resort: ANY active study within the window. The print and CT may
        //    have different CallingAeTitles, UIDs, or patient IDs but still belong
        //    to the same session. The 300s window is narrow enough to avoid
        //    merging unrelated studies. Never merge into Archived/Deleted —
        //    those are not active data.
        {
            var anyRecent = await db.Studies
                .Where(s => s.Status == StudyStatus.Receiving || s.Status == StudyStatus.Complete)
                .Where(s => s.LastUpdatedAt >= windowStart)
                .OrderByDescending(s => s.LastUpdatedAt)
                .FirstOrDefaultAsync();
            if (anyRecent != null) return (anyRecent, anyRecent.StudyInstanceUid);
        }

        return (null, null);
    }

    private static string TruncateUid(string uid)
    {
        var sanitized = new string(uid.Where(c => char.IsDigit(c) || c == '.').ToArray());
        if (sanitized.Length == 0) sanitized = "0";
        if (sanitized.Length > 64) sanitized = sanitized[..64];
        return sanitized;
    }

    /// <summary>
    /// When a CT/real study is created via C-STORE, find any recent print studies for the same
    /// patient (named or anonymous, within the merge window) and re-point their series into this
    /// study. Called from StoreFileOnlyAsync immediately after creating a new study.
    /// </summary>
    private async Task MergeRecentAnonymousStudiesAsync(FocusMedDbContext db, Study targetStudy)
    {
        var windowStart = DateTime.UtcNow.AddSeconds(-_printMergeWindowSeconds);

        var toMerge = await db.Studies
            .Include(s => s.Patient)
            .Include(s => s.Series).ThenInclude(s => s.Images)
            .AsSplitQuery()
            .Where(s => s.Id != targetStudy.Id
                && s.Status == StudyStatus.Receiving
                && s.LastUpdatedAt >= windowStart
                && s.Patient != null
                && (s.Patient.PatientId == ""
                    || s.Patient.PatientId == targetStudy.Patient.PatientId))
            .ToListAsync();

        foreach (var printStudy in toMerge)
        {
            foreach (var series in printStudy.Series)
                series.StudyId = targetStudy.Id;

            var printJobs = await db.PrintJobs.Where(p => p.StudyId == printStudy.Id).ToListAsync();
            foreach (var pj in printJobs)
            {
                pj.StudyId = targetStudy.Id;
                pj.PatientId = targetStudy.PatientId;
            }

            var printImage = printStudy.Series.SelectMany(s => s.Images).FirstOrDefault();
            var targetImage = targetStudy.Series.SelectMany(s => s.Images).FirstOrDefault();
            if (printImage != null && targetImage != null)
            {
                var printStudyDir = Directory.GetParent(Path.GetDirectoryName(printImage.FilePath) ?? "")?.FullName;
                var targetStudyDir = Directory.GetParent(Path.GetDirectoryName(targetImage.FilePath) ?? "")?.FullName;
                if (!string.IsNullOrEmpty(printStudyDir) && !string.IsNullOrEmpty(targetStudyDir)
                    && Directory.Exists(printStudyDir) && Directory.Exists(targetStudyDir))
                {
                    try
                    {
                        var dirName = Path.GetFileName(printStudyDir);
                        var newDir = Path.Combine(targetStudyDir, dirName);
                        if (Directory.Exists(newDir))
                            newDir = Path.Combine(targetStudyDir, dirName + "_merged_" + DateTime.UtcNow.ToString("HHmmss"));
                        Directory.Move(printStudyDir, newDir);
                        foreach (var img in printStudy.Series.SelectMany(s => s.Images))
                        {
                            if (!string.IsNullOrEmpty(img.FilePath) && img.FilePath.StartsWith(printStudyDir, StringComparison.OrdinalIgnoreCase))
                                img.FilePath = newDir + img.FilePath.Substring(printStudyDir.Length);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to move print archive dir into new CT study {TargetId}", targetStudy.Id);
                    }
                }
            }

            var printPatientId = printStudy.PatientId;
            db.Studies.Remove(printStudy);

            // Clean up the now-orphaned Patient row (e.g. anonymous PatientId="")
            // if no other study still references it — prevents phantom patient records.
            if (printPatientId != targetStudy.PatientId && printPatientId != 0)
            {
                var stillUsed = await db.Studies.AnyAsync(s => s.PatientId == printPatientId && s.Id != printStudy.Id);
                if (!stillUsed)
                {
                    var orphanPatient = await db.Patients.FindAsync([printPatientId]);
                    if (orphanPatient != null)
                        db.Patients.Remove(orphanPatient);
                }
            }

            _logger.LogInformation("C-STORE absorbed print study {PrintStudyId} (patient='{Patient}') into new CT study {TargetId}",
                printStudy.Id, printPatientId, targetStudy.Id);
        }

        if (toMerge.Count > 0)
            await db.SaveChangesAsync();
    }
}
