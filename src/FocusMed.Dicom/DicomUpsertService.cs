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
    private readonly string _archivePath;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _studyLocks = new();

    public DicomUpsertService(IServiceScopeFactory scopeFactory, ILogger<DicomUpsertService> logger, IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _archivePath = Environment.ExpandEnvironmentVariables(configuration.GetValue<string>("ArchivePath") ?? "%FOCUSMED_DATA%/archive");
        Directory.CreateDirectory(_archivePath);
    }

    public async Task ProcessDicomFileAsync(DicomFile dicomFile)
    {
        var dataset = dicomFile.Dataset;
        var studyUid = dataset.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, string.Empty);
        var seriesUid = dataset.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, string.Empty);
        var sopUid = dataset.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, string.Empty);
        var patientId = dataset.GetSingleValueOrDefault(DicomTag.PatientID, "UNKNOWN");

        if (string.IsNullOrEmpty(studyUid) || string.IsNullOrEmpty(seriesUid) || string.IsNullOrEmpty(sopUid))
        {
            _logger.LogError("DICOM file is missing essential UIDs. Study: {StudyUid}, Series: {SeriesUid}, SOP: {SopUid}", studyUid, seriesUid, sopUid);
            return;
        }

        var semaphore = _studyLocks.GetOrAdd(studyUid, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync();

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FocusMedDbContext>();

            if (db.DicomImages.Any(i => i.SopInstanceUid == sopUid))
            {
                _logger.LogInformation("Image {SopUid} already exists. Skipping.", sopUid);
                return;
            }

            var patient = db.Patients.FirstOrDefault(p => p.PatientId == patientId);
            if (patient == null)
            {
                patient = new Patient
                {
                    PatientId = patientId,
                    PatientName = dataset.GetSingleValueOrDefault(DicomTag.PatientName, "UNKNOWN")
                };
                db.Patients.Add(patient);
            }

            var study = db.Studies.FirstOrDefault(s => s.StudyInstanceUid == studyUid);
            if (study == null)
            {
                study = new Study
                {
                    Patient = patient,
                    StudyInstanceUid = studyUid,
                    StudyDate = GetDicomDate(dataset, DicomTag.StudyDate)
                };
                db.Studies.Add(study);
            }
            else
            {
                study.LastUpdatedAt = DateTime.UtcNow;
                study.Status = StudyStatus.Receiving;
            }

            var series = db.Series.FirstOrDefault(s => s.SeriesInstanceUid == seriesUid);
            if (series == null)
            {
                series = new Series
                {
                    Study = study,
                    SeriesInstanceUid = seriesUid,
                    Modality = dataset.GetSingleValueOrDefault(DicomTag.Modality, "OT")
                };
                db.Series.Add(series);
            }

            var studyHash = GetFnv1aHash(studyUid);
            var studyDir = Path.Combine(_archivePath, studyHash);
            Directory.CreateDirectory(studyDir);

            var infoPath = Path.Combine(studyDir, "study-info.json");
            if (!File.Exists(infoPath))
            {
                var info = new
                {
                    PatientId = patientId,
                    PatientName = dataset.GetSingleValueOrDefault(DicomTag.PatientName, "UNKNOWN"),
                    StudyInstanceUid = studyUid,
                    StudyDate = dataset.GetSingleValueOrDefault(DicomTag.StudyDate, string.Empty),
                    Modality = dataset.GetSingleValueOrDefault(DicomTag.Modality, "OT"),
                    AccessionNumber = dataset.GetSingleValueOrDefault(DicomTag.AccessionNumber, string.Empty),
                    ReceivedAt = DateTime.UtcNow
                };
                await File.WriteAllTextAsync(infoPath, System.Text.Json.JsonSerializer.Serialize(info, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }

            var seriesDir = Path.Combine(studyDir, seriesUid);
            Directory.CreateDirectory(seriesDir);

            var filePath = Path.Combine(seriesDir, $"{sopUid}.dcm");
            await dicomFile.SaveAsync(filePath);

            var dicomImage = new DicomImage
            {
                Series = series,
                SopInstanceUid = sopUid,
                FilePath = filePath
            };
            db.DicomImages.Add(dicomImage);

            await db.SaveChangesAsync();
            _logger.LogInformation("Successfully ingested image {SopUid} for study {StudyUid}", sopUid, studyUid);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing DICOM file with SOP {SopUid}", sopUid);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private static string GetFnv1aHash(string input)
    {
        ulong hash = 14695981039346656037;
        foreach (char c in input)
        {
            hash ^= c;
            hash *= 1099511628211;
        }
        return hash.ToString("X16");
    }

    private static DateTime? GetDicomDate(DicomDataset dataset, DicomTag tag)
    {
        var dateString = dataset.GetSingleValueOrDefault(tag, string.Empty);
        if (DateTime.TryParseExact(dateString, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var date))
            return date;
        return null;
    }
}
