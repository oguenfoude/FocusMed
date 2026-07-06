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
    private readonly string _imagesPath;

    public DicomUpsertService(IServiceScopeFactory scopeFactory, ILogger<DicomUpsertService> logger, IStorageForwardQueue forwardQueue, IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _forwardQueue = forwardQueue;
        _archivePath = Environment.ExpandEnvironmentVariables(configuration.GetValue<string>("ArchivePath") ?? "%FOCUSMED_DATA%/archive");
        _imagesPath = Environment.ExpandEnvironmentVariables(configuration.GetValue<string>("ImagesPath") ?? "%FOCUSMED_DATA%/images");
        Directory.CreateDirectory(_archivePath);
        Directory.CreateDirectory(_imagesPath);
    }

    public async Task ProcessDicomFileAsync(DicomFile dicomFile)
    {
        var dataset = dicomFile.Dataset;
        var patientId = dataset.GetSingleValueOrDefault(DicomTag.PatientID, string.Empty);
        if (string.IsNullOrWhiteSpace(patientId))
        {
            patientId = $"UNKNOWN_{Guid.NewGuid():N}";
            dataset.AddOrUpdate(DicomTag.PatientID, patientId);
            _logger.LogWarning("DICOM file missing PatientID. Synthesized fallback: {PatientId}", patientId);
        }

        var studyUid = dataset.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, string.Empty);
        if (string.IsNullOrWhiteSpace(studyUid))
        {
            studyUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
            dataset.AddOrUpdate(DicomTag.StudyInstanceUID, studyUid);
            _logger.LogWarning("DICOM file missing StudyInstanceUID. Synthesized fallback: {StudyUid}", studyUid);
        }

        var seriesUid = dataset.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, string.Empty);
        if (string.IsNullOrWhiteSpace(seriesUid))
        {
            seriesUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
            dataset.AddOrUpdate(DicomTag.SeriesInstanceUID, seriesUid);
            _logger.LogWarning("DICOM file missing SeriesInstanceUID. Synthesized fallback: {SeriesUid}", seriesUid);
        }

        var sopUid = dataset.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, string.Empty);
        if (string.IsNullOrWhiteSpace(sopUid))
        {
            sopUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
            dataset.AddOrUpdate(DicomTag.SOPInstanceUID, sopUid);
            _logger.LogWarning("DICOM file missing SOPInstanceUID. Synthesized fallback: {SopUid}", sopUid);
        }

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
                FilePath = filePath,
                SopClassUid = dataset.GetSingleValueOrDefault(DicomTag.SOPClassUID, string.Empty)
            };
            db.DicomImages.Add(dicomImage);

            try
            {
                if (dataset.Contains(DicomTag.PixelData))
                {
                    var imagesDir = Path.Combine(_imagesPath, studyHash, seriesUid);
                    Directory.CreateDirectory(imagesDir);

                    var image = new FellowOakDicom.Imaging.DicomImage(dataset);
                    for (int frameIndex = 0; frameIndex < image.NumberOfFrames; frameIndex++)
                    {
                        var pngPath = Path.Combine(imagesDir, $"{sopUid}_{frameIndex:D4}.png");

                        using var renderedImage = image.RenderImage(frameIndex);
                        using var sharpImage = renderedImage.As<SixLabors.ImageSharp.Image>();
                        await SixLabors.ImageSharp.ImageExtensions.SaveAsPngAsync(sharpImage, pngPath);

                        if (frameIndex == 0)
                        {
                            dicomImage.PngPath = pngPath;
                        }

                        dicomImage.Frames.Add(new DicomFrame
                        {
                            FrameIndex = frameIndex,
                            PngPath = pngPath
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract PNG for SOP {SopUid}. DICOM will still be saved.", sopUid);
            }

            await db.SaveChangesAsync();
            _logger.LogInformation("Successfully ingested image {SopUid} for study {StudyUid}", sopUid, studyUid);

            try
            {
                var sopClassUid = dataset.GetSingleValueOrDefault(DicomTag.SOPClassUID, string.Empty);
                _forwardQueue.Enqueue(new StorageForwardRequest(filePath, sopUid, sopClassUid));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to enqueue storage forward request for {SopUid}. Forward skipped.", sopUid);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing DICOM file with SOP {SopUid}", sopUid);
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
