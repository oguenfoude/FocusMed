using FellowOakDicom;
using FellowOakDicom.Network;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FocusMed.Dicom;

public class CStoreScp : DicomService, IDicomServiceProvider, IDicomCStoreProvider
{
    private readonly DicomUpsertService _upsertService;
    private readonly ILogger<CStoreScp> _logger;

    public CStoreScp(INetworkStream stream, System.Text.Encoding fallbackEncoding, ILogger<CStoreScp> logger, DicomServiceDependencies dependencies, DicomUpsertService upsertService)
        : base(stream, fallbackEncoding, logger, dependencies)
    {
        _upsertService = upsertService;
        _logger = logger;
    }

    public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
    {
        foreach (var pc in association.PresentationContexts)
        {
            if (pc.AbstractSyntax == DicomUID.Verification)
            {
                pc.AcceptTransferSyntaxes(pc.GetTransferSyntaxes().ToArray());
            }
            else if (pc.AbstractSyntax.StorageCategory != DicomStorageCategory.None)
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
}
