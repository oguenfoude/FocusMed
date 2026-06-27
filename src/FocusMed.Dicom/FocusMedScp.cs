using System.Text;
using FellowOakDicom;
using FellowOakDicom.Network;
using FocusMed.Data;
using FocusMed.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FocusMed.Dicom;

public class FocusMedScp : DicomService,
    IDicomServiceProvider,
    IDicomCEchoProvider,
    IDicomCStoreProvider,
    IDicomCFindProvider,
    IDicomCMoveProvider,
    IDicomNServiceProvider
{
    private readonly DicomUpsertService _upsertService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _logger;

    public FocusMedScp(
        INetworkStream stream,
        Encoding fallbackEncoding,
        ILogger logger,
        DicomServiceDependencies dependencies,
        DicomUpsertService upsertService,
        IServiceScopeFactory scopeFactory)
        : base(stream, fallbackEncoding, logger, dependencies)
    {
        _upsertService = upsertService;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
    {
        foreach (var pc in association.PresentationContexts)
        {
            var syntax = pc.AbstractSyntax;

            if (syntax == DicomUID.Verification
                || syntax.StorageCategory != DicomStorageCategory.None
                || syntax == DicomUID.PatientRootQueryRetrieveInformationModelFind
                || syntax == DicomUID.PatientRootQueryRetrieveInformationModelMove
                || syntax == DicomUID.StudyRootQueryRetrieveInformationModelFind
                || syntax == DicomUID.StudyRootQueryRetrieveInformationModelMove
                || syntax == DicomUID.BasicGrayscalePrintManagementMeta
                || syntax == DicomUID.BasicColorPrintManagementMeta)
            {
                pc.AcceptTransferSyntaxes(pc.GetTransferSyntaxes().ToArray());
            }
            else
            {
                pc.SetResult(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported);
            }
        }

        return SendAssociationAcceptAsync(association);
    }

    public Task OnReceiveAssociationReleaseRequestAsync()
    {
        return SendAssociationReleaseResponseAsync();
    }

    public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason)
    {
        _logger.LogWarning("DICOM Abort received. Source: {Source}, Reason: {Reason}", source, reason);
    }

    public void OnConnectionClosed(Exception? exception)
    {
        if (exception != null)
        {
            _logger.LogWarning(exception, "DICOM Connection closed with exception.");
        }
    }

    public async Task<DicomCStoreResponse> OnCStoreRequestAsync(DicomCStoreRequest request)
    {
        await _upsertService.ProcessDicomFileAsync(request.File);
        return new DicomCStoreResponse(request, DicomStatus.Success);
    }

    public Task OnCStoreRequestExceptionAsync(string tempFileName, Exception e)
    {
        _logger.LogError(e, "Error processing C-STORE request.");
        return Task.CompletedTask;
    }

    public Task<DicomCEchoResponse> OnCEchoRequestAsync(DicomCEchoRequest request)
    {
        _logger.LogInformation("C-ECHO request received.");
        return Task.FromResult(new DicomCEchoResponse(request, DicomStatus.Success));
    }

    public async IAsyncEnumerable<DicomCFindResponse> OnCFindRequestAsync(DicomCFindRequest request)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FocusMedDbContext>();

        var level = request.Dataset.GetSingleValue<DicomTag>(DicomTag.QueryRetrieveLevel);

        if (level?.ToString() == "PATIENT")
        {
            var patientName = request.Dataset.GetSingleValueOrDefault(DicomTag.PatientName, "*");
            var patientId = request.Dataset.GetSingleValueOrDefault(DicomTag.PatientID, string.Empty);

            var query = db.Patients.AsQueryable();
            if (!string.IsNullOrWhiteSpace(patientId))
                query = query.Where(p => p.PatientId.Contains(patientId));
            if (!string.IsNullOrWhiteSpace(patientName) && patientName != "*")
                query = query.Where(p => p.PatientName.Contains(patientName));

            var patients = await query.ToListAsync();

            foreach (var patient in patients)
            {
                var responseDataset = new DicomDataset
                {
                    { DicomTag.PatientName, patient.PatientName },
                    { DicomTag.PatientID, patient.PatientId }
                };
                yield return new DicomCFindResponse(request, DicomStatus.Pending) { Dataset = responseDataset };
            }
        }
        else if (level?.ToString() == "STUDY")
        {
            var studyUid = request.Dataset.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, string.Empty);
            var patientName = request.Dataset.GetSingleValueOrDefault(DicomTag.PatientName, string.Empty);
            var patientId = request.Dataset.GetSingleValueOrDefault(DicomTag.PatientID, string.Empty);

            var query = db.Studies
                .Include(s => s.Patient)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(studyUid))
                query = query.Where(s => s.StudyInstanceUid.Contains(studyUid));
            if (!string.IsNullOrWhiteSpace(patientId))
                query = query.Where(s => s.Patient.PatientId.Contains(patientId));
            if (!string.IsNullOrWhiteSpace(patientName))
                query = query.Where(s => s.Patient.PatientName.Contains(patientName));

            var studies = await query.ToListAsync();

            foreach (var study in studies)
            {
                var responseDataset = new DicomDataset
                {
                    { DicomTag.PatientName, study.Patient?.PatientName ?? string.Empty },
                    { DicomTag.PatientID, study.Patient?.PatientId ?? string.Empty },
                    { DicomTag.StudyInstanceUID, study.StudyInstanceUid },
                    { DicomTag.StudyDate, study.StudyDate?.ToString("yyyyMMdd") ?? string.Empty },
                    { DicomTag.AccessionNumber, string.Empty }
                };
                yield return new DicomCFindResponse(request, DicomStatus.Pending) { Dataset = responseDataset };
            }
        }
        else if (level?.ToString() == "SERIES")
        {
            var seriesUid = request.Dataset.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, string.Empty);
            var modality = request.Dataset.GetSingleValueOrDefault(DicomTag.Modality, string.Empty);

            var query = db.Series
                .Include(s => s.Study)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(seriesUid))
                query = query.Where(s => s.SeriesInstanceUid.Contains(seriesUid));
            if (!string.IsNullOrWhiteSpace(modality))
                query = query.Where(s => s.Modality.Contains(modality));

            var seriesList = await query.ToListAsync();

            foreach (var series in seriesList)
            {
                var responseDataset = new DicomDataset
                {
                    { DicomTag.SeriesInstanceUID, series.SeriesInstanceUid },
                    { DicomTag.Modality, series.Modality },
                    { DicomTag.StudyInstanceUID, series.Study?.StudyInstanceUid ?? string.Empty }
                };
                yield return new DicomCFindResponse(request, DicomStatus.Pending) { Dataset = responseDataset };
            }
        }

        yield return new DicomCFindResponse(request, DicomStatus.Success);
    }

    public async IAsyncEnumerable<DicomCMoveResponse> OnCMoveRequestAsync(DicomCMoveRequest request)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FocusMedDbContext>();

        var level = request.Dataset.GetSingleValue<DicomTag>(DicomTag.QueryRetrieveLevel);
        var affectedSop = request.Dataset.GetSingleValueOrDefault(DicomTag.AffectedSOPInstanceUID, string.Empty);

        List<DicomImage> images;

        if (!string.IsNullOrWhiteSpace(affectedSop))
        {
            images = await db.DicomImages
                .Where(i => i.SopInstanceUid == affectedSop)
                .ToListAsync();
        }
        else if (level?.ToString() == "SERIES")
        {
            var seriesUid = request.Dataset.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, string.Empty);
            images = await db.DicomImages
                .Include(i => i.Series)
                .Where(i => i.Series.SeriesInstanceUid == seriesUid)
                .ToListAsync();
        }
        else if (level?.ToString() == "STUDY")
        {
            var studyUid = request.Dataset.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, string.Empty);
            images = await db.DicomImages
                .Include(i => i.Series)
                .ThenInclude(s => s.Study)
                .Where(i => i.Series.Study.StudyInstanceUid == studyUid)
                .ToListAsync();
        }
        else
        {
            images = new List<DicomImage>();
        }

        var remaining = images.Count;
        var failed = 0;

        foreach (var image in images)
        {
            try
            {
                if (File.Exists(image.FilePath))
                {
                    var file = await DicomFile.OpenAsync(image.FilePath);
                    await SendRequestAsync(new DicomCStoreRequest(file));
                }
                else
                {
                    _logger.LogWarning("DICOM file not found: {FilePath}", image.FilePath);
                    failed++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send DICOM file {FilePath}", image.FilePath);
                failed++;
            }

            remaining--;

            yield return new DicomCMoveResponse(request, DicomStatus.Pending)
            {
                Dataset = new DicomDataset
                {
                    { DicomTag.NumberOfRemainingSuboperations, (ushort)remaining },
                    { DicomTag.NumberOfFailedSuboperations, (ushort)failed },
                    { DicomTag.NumberOfWarningSuboperations, (ushort)0 }
                }
            };
        }

        yield return new DicomCMoveResponse(request, DicomStatus.Success)
        {
            Dataset = new DicomDataset
            {
                { DicomTag.NumberOfRemainingSuboperations, (ushort)0 },
                { DicomTag.NumberOfFailedSuboperations, (ushort)failed },
                { DicomTag.NumberOfWarningSuboperations, (ushort)0 }
            }
        };
    }

    public async Task<DicomNCreateResponse> OnNCreateRequestAsync(DicomNCreateRequest request)
    {
        var sopUid = request.SOPInstanceUID.UID;
        var sopClass = request.SOPClassUID.UID;

        _logger.LogInformation("N-CREATE request for SOP Class {SopClass}, Instance {SopInstance}", sopClass, sopUid);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FocusMedDbContext>();

        if (sopClass == DicomUID.BasicFilmSession.UID)
        {
            var printJob = new PrintJob
            {
                SopInstanceUid = sopUid,
                NumberOfCopies = request.Dataset.GetSingleValueOrDefault(DicomTag.NumberOfCopies, (ushort)1),
                PrintPriority = request.Dataset.GetSingleValueOrDefault(DicomTag.PrintPriority, "NORMAL")
            };
            db.PrintJobs.Add(printJob);
            await db.SaveChangesAsync();
        }
        else if (sopClass == DicomUID.BasicFilmBox.UID)
        {
            var printJobUid = string.Empty;
            if (request.Dataset.TryGetSequence(DicomTag.ReferencedFilmSessionSequence, out var filmSessionSeq)
                && filmSessionSeq.Items.Count > 0)
            {
                printJobUid = filmSessionSeq.Items[0].GetSingleValueOrDefault(DicomTag.ReferencedSOPInstanceUID, string.Empty);
            }

            var printJob = await db.PrintJobs.FirstOrDefaultAsync(p => p.SopInstanceUid == printJobUid);

            var filmBox = new FilmBox
            {
                SopInstanceUid = sopUid,
                FilmSize = request.Dataset.GetSingleValueOrDefault(DicomTag.FilmSizeID, "A4"),
                Orientation = request.Dataset.GetSingleValueOrDefault(DicomTag.FilmOrientation, "PORTRAIT")
            };

            if (printJob != null)
            {
                filmBox.PrintJobId = printJob.Id;
            }

            db.FilmBoxes.Add(filmBox);
            await db.SaveChangesAsync();
        }
        else if (sopClass == DicomUID.BasicGrayscaleImageBox.UID || sopClass == DicomUID.BasicColorImageBox.UID)
        {
            var filmBoxUid = string.Empty;
            if (request.Dataset.TryGetSequence(DicomTag.ReferencedFilmBoxSequence, out var filmBoxSeq)
                && filmBoxSeq.Items.Count > 0)
            {
                filmBoxUid = filmBoxSeq.Items[0].GetSingleValueOrDefault(DicomTag.ReferencedSOPInstanceUID, string.Empty);
            }

            var filmBox = await db.FilmBoxes.FirstOrDefaultAsync(f => f.SopInstanceUid == filmBoxUid);

            var refImageUid = string.Empty;
            if (request.Dataset.TryGetSequence(DicomTag.ReferencedImageSequence, out var refImageSeq)
                && refImageSeq.Items.Count > 0)
            {
                refImageUid = refImageSeq.Items[0].GetSingleValueOrDefault(DicomTag.ReferencedSOPInstanceUID, string.Empty);
            }

            var imageBox = new PrintImageBox
            {
                SopInstanceUid = sopUid,
                ReferencedImageSopUid = refImageUid,
                FrameNumber = request.Dataset.GetSingleValueOrDefault(DicomTag.InstanceNumber, 0)
            };

            if (filmBox != null)
            {
                imageBox.FilmBoxId = filmBox.Id;
            }

            db.PrintImageBoxes.Add(imageBox);
            await db.SaveChangesAsync();
        }

        return new DicomNCreateResponse(request, DicomStatus.Success)
        {
            Dataset = request.Dataset
        };
    }

    public async Task<DicomNSetResponse> OnNSetRequestAsync(DicomNSetRequest request)
    {
        var sopUid = request.SOPInstanceUID.UID;

        _logger.LogInformation("N-SET request for Instance {SopInstance}", sopUid);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FocusMedDbContext>();

        var imageBox = await db.PrintImageBoxes.FirstOrDefaultAsync(i => i.SopInstanceUid == sopUid);
        if (imageBox != null)
        {
            if (request.Dataset.TryGetSequence(DicomTag.ReferencedImageSequence, out var refImageSeq)
                && refImageSeq.Items.Count > 0)
            {
                imageBox.ReferencedImageSopUid = refImageSeq.Items[0]
                    .GetSingleValueOrDefault(DicomTag.ReferencedSOPInstanceUID, string.Empty);
            }
            imageBox.FrameNumber = request.Dataset.GetSingleValueOrDefault(DicomTag.InstanceNumber, imageBox.FrameNumber);
            await db.SaveChangesAsync();
        }

        return new DicomNSetResponse(request, DicomStatus.Success);
    }

    public async Task<DicomNActionResponse> OnNActionRequestAsync(DicomNActionRequest request)
    {
        var sopUid = request.SOPInstanceUID.UID;
        var actionType = request.Dataset.GetSingleValueOrDefault(DicomTag.ActionTypeID, (ushort)0);

        _logger.LogInformation("N-ACTION request for Instance {SopInstance}, Action {Action}", sopUid, actionType);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FocusMedDbContext>();

        if (actionType == 1)
        {
            var filmBox = await db.FilmBoxes.FirstOrDefaultAsync(f => f.SopInstanceUid == sopUid);
            if (filmBox != null)
            {
                var printJob = await db.PrintJobs.FirstOrDefaultAsync(p => p.Id == filmBox.PrintJobId);
                if (printJob != null)
                {
                    printJob.Status = PrintStatus.Printing;
                    await db.SaveChangesAsync();
                }
            }
        }

        return new DicomNActionResponse(request, DicomStatus.Success);
    }

    public async Task<DicomNDeleteResponse> OnNDeleteRequestAsync(DicomNDeleteRequest request)
    {
        var sopUid = request.SOPInstanceUID.UID;

        _logger.LogInformation("N-DELETE request for Instance {SopInstance}", sopUid);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FocusMedDbContext>();

        var printJob = await db.PrintJobs.FirstOrDefaultAsync(p => p.SopInstanceUid == sopUid);
        if (printJob != null)
        {
            var filmBoxes = await db.FilmBoxes.Where(f => f.PrintJobId == printJob.Id).ToListAsync();
            foreach (var fb in filmBoxes)
            {
                var imageBoxes = await db.PrintImageBoxes.Where(i => i.FilmBoxId == fb.Id).ToListAsync();
                db.PrintImageBoxes.RemoveRange(imageBoxes);
            }
            db.FilmBoxes.RemoveRange(filmBoxes);
            db.PrintJobs.Remove(printJob);
            await db.SaveChangesAsync();
        }

        return new DicomNDeleteResponse(request, DicomStatus.Success);
    }

    public Task<DicomNGetResponse> OnNGetRequestAsync(DicomNGetRequest request)
    {
        _logger.LogInformation("N-GET request for Instance {SopInstance}", request.SOPInstanceUID.UID);
        return Task.FromResult(new DicomNGetResponse(request, DicomStatus.Success)
        {
            Dataset = new DicomDataset()
        });
    }

    public Task<DicomNEventReportResponse> OnNEventReportRequestAsync(DicomNEventReportRequest request)
    {
        _logger.LogInformation("N-EVENT-REPORT request for Instance {SopInstance}", request.SOPInstanceUID.UID);
        return Task.FromResult(new DicomNEventReportResponse(request, DicomStatus.Success));
    }
}
