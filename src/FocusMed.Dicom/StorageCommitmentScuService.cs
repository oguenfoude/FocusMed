using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using FocusMed.Data;
using FocusMed.Data.Entities;
using FocusMed.Dicom.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FocusMed.Dicom;

public class StorageCommitmentScuService : BackgroundService
{
    private const string StorageCommitmentSopClassUid = "1.2.840.10008.1.20.1";
    private const string StorageCommitmentSopInstanceUid = "1.2.840.10008.1.20.1.1";

    private readonly ILogger<StorageCommitmentScuService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<DicomNetworkingOptions> _networkingOptions;
    private readonly string _ourAet;

    public StorageCommitmentScuService(ILogger<StorageCommitmentScuService> logger, IServiceScopeFactory scopeFactory, IOptions<DicomNetworkingOptions> networkingOptions)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _networkingOptions = networkingOptions;
        _ourAet = networkingOptions.Value.AETitle;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _unmappedWarnedAt = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingJobsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error occurred processing storage commitment jobs.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ProcessPendingJobsAsync(CancellationToken stoppingToken)
    {
        List<FocusMed.Data.Entities.StorageCommitmentJob> pendingJobs;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FocusMedDbContext>();
            pendingJobs = await db.StorageCommitmentJobs
                .Where(j => j.Status == StorageCommitmentStatus.Pending)
                .ToListAsync(stoppingToken);
        }

        foreach (var job in pendingJobs)
        {
            try
            {
                // Back off unmapped AETs: no mapping means every attempt fails, so
                // skip the per-job image queries and warn at most once per 10 min.
                if (!_networkingOptions.Value.StorageCommitmentScuMapping.ContainsKey(job.CallingAet))
                {
                    if (!_unmappedWarnedAt.TryGetValue(job.CallingAet, out var lastWarned) || DateTime.UtcNow - lastWarned > TimeSpan.FromMinutes(10))
                    {
                        _logger.LogWarning("No StorageCommitmentScuMapping for AET {Aet}, leaving jobs Pending (throttled)", job.CallingAet);
                        _unmappedWarnedAt[job.CallingAet] = DateTime.UtcNow;
                    }
                    continue;
                }

                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FocusMedDbContext>();

                var uids = job.RequestedSopInstanceUids.Split(',', StringSplitOptions.RemoveEmptyEntries);
                var sopClassMap = await db.DicomImages
                    .Where(i => uids.Contains(i.SopInstanceUid))
                    .ToDictionaryAsync(i => i.SopInstanceUid, i => i.SopClassUid, stoppingToken);

                var archivedImagesCount = sopClassMap.Count;

                if (archivedImagesCount == uids.Length)
                {
                    var sent = await SendNEventReportAsync(job, sopClassMap, stoppingToken);
                    if (sent)
                    {
                        // Re-query tracked: the list above came from a disposed scope;
                        // Update(detached) would clobber concurrent column writes.
                        var tracked = await db.StorageCommitmentJobs.FirstOrDefaultAsync(j => j.Id == job.Id, stoppingToken);
                        if (tracked == null || tracked.Status != StorageCommitmentStatus.Pending) continue;
                        tracked.Status = StorageCommitmentStatus.Completed;
                        tracked.CompletedAt = DateTime.UtcNow;
                        await db.SaveChangesAsync(stoppingToken);
                    }
                    else
                    {
                        _logger.LogWarning("N-EVENT-REPORT not sent (no mapping for AET {Aet}), leaving job Pending", job.CallingAet);
                    }
                }
                else if (job.CreatedAt < DateTime.UtcNow.AddHours(-1))
                {
                    await SendNEventReportFailedAsync(job, sopClassMap, stoppingToken);
                    var tracked = await db.StorageCommitmentJobs.FirstOrDefaultAsync(j => j.Id == job.Id, stoppingToken);
                    if (tracked == null || tracked.Status != StorageCommitmentStatus.Pending) continue;
                    tracked.Status = StorageCommitmentStatus.Failed;
                    tracked.CompletedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to process StorageCommitmentJob {JobId}", job.Id);
            }
        }
    }

