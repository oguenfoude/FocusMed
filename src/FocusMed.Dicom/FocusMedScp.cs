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
        _logger.LogInformation("Association request received from {CallingAET} to {CalledAET}", association.CallingAE, association.CalledAE);

        if (_networkingOptions.Value.EnforceAeWhitelist)
        {
            var callingAe = association.CallingAE;
            var remoteIp = association.RemoteHost;

            var allowed = _networkingOptions.Value.AllowedCallingAETitles
                .Any(ae => ae.AETitle == callingAe && ae.IPAddress == remoteIp);

            if (!allowed)
            {
                _logger.LogWarning("Association REJECTED - {CallingAET} from {RemoteIp} not on whitelist", callingAe, remoteIp);
                await WriteAuditEntryAsync(association, remoteIp, AssociationOutcome.Rejected);
                SendAssociationRejectAsync(
                    DicomRejectResult.Permanent,
                    DicomRejectSource.ServiceUser,
                    DicomRejectReason.NoReasonGiven);
                return;
            }
        }

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
                || syntax == DicomUID.ModalityWorklistInformationModelFind
                || syntax == DicomUID.Parse("1.2.840.10008.1.20.1"))
            {
                var requestedSyntaxes = pc.GetTransferSyntaxes();
                var syntaxesToAccept = requestedSyntaxes.Intersect(_acceptedTransferSyntaxes).ToArray();

                if (syntaxesToAccept.Any())
                {
                    pc.AcceptTransferSyntaxes(syntaxesToAccept);
                    _logger.LogInformation("Accepted Abstract Syntax: {AbstractSyntax} with Transfer Syntaxes: {TransferSyntaxes}", syntax.Name, string.Join(", ", syntaxesToAccept.Select(s => s.UID.Name)));
                }
                else
                {
                    pc.SetResult(DicomPresentationContextResult.RejectTransferSyntaxesNotSupported);
                    _logger.LogWarning("Rejected Abstract Syntax: {AbstractSyntax} due to unsupported Transfer Syntaxes.", syntax.Name);
                }
            }
            else
            {
                pc.SetResult(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported);
                _logger.LogWarning("Rejected Abstract Syntax: {AbstractSyntax} (Not Supported)", syntax.Name);
            }
        }

        var remoteIpForAudit = association.RemoteHost;
        await WriteAuditEntryAsync(association, remoteIpForAudit, AssociationOutcome.Success);
        await SendAssociationAcceptAsync(association);
    }

    public Task OnReceiveAssociationReleaseRequestAsync()
    {
        var duration = (DateTime.UtcNow - _associationStartTime).TotalMilliseconds;
        _logger.LogInformation("Association release request received. Duration: {DurationMs}ms. Association closing normally.", (int)duration);
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

        var level = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.QueryRetrieveLevel, string.Empty);

        if (level == string.Empty || request.Dataset.Contains(DicomTag.ScheduledProcedureStepSequence))
        {
            var patientName = request.Dataset.GetSingleValueOrDefault(DicomTag.PatientName, string.Empty);
            var patientId = request.Dataset.GetSingleValueOrDefault(DicomTag.PatientID, string.Empty);

            var query = db.WorklistEntries.AsQueryable();
            if (!string.IsNullOrWhiteSpace(patientId)) query = query.Where(w => w.PatientId.Contains(patientId));
            if (!string.IsNullOrWhiteSpace(patientName) && patientName != "*") query = query.Where(w => w.PatientName.Contains(patientName.Replace("*", "")));

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

                yield return new DicomCFindResponse(request, DicomStatus.Pending) { Dataset = responseDataset };
            }

            await db.SaveChangesAsync();
            yield return new DicomCFindResponse(request, DicomStatus.Success);
            yield break;
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
            if (sopUid == "1.2.840.10008.1.20.1.1")
            {
                var transactionUid = request.Dataset.GetSingleValueOrDefault(DicomTag.TransactionUID, string.Empty);
                var referencedSopSeq = request.Dataset.GetSequence(DicomTag.ReferencedSOPSequence);

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
                    Status = "Pending"
                };

                db.StorageCommitmentJobs.Add(job);
                await db.SaveChangesAsync();

                _logger.LogInformation("Accepted Storage Commitment request for Transaction UID {TransactionUID}", transactionUid);
                return new DicomNActionResponse(request, DicomStatus.Success);
            }
            else
            {
                var filmBox = await db.FilmBoxes.FirstOrDefaultAsync(f => f.SopInstanceUid == sopUid);
                if (filmBox != null)
                {
                    var printJob = await db.PrintJobs.FirstOrDefaultAsync(p => p.Id == filmBox.PrintJobId);
                    if (printJob != null)
                    {
                        printJob.Status = PrintStatus.Printing;
                        await db.SaveChangesAsync();

                        var printScu = scope.ServiceProvider.GetService<IPrintScuService>();
                        var matchedPrinter = SelectPrinterForFilmBox(printScu);

                        if (matchedPrinter is null)
                        {
                            printJob.Status = PrintStatus.Failed;
                            await db.SaveChangesAsync();

                            _logger.LogError(
                                "Print job {PrintJobId} has no matching enabled FilmPrinter configured. " +
                                "Job will not be printed. Configure a FilmPrinter in appsettings.json.",
                                printJob.Id);
                            return new DicomNActionResponse(request, DicomStatus.ProcessingFailure);
                        }

                        var ct = CancellationToken.None;
                        _ = printScu!.SendPrintJobAsync(printJob.Id, matchedPrinter, ct);
                    }
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

    private FilmPrinterConfig? SelectPrinterForFilmBox(IPrintScuService? printScu)
    {
        if (printScu is null) return null;

        var printers = _networkingOptions.Value.FilmPrinters;
        if (printers.Count == 0) return null;

        var enabled = printers.Where(p => p.Enabled).ToList();
        if (enabled.Count == 0) return null;

        return enabled.FirstOrDefault();
    }
}
