using System.Collections.Concurrent;
using FellowOakDicom;
using FocusMed.Data;
using FocusMed.Data.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FocusMed.Dicom;

public class DicomUpsertService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DicomUpsertService> _logger;
    private readonly IStorageForwardQueue _forwardQueue;
    private readonly string _archivePath;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _studyLocks = new();

    public DicomUpsertService(IServiceScopeFactory scopeFactory, ILogger<DicomUpsertService> logger, IStorageForwardQueue forwardQueue, IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _forwardQueue = forwardQueue;
        var dataDir = Environment.ExpandEnvironmentVariables(configuration.GetValue<string>("DataDirectory") ?? "%FOCUSMED_DATA%");
        _archivePath = Path.Combine(dataDir, "archive");
        Directory.CreateDirectory(_archivePath);
    }

    public async Task StoreFileOnlyAsync(DicomFile dicomFile)
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
        {
            studyUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
            dataset.AddOrUpdate(DicomTag.StudyInstanceUID, studyUid);
        }

        var seriesUid = dataset.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, string.Empty);
        if (string.IsNullOrWhiteSpace(seriesUid))
        {
            seriesUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
            dataset.AddOrUpdate(DicomTag.SeriesInstanceUID, seriesUid);
        }

        var sopUid = dataset.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, string.Empty);
        if (string.IsNullOrWhiteSpace(sopUid))
        {
            sopUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
            dataset.AddOrUpdate(DicomTag.SOPInstanceUID, sopUid);
        }

        var studyLock = _studyLocks.GetOrAdd(studyUid, _ => new SemaphoreSlim(1, 1));
        await studyLock.WaitAsync();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FocusMedDbContext>();

            var patientName = dataset.GetSingleValueOrDefault(DicomTag.PatientName, "");
            var studyDate = DicomHelpers.GetDicomDate(dataset, DicomTag.StudyDate);
            var modality = dataset.GetSingleValueOrDefault(DicomTag.Modality, "OT");
            var accessionNumber = dataset.GetSingleValueOrDefault(DicomTag.AccessionNumber, string.Empty);
            var studyDescription = dataset.GetSingleValueOrDefault(DicomTag.StudyDescription, string.Empty);

            var patient = db.Patients.FirstOrDefault(p => p.PatientId == patientId);
            if (patient == null)
            {
                patient = new Patient { PatientId = patientId, PatientName = patientName };
                db.Patients.Add(patient);
            }
            else
            {
                patient.PatientName = patientName;
            }

            var study = db.Studies.FirstOrDefault(s => s.StudyInstanceUid == studyUid);
            if (study == null)
            {
                study = new Study { Patient = patient, StudyInstanceUid = studyUid, StudyDate = studyDate, Status = StudyStatus.Receiving };
                db.Studies.Add(study);
            }
            else
            {
                study.Patient = patient;
                study.LastUpdatedAt = DateTime.UtcNow;
            }

            var series = db.Series.FirstOrDefault(s => s.SeriesInstanceUid == seriesUid && s.StudyId == study.Id);
            if (series == null)
            {
                series = new Series { Study = study, SeriesInstanceUid = seriesUid, Modality = modality };
                db.Series.Add(series);
            }

            var existingImage = db.DicomImages.FirstOrDefault(d => d.SopInstanceUid == sopUid);
            if (existingImage != null)
            {
                return;
            }

            var studyHash = DicomHelpers.GetFnv1aHash(studyUid);
            var safePatientName = DicomHelpers.SanitizeFileName(patientName);
            var safeModality = DicomHelpers.SanitizeFileName(modality);
            var datePart = studyDate?.ToString("yyyyMMdd") ?? "nodate";
            var studyDirName = $"{safePatientName}_{safeModality}_{datePart}_{studyHash}";
            var studyDir = Path.Combine(_archivePath, studyDirName);
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
                File.WriteAllText(infoPath, System.Text.Json.JsonSerializer.Serialize(info, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }

            var seriesDir = Path.Combine(studyDir, seriesUid);
            Directory.CreateDirectory(seriesDir);

            var filePath = Path.Combine(seriesDir, $"{sopUid}.dcm");
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
        }
        finally
        {
            studyLock.Release();
        }
    }

    public async Task<DicomFile?> IngestPrintImageAsync(DicomDataset imageDataset, string patientId, string patientName)
    {
        var sopUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
        var studyUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
        var seriesUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;

        var newDataset = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian)
        {
            { DicomTag.SOPClassUID, DicomUID.SecondaryCaptureImageStorage.UID },
            { DicomTag.SOPInstanceUID, sopUid },
            { DicomTag.StudyInstanceUID, studyUid },
            { DicomTag.SeriesInstanceUID, seriesUid },
            { DicomTag.PatientID, patientId },
            { DicomTag.PatientName, patientName },
            { DicomTag.StudyDate, DateTime.UtcNow.ToString("yyyyMMdd") },
            { DicomTag.Modality, "OT" },
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

        var dicomFile = new DicomFile(newDataset);

        var studyLock = _studyLocks.GetOrAdd(studyUid, _ => new SemaphoreSlim(1, 1));
        await studyLock.WaitAsync();
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

            var study = new Study { Patient = patient, StudyInstanceUid = studyUid, StudyDate = DateTime.UtcNow };
            db.Studies.Add(study);

            var series = new Series { Study = study, SeriesInstanceUid = seriesUid, Modality = "OT" };
            db.Series.Add(series);

            var studyHash = DicomHelpers.GetFnv1aHash(studyUid);
            var safePatientName = DicomHelpers.SanitizeFileName(patientName);
            var datePart = DateTime.UtcNow.ToString("yyyyMMdd");
            var studyDirName = $"{safePatientName}_SC_{datePart}_{studyHash}";
            var studyDir = Path.Combine(_archivePath, studyDirName);
            Directory.CreateDirectory(studyDir);

            var infoPath = Path.Combine(studyDir, "study-info.json");
            var info = new
            {
                PatientId = patientId,
                PatientName = patientName,
                StudyInstanceUid = studyUid,
                StudyDate = datePart,
                Modality = "SC",
                Source = "PRINT",
                ReceivedAt = DateTime.UtcNow
            };
            File.WriteAllText(infoPath, System.Text.Json.JsonSerializer.Serialize(info, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

            var seriesDir = Path.Combine(studyDir, seriesUid);
            Directory.CreateDirectory(seriesDir);

            var filePath = Path.Combine(seriesDir, $"{sopUid}.dcm");
            await dicomFile.SaveAsync(filePath);

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
            _logger.LogInformation("Print image ingested: {PatientName} | SOP={SopUid}", patientName, sopUid);
            return dicomFile;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Print image ingest failed");
            return null;
        }
        finally
        {
            studyLock.Release();
        }
    }
}
