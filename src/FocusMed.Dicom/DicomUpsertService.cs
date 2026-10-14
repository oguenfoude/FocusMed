using System.Collections.Concurrent;
using FellowOakDicom;
using FocusMed.Data;
using FocusMed.Data.Entities;
using FocusMed.Dicom.Options;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FocusMed.Dicom;

/// <summary>Outcome of a single C-STORE file ingest.</summary>
public enum StoreOutcome
{
    /// <summary>New DicomImage row persisted.</summary>
    Stored
}

public class DicomUpsertService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DicomUpsertService> _logger;
    private readonly IStorageForwardQueue _forwardQueue;
    private readonly IStudyNotificationService _notificationService;
    private readonly string _archivePath;
    private readonly int _printMergeWindowSeconds;
    private readonly PngExtractionService? _pngExtractionService;
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
        IOptions<DicomNetworkingOptions> networkingOptions,
        PngExtractionService? pngExtractionService = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _forwardQueue = forwardQueue;
        _notificationService = notificationService;
        _pngExtractionService = pngExtractionService;
        var dataDir = Environment.GetEnvironmentVariable("FOCUSMED_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusMed");
        _archivePath = Path.Combine(dataDir, "archive");
        Directory.CreateDirectory(_archivePath);
        var window = networkingOptions?.Value?.PrintMergeWindowSeconds ?? 300;
        _printMergeWindowSeconds = window > 0 ? window : 300;
    }


    public async Task<StoreOutcome> StoreFileOnlyAsync(DicomFile dicomFile, string? callingAeTitle = null, string? remoteIp = null)
    {
        var dataset = dicomFile.Dataset;
        var patientId = dataset.GetSingleValueOrDefault(DicomTag.PatientID, string.Empty);
        if (string.IsNullOrWhiteSpace(patientId))
        {
            patientId = "";
            dataset.AddOrUpdate(DicomTag.PatientID, patientId);
        }

        // Use the sender's StudyInstanceUID. Empty → mint a fresh one.
        var studyUid = dataset.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, string.Empty);
        if (string.IsNullOrWhiteSpace(studyUid))
            studyUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
        studyUid = TruncateUid(studyUid, out _);
        dataset.AddOrUpdate(DicomTag.StudyInstanceUID, studyUid);

        var seriesUid = dataset.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, string.Empty);
        if (string.IsNullOrWhiteSpace(seriesUid))
            seriesUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
        seriesUid = TruncateUid(seriesUid, out _);
        dataset.AddOrUpdate(DicomTag.SeriesInstanceUID, seriesUid);

        var sopUid = dataset.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, string.Empty);
        if (string.IsNullOrWhiteSpace(sopUid))
            sopUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
        sopUid = TruncateUid(sopUid, out _);
        dataset.AddOrUpdate(DicomTag.SOPInstanceUID, sopUid);

        var studyLock = AcquireStudyLockRef(studyUid);
        await studyLock.WaitAsync();
        string filePath = "";
        string studyDir = "";
        string seriesDir = "";
        string infoPath = "";
        bool createdPatient = false;
        bool createdInfo = false;
        bool createdStudyDir = false;
        bool createdSeriesDir = false;
        bool isNewStudy = false;
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
                createdPatient = true;
            }
            else
            {
                // Never let an empty incoming name clobber a named patient sharing the same
                // PatientId key (SV prints always carry empty demographics).
                if (!string.IsNullOrWhiteSpace(patientName)) patient.PatientName = patientName;
                if (!string.IsNullOrWhiteSpace(patientBirthDate)) patient.BirthDate = patientBirthDate;
                if (!string.IsNullOrWhiteSpace(patientSex)) patient.Sex = patientSex;
            }

            // Group by StudyInstanceUID: same UID → same study while the study is
            // still open (Receiving/Failed) AND the arrival is the same UTC calendar
            // day as the study's CreatedAt. Otherwise mint a fresh study: a re-exam
            // days later is a new exam (never unbounded growth), and a re-send onto
            // a Complete/Archived study never reopens it.
            // Raw SQL bypasses EF Core change tracker + SQLite connection pool
            // visibility issues that cause concurrent C-STOREs to miss each
            // other's committed rows.
            int? existingStudyId = null;
            StudyStatus? existingStudyStatus = null;
            DateTime? existingStudyCreatedAt = null;
            using (var cmd = db.Database.GetDbConnection().CreateCommand())
            {
                // ORDER BY Id DESC: get the NEWEST study with this UID. Without
                // this, LIMIT 1 returns the oldest (Complete) study and every
                // image creates a new study instead of appending.
                cmd.CommandText = "SELECT Id, Status, CreatedAt FROM Studies WHERE StudyInstanceUid = $uid AND Status != $deleted ORDER BY Id DESC LIMIT 1";
                cmd.Parameters.Add(new SqliteParameter("$uid", studyUid));
                cmd.Parameters.Add(new SqliteParameter("$deleted", (int)StudyStatus.Deleted));
                if (db.Database.GetDbConnection().State != System.Data.ConnectionState.Open)
                    await db.Database.OpenConnectionAsync();
                using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    existingStudyId = reader.GetInt32(0);
                    existingStudyStatus = (StudyStatus)reader.GetInt32(1);
                    // SQLite stores UTC TEXT; the raw read comes back Unspecified,
                    // so pin it to UTC before the calendar-day comparison.
                    existingStudyCreatedAt = DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc);
                }
            }

            var isOpenStatus = existingStudyStatus == StudyStatus.Receiving || existingStudyStatus == StudyStatus.Failed;
            var isSameUtcDay = existingStudyCreatedAt.HasValue
                && existingStudyCreatedAt.Value.Date == DateTime.UtcNow.Date;

            Study study;
            if (existingStudyId.HasValue && isOpenStatus && isSameUtcDay)
            {
                // Re-query with tracking so modifications (LastUpdatedAt) are saved.
                study = db.Studies.First(s => s.Id == existingStudyId.Value);
                study.LastUpdatedAt = DateTime.UtcNow;
                if (string.IsNullOrWhiteSpace(study.CallingAeTitle))
                    study.CallingAeTitle = callingAeTitle;
                if (string.IsNullOrWhiteSpace(study.RemoteIp))
                    study.RemoteIp = remoteIp;
                isNewStudy = false;
            }
            else
            {
                if (existingStudyId.HasValue && !isOpenStatus)
                    _logger.LogInformation("C-STORE re-exam fork: study {StudyId} is {Status} (completed/archived), minting fresh study for uid=...{StudyTail}",
                        existingStudyId.Value, existingStudyStatus, studyUid[^Math.Min(8, studyUid.Length)..]);
                else if (existingStudyId.HasValue)
                    _logger.LogInformation("C-STORE re-exam fork: study {StudyId} created {CreatedDay:yyyy-MM-dd} is a different UTC day (re-exam new day), minting fresh study for uid=...{StudyTail}",
                        existingStudyId.Value, existingStudyCreatedAt, studyUid[^Math.Min(8, studyUid.Length)..]);
                study = new Study
                {
                    Patient = patient,
                    StudyInstanceUid = studyUid,
                    StudyDate = studyDate,
                    Description = string.IsNullOrWhiteSpace(studyDescription) ? null : studyDescription,
                    AccessionNumber = string.IsNullOrWhiteSpace(accessionNumber) ? null : accessionNumber,
                    InstitutionName = string.IsNullOrWhiteSpace(institutionName) ? null : institutionName,
                    Manufacturer = string.IsNullOrWhiteSpace(manufacturer) ? null : manufacturer,
                    ReferringPhysicianName = string.IsNullOrWhiteSpace(referringPhysician) ? null : referringPhysician,
                    CallingAeTitle = string.IsNullOrWhiteSpace(callingAeTitle) ? null : callingAeTitle,
                    RemoteIp = string.IsNullOrWhiteSpace(remoteIp) ? null : remoteIp,
                    Status = StudyStatus.Receiving
                };
                db.Studies.Add(study);
                isNewStudy = true;
            }

            // Save the study first to get its real auto-generated Id (needed
            // for the series FK). Detach it afterward so concurrent C-STOREs
            // for the same StudyInstanceUID see the DB row, not this cached
            // entity with temporary Id=0.
            await db.SaveChangesAsync();
            if (isNewStudy)
                _logger.LogInformation("C-STORE study Created Id={StudyId} uid=...{StudyTail} AE={Ae}",
                    study.Id, studyUid[^Math.Min(8, studyUid.Length)..], callingAeTitle ?? "(null)");

            // Series: same UID within the same study → same series.
            var series = db.Series.FirstOrDefault(s => s.SeriesInstanceUid == seriesUid && s.StudyId == study.Id);
            if (series == null)
            {
                series = new Series { Study = study, SeriesInstanceUid = seriesUid, Modality = modality };
                db.Series.Add(series);
            }

            // Same SOP already in THIS study (operator re-sent the same images
            // while the study is open): skip the duplicate row + file, but bump
            // LastUpdatedAt + log so the re-send is visible, not silent.
            // Raw SQL: same SQLite pool visibility reason as the study lookup.
            bool sopAlreadyInStudy;
            using (var sopCmd = db.Database.GetDbConnection().CreateCommand())
            {
                sopCmd.CommandText = "SELECT 1 FROM DicomImages d JOIN Series se ON se.Id = d.SeriesId WHERE d.SopInstanceUid = $sop AND se.StudyId = $studyId LIMIT 1";
                sopCmd.Parameters.Add(new SqliteParameter("$sop", sopUid));
                sopCmd.Parameters.Add(new SqliteParameter("$studyId", study.Id));
                if (db.Database.GetDbConnection().State != System.Data.ConnectionState.Open)
                    await db.Database.OpenConnectionAsync();
                using var sopReader = await sopCmd.ExecuteReaderAsync();
                sopAlreadyInStudy = await sopReader.ReadAsync();
            }
            if (sopAlreadyInStudy)
            {
                study.LastUpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
                _logger.LogInformation("C-STORE duplicate SOP=...{SopTail} already in Study={StudyId} AE={Ae} — skipped, timestamp bumped",
                    sopUid[^Math.Min(8, sopUid.Length)..], study.Id, callingAeTitle ?? "(null)");
                _notificationService.NotifyStudyChanged();
                return StoreOutcome.Stored;
            }

            var studyHash = DicomHelpers.GetFnv1aHash(studyUid);
            var safePatientName = DicomHelpers.SanitizeFileName(patientName);
            var safeModality = DicomHelpers.SanitizeFileName(modality);
            var datePart = studyDate?.ToString("yyyyMMdd") ?? "nodate";
            var studyDirName = $"{safePatientName}_{safeModality}_{datePart}_{studyHash}";
            studyDir = Path.Combine(_archivePath, studyDirName);
            createdStudyDir = !Directory.Exists(studyDir);
            Directory.CreateDirectory(studyDir);

            infoPath = Path.Combine(studyDir, "study-info.json");
            if (!File.Exists(infoPath))
            {
                createdInfo = true;
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

            seriesDir = Path.Combine(studyDir, seriesUid);
            createdSeriesDir = !Directory.Exists(seriesDir);
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
            _logger.LogDebug("C-STORE stored SOP=...{SopTail} Study={StudyId} AE={Ae}",
                sopUid[^Math.Min(8, sopUid.Length)..], study.Id, callingAeTitle ?? "(null)");
            _notificationService.NotifyStudyChanged();


            try
            {
                _forwardQueue.Enqueue(new StorageForwardRequest(filePath, sopUid, dicomImage.SopClassUid));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Forward queue failed for {SopUid}", sopUid);
            }
            return StoreOutcome.Stored;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Store failed: {SopUid}", sopUid);
            try { if (File.Exists(filePath)) File.Delete(filePath); } catch { }
            try
            {
                // Compensate: remove rows/dirs THIS call created so a late failure
                // doesn't leave phantom empty Study/Series/Patient rows + orphan dirs.
                // Only touches what this call created; concurrent attachers may have
                // added siblings since, so every delete re-checks emptiness first.
                using var cscope = _scopeFactory.CreateScope();
                var cdb = cscope.ServiceProvider.GetRequiredService<FocusMedDbContext>();
                var img = await cdb.DicomImages.FirstOrDefaultAsync(i => i.FilePath == filePath);
                int? seriesId = img?.SeriesId;
                if (img != null) cdb.DicomImages.Remove(img);
                if (seriesId.HasValue && !await cdb.DicomImages.AnyAsync(i => i.SeriesId == seriesId.Value))
                {
                    var cseries = await cdb.Series.FindAsync(seriesId.Value);
                    if (cseries != null) cdb.Series.Remove(cseries);
                    if (isNewStudy)
                    {
                        var cstudy = await cdb.Studies.Include(s => s.Series).FirstOrDefaultAsync(s => s.StudyInstanceUid == studyUid);
                        if (cstudy != null && cstudy.Series.Count == 0)
                        {
                            int cpatientId = cstudy.PatientId;
                            cdb.Studies.Remove(cstudy);
                            if (createdPatient && !await cdb.Studies.AnyAsync(s => s.PatientId == cpatientId))
                            {
                                var cpatient = await cdb.Patients.FindAsync(cpatientId);
                                if (cpatient != null) cdb.Patients.Remove(cpatient);
                            }
                        }
                    }
                }
                await cdb.SaveChangesAsync();
            }
            catch (Exception cex) { _logger.LogDebug(cex, "C-STORE compensation failed (best-effort)"); }
            try { if (createdInfo && File.Exists(infoPath)) File.Delete(infoPath); } catch { }
            try { if (createdSeriesDir && Directory.Exists(seriesDir) && !Directory.EnumerateFileSystemEntries(seriesDir).Any()) Directory.Delete(seriesDir); } catch { }
            try { if (createdStudyDir && Directory.Exists(studyDir) && !Directory.EnumerateFileSystemEntries(studyDir).Any()) Directory.Delete(studyDir); } catch { }
            throw;
        }
        finally
        {
            ReleaseStudyLockRef(studyUid, studyLock);
        }
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

    public async Task<DicomFile?> IngestPrintImageAsync(DicomDataset imageDataset, string patientId, string patientName, string? callingAeTitle = null, string? remoteIp = null, int? printJobId = null)
    {
        var sopUid = TruncateUid(DicomUIDGenerator.GenerateDerivedFromUUID().UID, out _);
        var studyUid = TruncateUid(DicomUIDGenerator.GenerateDerivedFromUUID().UID, out _);
        var seriesUid = TruncateUid(DicomUIDGenerator.GenerateDerivedFromUUID().UID, out _);

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

        // Carry over any patient/study metadata the SCU sent (PS3.4 Annex H allows these in the
        // Film Session Proposed Study Sequence / Image Box) instead of discarding them — this is
        // how a SonoVision print can retain PatientID, StudyDate, AccessionNumber etc. in the
        // stored .dcm even when the print carries no pixel-level identity.
        if (imageDataset.TryGetSingleValue(DicomTag.StudyDate, out string? srcStudyDate) && !string.IsNullOrWhiteSpace(srcStudyDate))
            newDataset.AddOrUpdate(DicomTag.StudyDate, srcStudyDate);
        if (imageDataset.TryGetSingleValue(DicomTag.PatientBirthDate, out string? srcBirthDate) && !string.IsNullOrWhiteSpace(srcBirthDate))
            newDataset.Add(DicomTag.PatientBirthDate, srcBirthDate);
        if (imageDataset.TryGetSingleValue(DicomTag.PatientSex, out string? srcSex) && !string.IsNullOrWhiteSpace(srcSex))
            newDataset.Add(DicomTag.PatientSex, srcSex);
        foreach (var (srcTag, dstTag) in new[]
        {
            (DicomTag.StudyDescription, DicomTag.StudyDescription),
            (DicomTag.AccessionNumber, DicomTag.AccessionNumber),
            (DicomTag.InstitutionName, DicomTag.InstitutionName),
            (DicomTag.Manufacturer, DicomTag.Manufacturer),
            (DicomTag.ReferringPhysicianName, DicomTag.ReferringPhysicianName),
        })
        {
            if (imageDataset.TryGetSingleValue(srcTag, out string? v) && !string.IsNullOrWhiteSpace(v))
                newDataset.Add(dstTag, v);
        }

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
        // Grouping is by PrintJob linkage (all N-SETs of one film session land together),
        // then by explicit StudyInstanceUID. No patient/AE heuristics — different exams
        // must never merge, even for the same patient on the same day.
        (Study? target, string? targetStudyUid) = await ResolvePrintMergeTargetAsync(patientId, callingAeTitle, imageDataset, printJobId);
        if (target != null)
        {
            studyUid = TruncateUid(target.StudyInstanceUid, out _);
            newDataset.AddOrUpdate(DicomTag.StudyInstanceUID, studyUid);
            _logger.LogInformation("Print merge resolved: printJob={PrintJobId} patient='{PatientId}' ae='{Ae}' -> target study {TargetId} (uid={TargetUid})",
                printJobId, patientId, callingAeTitle ?? "(null)", target.Id, studyUid);
        }
        else
        {
            _logger.LogInformation("Print merge: no match found for printJob={PrintJobId} patient='{PatientId}' ae='{Ae}', creating new study", printJobId, patientId, callingAeTitle ?? "(null)");
        }

        var dicomFile = new DicomFile(newDataset);

        var studyLock = AcquireStudyLockRef(studyUid);
        await studyLock.WaitAsync();
        string? savedFilePath = null;
        int? preExtractStudyId = null;
        DicomFile? ingestedResult = null;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FocusMedDbContext>();

            var patientBirthDate = imageDataset.GetSingleValueOrDefault(DicomTag.PatientBirthDate, "");
            var patientSex = imageDataset.GetSingleValueOrDefault(DicomTag.PatientSex, "");

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
                // Never let an empty incoming name clobber a named patient sharing the same
                // PatientId key (SV prints always carry empty demographics).
                if (!string.IsNullOrWhiteSpace(patientName)) patient.PatientName = patientName;
                if (!string.IsNullOrWhiteSpace(patientBirthDate)) patient.BirthDate = patientBirthDate;
                if (!string.IsNullOrWhiteSpace(patientSex)) patient.Sex = patientSex;
            }

            Study? study = null;
            if (target != null)
            {
                study = await db.Studies.Include(s => s.Patient).FirstOrDefaultAsync(s => s.Id == target.Id);
                if (study == null)
                {
                    // Target vanished between resolve and lock (absorb/merge/delete
                    // raced us). NEVER re-attach the detached `target` entity: EF would
                    // INSERT it as a duplicate StudyInstanceUid row. Fall through and
                    // mint a genuinely fresh study below.
                    _logger.LogWarning("Print merge target {TargetId} gone mid-ingest, minting fresh study", target.Id);
                    target = null;
                    studyUid = TruncateUid(DicomUIDGenerator.GenerateDerivedFromUUID().UID, out _);
                    newDataset.AddOrUpdate(DicomTag.StudyInstanceUID, studyUid);
                }
            }
            if (study != null)
            {
                study.LastUpdatedAt = DateTime.UtcNow;
                if (string.IsNullOrWhiteSpace(study.CallingAeTitle) && !string.IsNullOrWhiteSpace(callingAeTitle))
                    study.CallingAeTitle = callingAeTitle;
                if (string.IsNullOrWhiteSpace(study.RemoteIp) && !string.IsNullOrWhiteSpace(remoteIp))
                    study.RemoteIp = remoteIp;
                if (study.Status != StudyStatus.Receiving)
                {
                    var wasComplete = study.Status == StudyStatus.Complete;
                    study.Status = StudyStatus.Receiving;
                    // A print landing on an already-completed study reopens it so the
                    // completion loop re-stabilizes (and pre-extracts) with the new image.
                    if (wasComplete)
                        _logger.LogInformation("Print reopened completed study {StudyId} (ae={Ae})", study.Id, callingAeTitle ?? "(null)");
                }
            }
            else
            {
                study = new Study
                {
                    Patient = patient,
                    StudyInstanceUid = studyUid,
                    StudyDate = DateTime.UtcNow,
                    CallingAeTitle = string.IsNullOrWhiteSpace(callingAeTitle) ? null : callingAeTitle,
                    RemoteIp = string.IsNullOrWhiteSpace(remoteIp) ? null : remoteIp,
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
            studyDir = Path.GetFullPath(studyDir);
            if (!DicomHelpers.IsSubdirectoryOf(studyDir, _archivePath))
            {
                // A poisoned/hand-edited FilePath in the DB must not redirect print
                // writes outside archive/. Fall back to a fresh _SC_ dir.
                _logger.LogWarning("Print target dir outside archive root, falling back to fresh dir");
                studyDir = Path.Combine(_archivePath, DicomHelpers.SanitizeFileName(patientName) + "_SC_" + DateTime.UtcNow.ToString("yyyyMMdd"));
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
            _logger.LogInformation("Print image ingested: patient='{PatientId}' name='{PatientName}' birth='{BirthDate}' sex='{Sex}' | SOP={SopUid} | Study={StudyUid} | AE={Ae} | IP={Ip}{Merge}",
                patientId, patientName, patientBirthDate ?? "(none)", patientSex ?? "(none)", sopUid, studyUid, callingAeTitle ?? "(null)", remoteIp ?? "(null)", target != null ? " (merged)" : "");

            try
            {
                _forwardQueue.Enqueue(new StorageForwardRequest(filePath, sopUid, DicomUID.SecondaryCaptureImageStorage.UID));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Forward queue failed for {SopUid}", sopUid);
            }

            // Eager PNG warm-up armed here, FIRED AFTER the study-lock release below:
            // prints are opened/reviewed immediately, so render this study's PNGs in the
            // background instead of on first Dashboard open (sequential render cost
            // ~1s/frame = the 15-30s first-open stall). Fire-and-forget,
            // semaphore-guarded, failures non-fatal. C-STORE stays lazy
            // (completion pre-warm) to keep the receive path CPU-free.
            preExtractStudyId = study.Id;
            ingestedResult = dicomFile;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Print image ingest failed");
            if (savedFilePath != null)
            {
                try { if (File.Exists(savedFilePath)) File.Delete(savedFilePath); }
                catch { }
            }
            ingestedResult = null;
        }
        finally
        {
            ReleaseStudyLockRef(studyUid, studyLock);
        }

        // PNG warm-up fires AFTER the lock release so the receive path never holds
        // the per-study lock while kicking off background extraction work.
        if (preExtractStudyId.HasValue && _pngExtractionService != null)
            _ = _pngExtractionService.PreExtractStudyAsync(preExtractStudyId.Value);

        return ingestedResult;
    }

    private async Task<(Study? study, string? studyUid)> ResolvePrintMergeTargetAsync(
        string patientId, string? callingAeTitle, DicomDataset imageDataset, int? printJobId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FocusMedDbContext>();

        // (a) PrintJob linkage wins: N-SET #2..N of the same film session rejoin the
        // study created by N-SET #1 (linked in the N-SET handler after ingest).
        if (printJobId.HasValue)
        {
            var linkedStudyId = await db.PrintJobs
                .Where(p => p.Id == printJobId.Value && p.StudyId != null)
                .Select(p => p.StudyId!.Value)
                .FirstOrDefaultAsync();
            if (linkedStudyId != 0)
            {
                var linked = await db.Studies.FirstOrDefaultAsync(s => s.Id == linkedStudyId && s.Status != StudyStatus.Deleted && s.Status != StudyStatus.Failed && s.Status != StudyStatus.Archived);
                if (linked != null) return (linked, linked.StudyInstanceUid);
            }
        }

        // (b) Explicit source StudyInstanceUID inside the image dataset (e.g. a print of an
        // existing CT/OT study via OriginalImageSequence/ProposedStudySequence).
        var sourceStudyUid = imageDataset.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, string.Empty);
        if (!string.IsNullOrWhiteSpace(sourceStudyUid))
        {
            var byUid = await db.Studies.FirstOrDefaultAsync(s => s.StudyInstanceUid == sourceStudyUid && s.Status != StudyStatus.Deleted && s.Status != StudyStatus.Failed && s.Status != StudyStatus.Archived);
            if (byUid != null) return (byUid, byUid.StudyInstanceUid);
        }

        // (c) Same-AE recent-exam attach: SonoVision prints carry no identity
        // (always patient=""), so an empty print from a known AE joins the newest
        // recent study from that AE and one exam stays one row. Named candidates win
        // (the print lands in the C-STORE folder); otherwise prints chain into the
        // recent anonymous SC study (covers 10 prints across film sessions).
        // Guards: print itself anonymous + AE known; candidates exclude Deleted /
        // Failed / Archived; proximity = CreatedAt within the merge window; newest first.
        if (string.IsNullOrWhiteSpace(patientId) && !string.IsNullOrWhiteSpace(callingAeTitle))
        {
            var cutoff = DateTime.UtcNow.AddSeconds(-_printMergeWindowSeconds);
            var candidates = await db.Studies
                .Include(s => s.Patient)
                .Include(s => s.Series)
                .AsSplitQuery()
                .Where(s => s.CallingAeTitle == callingAeTitle
                    && s.Status != StudyStatus.Deleted
                    && s.Status != StudyStatus.Failed
                    && s.Status != StudyStatus.Archived
                    && s.CreatedAt >= cutoff)
                .OrderByDescending(s => s.CreatedAt)
                .Take(5)
                .ToListAsync();
            var named = candidates.FirstOrDefault(s => s.Patient != null
                && (!string.IsNullOrWhiteSpace(s.Patient.PatientId) || !string.IsNullOrWhiteSpace(s.Patient.PatientName)));
            if (named != null)
            {
                _logger.LogInformation("Print merge resolved (ae-window): printJob={PrintJobId} ae='{Ae}' -> named study {TargetId} (uid={TargetUid})",
                    printJobId, callingAeTitle, named.Id, named.StudyInstanceUid);
                return (named, named.StudyInstanceUid);
            }
            var anonSc = candidates.FirstOrDefault(s => (s.Patient == null
                    || (string.IsNullOrWhiteSpace(s.Patient.PatientId) && string.IsNullOrWhiteSpace(s.Patient.PatientName)))
                && (s.Series.Count == 0 || s.Series.All(x => x.Modality == "SC")));
            if (anonSc != null)
            {
                _logger.LogInformation("Print merge resolved (ae-window-chain): printJob={PrintJobId} ae='{Ae}' -> anonymous SC study {TargetId} (uid={TargetUid})",
                    printJobId, callingAeTitle, anonSc.Id, anonSc.StudyInstanceUid);
                return (anonSc, anonSc.StudyInstanceUid);
            }
        }

        // (d) No match: the print starts its own new study. Named-to-named heuristics
        // stay removed — different exams of the same patient must never merge.
        // Operators can still join studies explicitly via the Dashboard manual merge.
        return (null, null);
    }

    private static string TruncateUid(string uid, out bool wasInvalid)
    {
        var sanitized = new string(uid.Where(c => char.IsDigit(c) || c == '.').ToArray());
        // Reject (don't fold): empty, over-long, or malformed UIDs. Folding them onto
        // "0" or a shared 64-char prefix would pile different exams into one study.
        wasInvalid = sanitized.Length == 0 || sanitized.Length > 64
            || sanitized[0] == '.' || sanitized[^1] == '.'
            || sanitized.Contains("..");
        if (wasInvalid) return DicomUIDGenerator.GenerateDerivedFromUUID().UID;
        return sanitized;
    }
}