    private async Task<bool> SendNEventReportAsync(FocusMed.Data.Entities.StorageCommitmentJob job, Dictionary<string, string> sopClassMap, CancellationToken ct = default)
    {
        if (!_networkingOptions.Value.StorageCommitmentScuMapping.TryGetValue(job.CallingAet, out var endpoint))
        {
            _logger.LogWarning("No mapping for AET: {Aet}", job.CallingAet);
            return false;
        }

        var client = DicomClientFactory.Create(endpoint.Ip, endpoint.Port, false, _ourAet, job.CallingAet);
        var dataset = new DicomDataset
        {
            { DicomTag.TransactionUID, job.TransactionUid }
        };

        var seq = new DicomSequence(DicomTag.ReferencedSOPSequence);
        foreach (var uid in job.RequestedSopInstanceUids.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var referencedSopClass = DicomUID.SecondaryCaptureImageStorage;
            if (sopClassMap.TryGetValue(uid, out var sopClassUid) && !string.IsNullOrEmpty(sopClassUid))
            {
                try { referencedSopClass = DicomUID.Parse(sopClassUid); }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Unknown SOP Class {SopClassUid} for {Uid}", sopClassUid, uid);
                }
            }
            else
            {
                _logger.LogWarning("SOP Class not in DB for {Uid}", uid);
            }

            var item = new DicomDataset
            {
                { DicomTag.ReferencedSOPClassUID, referencedSopClass.UID },
                { DicomTag.ReferencedSOPInstanceUID, uid }
            };
            seq.Items.Add(item);
        }
        dataset.Add(seq);

        var storageCommitmentSopClass = DicomUID.Parse(StorageCommitmentSopClassUid);
        var storageCommitmentSopInstance = DicomUID.Parse(StorageCommitmentSopInstanceUid);
        var request = new DicomNEventReportRequest(storageCommitmentSopClass, storageCommitmentSopInstance, 1)
        {
            Dataset = dataset
        };

        await client.AddRequestAsync(request);
        // A hung peer must not stall the 10s loop forever: 30s cap, shutdown-aware.
        // (The installed fo-dicom exposes no cancellable SendAsync overload.)
        var sendTask = client.SendAsync();
        Task finished;
        try
        {
            finished = await Task.WhenAny(sendTask, Task.Delay(TimeSpan.FromSeconds(30), ct));
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        if (finished != sendTask)
        {
            _logger.LogWarning("N-EVENT-REPORT send timed out for Tx={TransactionUid}", job.TransactionUid);
            return false;
        }
        await sendTask;
        _logger.LogInformation("N-EVENT-REPORT sent: Tx={TransactionUid}", job.TransactionUid);
        return true;
    }

    private async Task SendNEventReportFailedAsync(FocusMed.Data.Entities.StorageCommitmentJob job, Dictionary<string, string> sopClassMap, CancellationToken ct = default)
    {
        if (!_networkingOptions.Value.StorageCommitmentScuMapping.TryGetValue(job.CallingAet, out var endpoint))
        {
            _logger.LogWarning("N-EVENT-REPORT (failed) skipped: no mapping for AET {Aet}", job.CallingAet);
            return;
        }

        var client = DicomClientFactory.Create(endpoint.Ip, endpoint.Port, false, _ourAet, job.CallingAet);
        var dataset = new DicomDataset
        {
            { DicomTag.TransactionUID, job.TransactionUid }
        };

        var seq = new DicomSequence(DicomTag.FailedSOPSequence);
        foreach (var uid in job.RequestedSopInstanceUids.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var referencedSopClass = DicomUID.SecondaryCaptureImageStorage;
            if (sopClassMap.TryGetValue(uid, out var sopClassUid) && !string.IsNullOrEmpty(sopClassUid))
            {
                try { referencedSopClass = DicomUID.Parse(sopClassUid); }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Unknown SOP Class {SopClassUid} for {Uid}", sopClassUid, uid);
                }
            }

            var item = new DicomDataset
            {
                { DicomTag.ReferencedSOPClassUID, referencedSopClass.UID },
                { DicomTag.ReferencedSOPInstanceUID, uid },
                { DicomTag.FailureReason, (ushort)0x0110 }
            };
            seq.Items.Add(item);
        }
        dataset.Add(seq);

        var storageCommitmentSopClass = DicomUID.Parse(StorageCommitmentSopClassUid);
        var storageCommitmentSopInstance = DicomUID.Parse(StorageCommitmentSopInstanceUid);
        var request = new DicomNEventReportRequest(storageCommitmentSopClass, storageCommitmentSopInstance, 2)
        {
            Dataset = dataset
        };

        await client.AddRequestAsync(request);
        var sendTask = client.SendAsync();
        Task finished;
        try
        {
            finished = await Task.WhenAny(sendTask, Task.Delay(TimeSpan.FromSeconds(30), ct));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (finished != sendTask)
        {
            _logger.LogWarning("N-EVENT-REPORT (failed) send timed out for Tx={TransactionUid}", job.TransactionUid);
            return;
        }
        await sendTask;
    }
}
