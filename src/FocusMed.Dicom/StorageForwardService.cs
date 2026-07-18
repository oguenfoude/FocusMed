using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using FocusMed.Dicom.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FocusMed.Dicom;

public sealed class StorageForwardService : BackgroundService
{
    private readonly ILogger<StorageForwardService> _logger;
    private readonly IStorageForwardQueue _queue;
    private readonly IOptions<DicomNetworkingOptions> _networkingOptions;

    public StorageForwardService(
        ILogger<StorageForwardService> logger,
        IStorageForwardQueue queue,
        IOptions<DicomNetworkingOptions> networkingOptions)
    {
        _logger = logger;
        _queue = queue;
        _networkingOptions = networkingOptions;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var request in _queue.ReadAllAsync(stoppingToken))
            {
                await ForwardToAllTargetsAsync(request, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Forward cancelled during shutdown.");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        var pending = _queue.PendingCount;
        if (pending > 0)
        {
            _logger.LogWarning(
                "StorageForwardService stopping with {PendingCount} items still in queue. Draining...",
                pending);
        }

        _queue.Complete();

        await base.StopAsync(cancellationToken);
    }

    private async Task ForwardToAllTargetsAsync(StorageForwardRequest request, CancellationToken ct)
    {
        var targets = _networkingOptions.Value.StorageForwardTargets
            .Where(t => t.Enabled && !string.IsNullOrWhiteSpace(t.Ip) && t.Port > 0)
            .ToList();

        if (targets.Count == 0)
        {
            _logger.LogDebug("No enabled StorageForwardTargets configured. Skipping forward of {SopUid}.", request.SopInstanceUid);
            return;
        }

        if (!File.Exists(request.FilePath))
        {
            _logger.LogWarning("Cannot forward {SopUid}: file missing at {FilePath}", request.SopInstanceUid, request.FilePath);
            return;
        }

        foreach (var target in targets)
        {
            var file = await DicomFile.OpenAsync(request.FilePath);
            await ForwardToTargetAsync(file, target, ct);
        }
    }

    private async Task ForwardToTargetAsync(DicomFile file, StorageForwardTarget target, CancellationToken ct)
    {
        try
        {
            var callingAe = string.IsNullOrWhiteSpace(target.ScuAe) ? _networkingOptions.Value.AETitle : target.ScuAe;
            var client = DicomClientFactory.Create(target.Ip, target.Port, false, callingAe, target.AeTitle);

            var cstore = new DicomCStoreRequest(file);

            await client.AddRequestAsync(cstore);
            await client.SendAsync(ct);

            _logger.LogInformation("Forwarded: {SopUid} -> {TargetName}", file.Dataset.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, string.Empty), target.Name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Forward failed: {TargetName}", target.Name);
        }
    }
}
