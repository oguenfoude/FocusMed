using System.Text;
using FellowOakDicom;
using FellowOakDicom.Network;
using FocusMed.Data;
using FocusMed.Data.Entities;
using FocusMed.Dicom.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    private readonly IOptions<DicomNetworkingOptions> _networkingOptions;
    private readonly DicomTransferSyntax[] _acceptedTransferSyntaxes;
    private DateTime _associationStartTime;

    private static readonly Dictionary<string, DicomTransferSyntax> TransferSyntaxMap = new()
    {
        ["ImplicitVRLittleEndian"] = DicomTransferSyntax.ImplicitVRLittleEndian,
        ["ExplicitVRLittleEndian"] = DicomTransferSyntax.ExplicitVRLittleEndian,
        ["JPEGLSLossless"] = DicomTransferSyntax.JPEGLSLossless,
        ["JPEG2000Lossless"] = DicomTransferSyntax.JPEG2000Lossless,
        ["RLELossless"] = DicomTransferSyntax.RLELossless,
        ["JPEGProcess1"] = DicomTransferSyntax.JPEGProcess1,
        ["JPEGProcess2_4"] = DicomTransferSyntax.JPEGProcess2_4,
        ["JPEGProcess14"] = DicomTransferSyntax.JPEGProcess14,
        ["MPEG2"] = DicomTransferSyntax.MPEG2,
        ["MPEG4AVCH264HighProfileLevel41"] = DicomTransferSyntax.MPEG4AVCH264HighProfileLevel41,
    };

    public FocusMedScp(
        INetworkStream stream,
        Encoding fallbackEncoding,
        ILogger logger,
        DicomServiceDependencies dependencies,
        DicomUpsertService upsertService,
        IServiceScopeFactory scopeFactory,
        IOptions<DicomNetworkingOptions> networkingOptions)
        : base(stream, fallbackEncoding, logger, dependencies)
    {
        _upsertService = upsertService;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _networkingOptions = networkingOptions;

        _acceptedTransferSyntaxes = _networkingOptions.Value.SupportedTransferSyntaxes
            .Select(name => TransferSyntaxMap.TryGetValue(name, out var ts) ? ts : null)
            .Where(ts => ts != null)
            .ToArray()!;
    }

    public async Task OnReceiveAssociationRequestAsync(DicomAssociation association)
    {
        _associationStartTime = DateTime.UtcNow;

        if (_networkingOptions.Value.EnforceAeWhitelist)
        {
            var callingAe = association.CallingAE;
            var remoteIp = association.RemoteHost;

            var allowed = _networkingOptions.Value.AllowedCallingAETitles
                .Any(ae => ae.AETitle == callingAe && ae.IPAddress == remoteIp);

            if (!allowed)
            {
                _logger.LogWarning("REJECTED {CallingAET} from {RemoteIp} (not on whitelist)", callingAe, remoteIp);
                await WriteAuditEntryAsync(association, remoteIp, AssociationOutcome.Rejected);
                await SendAssociationRejectAsync(
                    DicomRejectResult.Permanent,
                    DicomRejectSource.ServiceUser,
                    DicomRejectReason.NoReasonGiven);
                return;
            }
        }

        var accepted = 0;
        var rejected = 0;

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
                || syntax == DicomUID.BasicColorPrintManagementMeta
                || syntax == DicomUID.BasicFilmSession
                || syntax == DicomUID.BasicFilmBox
                || syntax == DicomUID.BasicGrayscaleImageBox
                || syntax == DicomUID.BasicColorImageBox
                || syntax == DicomUID.Printer
                || syntax == DicomUID.PrintJob
                || syntax == DicomUID.ModalityWorklistInformationModelFind
                || syntax == DicomUID.Parse("1.2.840.10008.1.20.1"))
            {
                var requestedSyntaxes = pc.GetTransferSyntaxes();
                var syntaxesToAccept = requestedSyntaxes.Intersect(_acceptedTransferSyntaxes).ToArray();

                if (syntaxesToAccept.Any())
                {
                    pc.AcceptTransferSyntaxes(syntaxesToAccept);
                    accepted++;
                }
                else
                {
                    pc.SetResult(DicomPresentationContextResult.RejectTransferSyntaxesNotSupported);
                    rejected++;
                }
            }
            else
            {
                pc.SetResult(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported);
                rejected++;
            }
        }

        var remoteIpForAudit = association.RemoteHost;

        if (accepted == 0)
        {
            _logger.LogWarning("Association: {CallingAe} -> {CalledAe} | 0 PCs accepted, rejecting association",
                association.CallingAE, association.CalledAE);
            await WriteAuditEntryAsync(association, remoteIpForAudit, AssociationOutcome.Rejected);
            await SendAssociationRejectAsync(
                DicomRejectResult.Permanent,
                DicomRejectSource.ServiceUser,
                DicomRejectReason.NoReasonGiven);
            return;
        }

        _logger.LogInformation("Association: {CallingAe} -> {CalledAe} | {Accepted} accepted, {Rejected} rejected",
            association.CallingAE, association.CalledAE, accepted, rejected);

        var outcome = rejected > 0 ? AssociationOutcome.PartiallyAccepted : AssociationOutcome.Success;
        await WriteAuditEntryAsync(association, remoteIpForAudit, outcome);
        await SendAssociationAcceptAsync(association);
    }

    public Task OnReceiveAssociationReleaseRequestAsync()
    {
        return SendAssociationReleaseResponseAsync();
    }

    private async Task WriteAuditEntryAsync(DicomAssociation association, string remoteIp, AssociationOutcome outcome)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FocusMedDbContext>();
            var sopClasses = string.Join(",", association.PresentationContexts.Select(pc => pc.AbstractSyntax.Name));
            var durationMs = (int)(DateTime.UtcNow - _associationStartTime).TotalMilliseconds;

            db.AssociationAuditEntries.Add(new AssociationAuditEntry
            {
                CallingAeTitle = association.CallingAE,
                RemoteIp = remoteIp,
                CalledAeTitle = association.CalledAE,
                RequestedSopClasses = sopClasses,
                Outcome = outcome,
                DurationMs = durationMs,
                Timestamp = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write association audit entry");
        }
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
        try
        {
            await _upsertService.StoreFileOnlyAsync(request.File);
            return new DicomCStoreResponse(request, DicomStatus.Success);
        }
        catch (Exception ex)
        {
            var sopUid = request.File.Dataset.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, string.Empty);
            _logger.LogError(ex, "C-STORE failed for {SopUid}", sopUid);
            return new DicomCStoreResponse(request, DicomStatus.ProcessingFailure);
        }
    }

    public Task OnCStoreRequestExceptionAsync(string tempFileName, Exception e)
    {
        _logger.LogError(e, "Error processing C-STORE request.");
        return Task.CompletedTask;
    }

    public Task<DicomCEchoResponse> OnCEchoRequestAsync(DicomCEchoRequest request)
    {
        return Task.FromResult(new DicomCEchoResponse(request, DicomStatus.Success));
    }

    public async IAsyncEnumerable<DicomCFindResponse> OnCFindRequestAsync(DicomCFindRequest request)
    {
        DicomCFindResponse[] results;
        Exception? queryException = null;
        try
        {
            results = await ExecuteCFindQueryAsync(request);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "C-FIND query failed");
            queryException = ex;
            results = [];
        }

        if (queryException != null)
        {
            yield return new DicomCFindResponse(request, DicomStatus.ProcessingFailure);
            yield break;
        }

        foreach (var r in results)
            yield return r;
    }

    private async Task<DicomCFindResponse[]> ExecuteCFindQueryAsync(DicomCFindRequest request)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FocusMedDbContext>();

        var level = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.QueryRetrieveLevel, string.Empty);
        var results = new List<DicomCFindResponse>();

        if (level == string.Empty || request.Dataset.Contains(DicomTag.ScheduledProcedureStepSequence))
        {
            var patientName = request.Dataset.GetSingleValueOrDefault(DicomTag.PatientName, string.Empty);
            var patientId = request.Dataset.GetSingleValueOrDefault(DicomTag.PatientID, string.Empty);

            var query = db.WorklistEntries.AsQueryable();
            if (!string.IsNullOrWhiteSpace(patientId)) query = query.Where(w => w.PatientId.Contains(patientId));
            if (!string.IsNullOrWhiteSpace(patientName) && patientName != "*")
            {
                var searchName = patientName.Replace("*", "").Replace("?", "");
                query = query.Where(w => EF.Functions.Like(w.PatientName, $"%{searchName}%"));
            }

            var entries = await query.ToListAsync();

            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry.StudyInstanceUid))
                {
                    entry.StudyInstanceUid = $"2.25.{Guid.NewGuid():N}";
                }

                var responseDataset = new DicomDataset
                {
                    { DicomTag.PatientName, entry.PatientName },
                    { DicomTag.PatientID, entry.PatientId },
                    { DicomTag.AccessionNumber, entry.AccessionNumber },
                    { DicomTag.StudyInstanceUID, entry.StudyInstanceUid },
                    { DicomTag.RequestedProcedureID, entry.RequestedProcedureId }
                };

                var spsDataset = new DicomDataset
                {
                    { DicomTag.Modality, entry.Modality },
                    { DicomTag.ScheduledProcedureStepID, entry.ScheduledProcedureStepId }
                };
                if (entry.ScheduledProcedureStepStartDate.HasValue)
                {
                    spsDataset.Add(DicomTag.ScheduledProcedureStepStartDate, entry.ScheduledProcedureStepStartDate.Value.ToString("yyyyMMdd"));
                }

                responseDataset.Add(new DicomSequence(DicomTag.ScheduledProcedureStepSequence, spsDataset));

                results.Add(new DicomCFindResponse(request, DicomStatus.Pending) { Dataset = responseDataset });
            }

            await db.SaveChangesAsync();
            results.Add(new DicomCFindResponse(request, DicomStatus.Success));
            return results.ToArray();
        }

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
                results.Add(new DicomCFindResponse(request, DicomStatus.Pending) { Dataset = responseDataset });
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
                results.Add(new DicomCFindResponse(request, DicomStatus.Pending) { Dataset = responseDataset });
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
                results.Add(new DicomCFindResponse(request, DicomStatus.Pending) { Dataset = responseDataset });
            }
        }

        results.Add(new DicomCFindResponse(request, DicomStatus.Success));
        return results.ToArray();
    }

    public async IAsyncEnumerable<DicomCMoveResponse> OnCMoveRequestAsync(DicomCMoveRequest request)
    {
        List<DicomImage> images;
        Exception? queryException = null;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FocusMedDbContext>();

            var level = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.QueryRetrieveLevel, string.Empty);
            var affectedSop = request.Dataset.GetSingleValueOrDefault(DicomTag.AffectedSOPInstanceUID, string.Empty);

            if (!string.IsNullOrWhiteSpace(affectedSop))
            {
                images = await db.DicomImages
                    .Where(i => i.SopInstanceUid == affectedSop)
                    .ToListAsync();
            }
            else if (level == "SERIES")
            {
                var seriesUid = request.Dataset.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, string.Empty);
                images = await db.DicomImages
                    .Include(i => i.Series)
                    .Where(i => i.Series.SeriesInstanceUid == seriesUid)
                    .ToListAsync();
            }
            else if (level == "STUDY")
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
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "C-MOVE query failed");
            queryException = ex;
            images = new List<DicomImage>();
        }

        if (queryException != null)
        {
            yield return new DicomCMoveResponse(request, DicomStatus.ProcessingFailure)
            {
                Dataset = new DicomDataset
                {
                    { DicomTag.NumberOfRemainingSuboperations, (ushort)0 },
                    { DicomTag.NumberOfFailedSuboperations, (ushort)1 },
                    { DicomTag.NumberOfWarningSuboperations, (ushort)0 }
                }
            };
            yield break;
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
        try
        {
            var sopUid = request.SOPInstanceUID?.UID ?? string.Empty;
            var sopClass = request.SOPClassUID?.UID ?? string.Empty;

            if (string.IsNullOrEmpty(sopUid))
            {
                sopUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
            }

            if (string.IsNullOrEmpty(sopClass))
            {
                _logger.LogWarning("N-CREATE rejected: missing SOP Class UID");
                return new DicomNCreateResponse(request, DicomStatus.InvalidArgumentValue);
            }

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FocusMedDbContext>();

            if (sopClass == DicomUID.BasicFilmSession.UID)
            {
                var existing = await db.PrintJobs.FirstOrDefaultAsync(p => p.SopInstanceUid == sopUid);
                if (existing != null)
                {
                    var ds = new DicomDataset(request.Dataset);
                    ds.AddOrUpdate(DicomTag.SOPInstanceUID, sopUid);
                    return new DicomNCreateResponse(request, DicomStatus.Success) { Dataset = ds };
                }

                var printJob = new PrintJob
                {
                    SopInstanceUid = sopUid,
                    NumberOfCopies = request.Dataset.GetSingleValueOrDefault(DicomTag.NumberOfCopies, (ushort)1),
                    PrintPriority = request.Dataset.GetSingleValueOrDefault(DicomTag.PrintPriority, "NORMAL")
                };

                db.PrintJobs.Add(printJob);
                await db.SaveChangesAsync();
                _logger.LogInformation("Print Job #{PrintJobId} created ({Copies} {CopyLabel}, {Priority})", printJob.Id, printJob.NumberOfCopies, printJob.NumberOfCopies == 1 ? "copy" : "copies", printJob.PrintPriority);
            }
            else if (sopClass == DicomUID.BasicFilmBox.UID)
            {
                var printJobUid = string.Empty;
                if (request.Dataset.TryGetSequence(DicomTag.ReferencedFilmSessionSequence, out var filmSessionSeq)
                    && filmSessionSeq.Items.Count > 0)
                {
                    printJobUid = filmSessionSeq.Items[0].GetSingleValueOrDefault(DicomTag.ReferencedSOPInstanceUID, string.Empty);
                }

                var existing = await db.FilmBoxes.FirstOrDefaultAsync(f => f.SopInstanceUid == sopUid);
                if (existing != null)
                {
                    var ds = new DicomDataset(request.Dataset);
                    ds.AddOrUpdate(DicomTag.SOPInstanceUID, sopUid);
                    return new DicomNCreateResponse(request, DicomStatus.Success) { Dataset = ds };
                }

                PrintJob? printJob = null;

                if (!string.IsNullOrEmpty(printJobUid))
                {
                    printJob = await db.PrintJobs.FirstOrDefaultAsync(p => p.SopInstanceUid == printJobUid);
                }

                if (printJob == null)
                {
                    printJob = await db.PrintJobs
                        .Where(p => p.Status == PrintStatus.Pending)
                        .OrderByDescending(p => p.CreatedAt)
                        .FirstOrDefaultAsync();
                    if (printJob != null)
                        _logger.LogInformation("FilmBox N-CREATE: matched PrintJob #{PrintJobId} via pending fallback", printJob.Id);
                }

                if (printJob == null)
                {
                    _logger.LogWarning("FilmBox N-CREATE: no PrintJob found, creating orphaned FilmBox");
                }

                var filmBox = new FilmBox
                {
                    SopInstanceUid = sopUid,
                    FilmSize = request.Dataset.GetSingleValueOrDefault(DicomTag.FilmSizeID, "A4"),
                    Orientation = request.Dataset.GetSingleValueOrDefault(DicomTag.FilmOrientation, "PORTRAIT"),
                    PrintJobId = printJob?.Id
                };

                db.FilmBoxes.Add(filmBox);
                await db.SaveChangesAsync();

                var imageDisplayFormat = request.Dataset.GetSingleValueOrDefault(DicomTag.ImageDisplayFormat, "STANDARD\\1,1");
                var imageBoxCount = ParseImageBoxCount(imageDisplayFormat);

                var isColor = Association.PresentationContexts
                    .Any(pc => pc.AbstractSyntax == DicomUID.BasicColorPrintManagementMeta
                        && pc.Result == DicomPresentationContextResult.Accept);
                if (!isColor)
                    isColor = request.Dataset.GetSingleValueOrDefault(DicomTag.PrintPriority, "") == "COLOR";
                var imageBoxSopClass = isColor ? DicomUID.BasicColorImageBox : DicomUID.BasicGrayscaleImageBox;

                var referencedImageBoxSeq = new DicomSequence(DicomTag.ReferencedImageBoxSequence);

                for (int i = 0; i < imageBoxCount; i++)
                {
                    var imageBoxUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;

                    var imageBox = new PrintImageBox
                    {
                        SopInstanceUid = imageBoxUid,
                        FilmBoxId = filmBox.Id,
                        FrameNumber = i + 1
                    };
                    db.PrintImageBoxes.Add(imageBox);

                    var refItem = new DicomDataset
                    {
                        { DicomTag.ReferencedSOPClassUID, imageBoxSopClass.UID },
                        { DicomTag.ReferencedSOPInstanceUID, imageBoxUid }
                    };
                    referencedImageBoxSeq.Items.Add(refItem);
                }

                await db.SaveChangesAsync();

                var filmBoxResponseDataset = new DicomDataset(request.Dataset);
                filmBoxResponseDataset.AddOrUpdate(DicomTag.SOPInstanceUID, sopUid);
                filmBoxResponseDataset.AddOrUpdate(DicomTag.ImageDisplayFormat, imageDisplayFormat);
                if (filmSessionSeq != null)
                    filmBoxResponseDataset.AddOrUpdate(DicomTag.ReferencedFilmSessionSequence, filmSessionSeq);
                filmBoxResponseDataset.AddOrUpdate(DicomTag.ReferencedImageBoxSequence, referencedImageBoxSeq);

                return new DicomNCreateResponse(request, DicomStatus.Success) { Dataset = filmBoxResponseDataset };
            }
            else if (sopClass == DicomUID.BasicGrayscaleImageBox.UID || sopClass == DicomUID.BasicColorImageBox.UID)
            {
                var filmBoxUid = string.Empty;
                if (request.Dataset.TryGetSequence(DicomTag.ReferencedFilmBoxSequence, out var filmBoxSeq)
                    && filmBoxSeq.Items.Count > 0)
                {
                    filmBoxUid = filmBoxSeq.Items[0].GetSingleValueOrDefault(DicomTag.ReferencedSOPInstanceUID, string.Empty);
                }

                var existing = await db.PrintImageBoxes.FirstOrDefaultAsync(i => i.SopInstanceUid == sopUid);
                if (existing != null)
                {
                    var ds = new DicomDataset(request.Dataset);
                    ds.AddOrUpdate(DicomTag.SOPInstanceUID, sopUid);
                    return new DicomNCreateResponse(request, DicomStatus.Success) { Dataset = ds };
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
                    FrameNumber = request.Dataset.GetSingleValueOrDefault(DicomTag.InstanceNumber, 0),
                    FilmBoxId = filmBox?.Id
                };

                db.PrintImageBoxes.Add(imageBox);
                await db.SaveChangesAsync();
            }

            var responseDataset = new DicomDataset(request.Dataset);
            responseDataset.AddOrUpdate(DicomTag.SOPInstanceUID, sopUid);

            return new DicomNCreateResponse(request, DicomStatus.Success)
            {
                Dataset = responseDataset
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "N-CREATE failed");
            return new DicomNCreateResponse(request, DicomStatus.ProcessingFailure);
        }
    }

    public async Task<DicomNSetResponse> OnNSetRequestAsync(DicomNSetRequest request)
    {
        string sopUid;
        try { sopUid = request.SOPInstanceUID?.UID ?? string.Empty; }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to extract SOP Instance UID from N-SET request"); sopUid = string.Empty; }

        if (string.IsNullOrEmpty(sopUid))
        {
            var command = new DicomDataset
            {
                { DicomTag.CommandField, (ushort)DicomCommandField.NSetResponse },
                { DicomTag.MessageIDBeingRespondedTo, request.MessageID },
                { DicomTag.Status, (ushort)DicomStatus.InvalidArgumentValue.Code },
                { DicomTag.CommandDataSetType, (ushort)0x0101 },
            };
            var response = new DicomNSetResponse(command);
            response.PresentationContext = request.PresentationContext;
            return response;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FocusMedDbContext>();

            var imageBox = await db.PrintImageBoxes
                .Include(i => i.FilmBox).ThenInclude(f => f!.PrintJob).ThenInclude(p => p!.Patient)
                .FirstOrDefaultAsync(i => i.SopInstanceUid == sopUid);
            if (imageBox != null)
            {
                var instanceNumber = request.Dataset.GetSingleValueOrDefault(DicomTag.InstanceNumber, imageBox.FrameNumber);
                imageBox.FrameNumber = instanceNumber;

                string? patientId = null;
                string? patientName = null;

                if (imageBox.FilmBox?.PrintJob?.Patient != null)
                {
                    patientId = imageBox.FilmBox.PrintJob.Patient.PatientId;
                    patientName = imageBox.FilmBox.PrintJob.Patient.PatientName;
                    _logger.LogDebug("Patient from PrintJob chain: {PatientId} - {PatientName}", patientId, patientName);
                }

                DicomSequence? imageSeq = null;
                if (request.Dataset.TryGetSequence(DicomTag.BasicGrayscaleImageSequence, out var gsSeq)
                    && gsSeq.Items.Count > 0)
                    imageSeq = gsSeq;
                else if (request.Dataset.TryGetSequence(DicomTag.BasicColorImageSequence, out var csSeq)
                    && csSeq.Items.Count > 0)
                    imageSeq = csSeq;

                if (string.IsNullOrEmpty(patientId) && imageSeq != null && imageSeq.Items.Count > 0)
                {
                    patientId = imageSeq.Items[0].GetSingleValueOrDefault(DicomTag.PatientID, string.Empty);
                    patientName = imageSeq.Items[0].GetSingleValueOrDefault(DicomTag.PatientName, string.Empty);
                    if (!string.IsNullOrEmpty(patientId))
                        _logger.LogDebug("Patient from inner DICOM dataset: {PatientId} - {PatientName}", patientId, patientName);
                }

                if (string.IsNullOrEmpty(patientId))
                {
                    _logger.LogWarning("No patient info in print image box {SopUid}, skipping image ingestion", sopUid);
                    await db.SaveChangesAsync();
                    return new DicomNSetResponse(request, DicomStatus.ProcessingFailure);
                }

                if (imageSeq != null)
                {
                    var innerDataset = imageSeq.Items[0];
                    var storedFile = await _upsertService.IngestPrintImageAsync(innerDataset, patientId, patientName!);
                    if (storedFile != null)
                    {
                        var newSopUid = storedFile.Dataset.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, string.Empty);
                        imageBox.ReferencedImageSopUid = newSopUid;

                        var newStudyUid = storedFile.Dataset.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, string.Empty);
                        var printStudy = await db.Studies.FirstOrDefaultAsync(s => s.StudyInstanceUid == newStudyUid);
                        if (printStudy != null && imageBox.FilmBox?.PrintJob != null)
                        {
                            imageBox.FilmBox.PrintJob.PatientId = printStudy.PatientId;
                            imageBox.FilmBox.PrintJob.StudyId = printStudy.Id;
                        }
                    }
                }

                await db.SaveChangesAsync();
            }
            else
            {
                _logger.LogWarning("Image Box not found for N-SET: {SopUid}", sopUid);
                var cmd = new DicomDataset
                {
                    { DicomTag.CommandField, (ushort)DicomCommandField.NSetResponse },
                    { DicomTag.MessageIDBeingRespondedTo, request.MessageID },
                    { DicomTag.Status, (ushort)DicomStatus.ProcessingFailure.Code },
                    { DicomTag.CommandDataSetType, (ushort)0x0101 },
                };
                var resp = new DicomNSetResponse(cmd);
                resp.PresentationContext = request.PresentationContext;
                return resp;
            }

            return new DicomNSetResponse(request, DicomStatus.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "N-SET failed");
            var command = new DicomDataset
            {
                { DicomTag.CommandField, (ushort)DicomCommandField.NSetResponse },
                { DicomTag.MessageIDBeingRespondedTo, request.MessageID },
                { DicomTag.Status, (ushort)DicomStatus.ProcessingFailure.Code },
                { DicomTag.CommandDataSetType, (ushort)0x0101 },
            };
            var response = new DicomNSetResponse(command);
            response.PresentationContext = request.PresentationContext;
            return response;
        }
    }

    public async Task<DicomNActionResponse> OnNActionRequestAsync(DicomNActionRequest request)
    {
        string sopUid;
        try { sopUid = request.SOPInstanceUID?.UID ?? string.Empty; }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to extract SOP Instance UID from N-ACTION request"); sopUid = string.Empty; }

        string sopClassUid;
        try { sopClassUid = request.SOPClassUID?.UID ?? string.Empty; }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to extract SOP Class UID from N-ACTION request"); sopClassUid = string.Empty; }

        var actionType = request.Dataset.GetSingleValueOrDefault(DicomTag.ActionTypeID, (ushort)0);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FocusMedDbContext>();

            if (!string.IsNullOrEmpty(sopUid) && !string.IsNullOrEmpty(sopClassUid))
            {
                if (sopClassUid == DicomUID.BasicFilmSession.UID && actionType == 1)
                {
                    var printJob = await db.PrintJobs.Include(p => p.Patient).FirstOrDefaultAsync(p => p.SopInstanceUid == sopUid);
                    if (printJob != null)
                    {
                        printJob.Status = PrintStatus.Completed;
                        await db.SaveChangesAsync();
                    }
                }
                else if (sopClassUid == DicomUID.BasicFilmBox.UID && actionType == 1)
                {
                    var filmBox = await db.FilmBoxes.FirstOrDefaultAsync(f => f.SopInstanceUid == sopUid);
                    if (filmBox?.PrintJobId != null)
                    {
                        var printJob = await db.PrintJobs.FirstOrDefaultAsync(p => p.Id == filmBox.PrintJobId);
                        if (printJob != null)
                        {
                            printJob.Status = PrintStatus.Completed;
                            await db.SaveChangesAsync();
                        }
                    }
                }
                else if (sopClassUid == "1.2.840.10008.1.20.1")
                {
                    var transactionUid = request.Dataset.GetSingleValueOrDefault(DicomTag.TransactionUID, string.Empty);
                    if (request.Dataset.TryGetSequence(DicomTag.ReferencedSOPSequence, out var referencedSopSeq)
                        && referencedSopSeq.Items.Count > 0)
                    {
                        var uids = new List<string>();
                        foreach (var item in referencedSopSeq.Items)
                        {
                            uids.Add(item.GetSingleValueOrDefault(DicomTag.ReferencedSOPInstanceUID, string.Empty));
                        }

                        var job = new StorageCommitmentJob
                        {
                            TransactionUid = transactionUid,
                            RequestedSopInstanceUids = string.Join(",", uids),
                            CallingAet = Association.CallingAE,
                            Status = StorageCommitmentStatus.Pending
                        };

                        db.StorageCommitmentJobs.Add(job);
                        await db.SaveChangesAsync();
                    }
                }
            }
            else
            {
                _logger.LogDebug("N-ACTION with empty SOP Instance UID or SOP Class UID, ignoring");
            }

            var actionCommand = new DicomDataset
            {
                { DicomTag.CommandField, (ushort)DicomCommandField.NActionResponse },
                { DicomTag.MessageIDBeingRespondedTo, request.MessageID },
                { DicomTag.Status, (ushort)DicomStatus.Success.Code },
                { DicomTag.CommandDataSetType, (ushort)0x0101 },
            };
            var actionResponse = new DicomNActionResponse(actionCommand);
            actionResponse.PresentationContext = request.PresentationContext;
            return actionResponse;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "N-ACTION failed");
            var command = new DicomDataset
            {
                { DicomTag.CommandField, (ushort)DicomCommandField.NActionResponse },
                { DicomTag.MessageIDBeingRespondedTo, request.MessageID },
                { DicomTag.Status, (ushort)DicomStatus.ProcessingFailure.Code },
                { DicomTag.CommandDataSetType, (ushort)0x0101 },
            };
            var response = new DicomNActionResponse(command);
            response.PresentationContext = request.PresentationContext;
            return response;
        }
    }

    public async Task<DicomNDeleteResponse> OnNDeleteRequestAsync(DicomNDeleteRequest request)
    {
        string sopUid;
        try { sopUid = request.SOPInstanceUID?.UID ?? string.Empty; }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to extract SOP Instance UID from N-DELETE request"); sopUid = string.Empty; }

        if (string.IsNullOrEmpty(sopUid))
        {
            _logger.LogWarning("N-DELETE: SOP Instance UID is empty, attempting fallback lookup");

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FocusMedDbContext>();

                var printJob = await db.PrintJobs
                    .Where(p => p.Status == PrintStatus.Pending || p.Status == PrintStatus.Completed)
                    .OrderByDescending(p => p.CreatedAt)
                    .Include(p => p.Patient)
                    .Include(p => p.FilmBoxes).ThenInclude(fb => fb.ImageBoxes)
                    .AsSplitQuery()
                    .FirstOrDefaultAsync();

                if (printJob == null)
                {
                    var notFoundCmd = new DicomDataset
                    {
                        { DicomTag.CommandField, (ushort)DicomCommandField.NDeleteResponse },
                        { DicomTag.MessageIDBeingRespondedTo, request.MessageID },
                        { DicomTag.Status, (ushort)DicomStatus.ProcessingFailure.Code },
                        { DicomTag.CommandDataSetType, (ushort)0x0101 },
                    };
                    var notFoundResp = new DicomNDeleteResponse(notFoundCmd);
                    notFoundResp.PresentationContext = request.PresentationContext;
                    return notFoundResp;
                }

                _logger.LogInformation("N-DELETE: matched PrintJob #{PrintJobId} via fallback", printJob.Id);

                foreach (var fb in printJob.FilmBoxes)
                {
                    db.PrintImageBoxes.RemoveRange(fb.ImageBoxes);
                }
                db.FilmBoxes.RemoveRange(printJob.FilmBoxes);
                db.PrintJobs.Remove(printJob);
                await db.SaveChangesAsync();

                var command = new DicomDataset
                {
                    { DicomTag.CommandField, (ushort)DicomCommandField.NDeleteResponse },
                    { DicomTag.MessageIDBeingRespondedTo, request.MessageID },
                    { DicomTag.Status, (ushort)DicomStatus.Success.Code },
                    { DicomTag.CommandDataSetType, (ushort)0x0101 },
                };
                var response = new DicomNDeleteResponse(command);
                response.PresentationContext = request.PresentationContext;
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "N-DELETE fallback failed");
                var failCmd = new DicomDataset
                {
                    { DicomTag.CommandField, (ushort)DicomCommandField.NDeleteResponse },
                    { DicomTag.MessageIDBeingRespondedTo, request.MessageID },
                    { DicomTag.Status, (ushort)DicomStatus.ProcessingFailure.Code },
                    { DicomTag.CommandDataSetType, (ushort)0x0101 },
                };
                var failResp = new DicomNDeleteResponse(failCmd);
                failResp.PresentationContext = request.PresentationContext;
                return failResp;
            }
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FocusMedDbContext>();

            var printJob = await db.PrintJobs
                .Include(p => p.Patient)
                .Include(p => p.FilmBoxes).ThenInclude(fb => fb.ImageBoxes)
                .AsSplitQuery()
                .FirstOrDefaultAsync(p => p.SopInstanceUid == sopUid);
            if (printJob == null)
            {
                _logger.LogWarning("N-DELETE: PrintJob not found for SOP {SopUid}", sopUid);
                var notFoundCmd = new DicomDataset
                {
                    { DicomTag.CommandField, (ushort)DicomCommandField.NDeleteResponse },
                    { DicomTag.MessageIDBeingRespondedTo, request.MessageID },
                    { DicomTag.Status, (ushort)DicomStatus.ProcessingFailure.Code },
                    { DicomTag.CommandDataSetType, (ushort)0x0101 },
                };
                var notFoundResponse = new DicomNDeleteResponse(notFoundCmd);
                notFoundResponse.PresentationContext = request.PresentationContext;
                return notFoundResponse;
            }

            foreach (var fb in printJob.FilmBoxes)
            {
                db.PrintImageBoxes.RemoveRange(fb.ImageBoxes);
            }
            db.FilmBoxes.RemoveRange(printJob.FilmBoxes);
            db.PrintJobs.Remove(printJob);
            await db.SaveChangesAsync();

            var command = new DicomDataset
            {
                { DicomTag.CommandField, (ushort)DicomCommandField.NDeleteResponse },
                { DicomTag.MessageIDBeingRespondedTo, request.MessageID },
                { DicomTag.Status, (ushort)DicomStatus.Success.Code },
                { DicomTag.CommandDataSetType, (ushort)0x0101 },
            };
            var response = new DicomNDeleteResponse(command);
            response.PresentationContext = request.PresentationContext;
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "N-DELETE failed");
            var command = new DicomDataset
            {
                { DicomTag.CommandField, (ushort)DicomCommandField.NDeleteResponse },
                { DicomTag.MessageIDBeingRespondedTo, request.MessageID },
                { DicomTag.Status, (ushort)DicomStatus.ProcessingFailure.Code },
                { DicomTag.CommandDataSetType, (ushort)0x0101 },
            };
            var response = new DicomNDeleteResponse(command);
            response.PresentationContext = request.PresentationContext;
            return response;
        }
    }

    public Task<DicomNGetResponse> OnNGetRequestAsync(DicomNGetRequest request)
    {
        var dataset = new DicomDataset
        {
            { DicomTag.PrinterStatus, "NORMAL" },
            { DicomTag.PrinterStatusInfo, "IDLE" },
            { DicomTag.PrinterName, "FocusMed" }
        };

        return Task.FromResult(new DicomNGetResponse(request, DicomStatus.Success) { Dataset = dataset });
    }

    public Task<DicomNEventReportResponse> OnNEventReportRequestAsync(DicomNEventReportRequest request)
    {
        return Task.FromResult(new DicomNEventReportResponse(request, DicomStatus.Success));
    }

    private static int ParseImageBoxCount(string imageDisplayFormat)
    {
        if (string.IsNullOrWhiteSpace(imageDisplayFormat))
            return 1;

        var parts = imageDisplayFormat.Split('\\');
        if (parts.Length < 2)
            return 1;

        var dimensions = parts[1].Split(',');
        if (dimensions.Length >= 2
            && int.TryParse(dimensions[0], out var cols)
            && int.TryParse(dimensions[1], out var rows))
        {
            return Math.Max(1, cols * rows);
        }

        if (int.TryParse(dimensions[0], out var single))
            return Math.Max(1, single);

        return 1;
    }
}
