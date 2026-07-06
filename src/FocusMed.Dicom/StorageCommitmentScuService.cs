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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingJobsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred processing storage commitment jobs.");
            }

            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }

    private async Task ProcessPendingJobsAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FocusMedDbContext>();

        var pendingJobs = await db.StorageCommitmentJobs
            .Where(j => j.Status == StorageCommitmentStatus.Pending)
            .ToListAsync(stoppingToken);

        foreach (var job in pendingJobs)
        {
            var uids = job.RequestedSopInstanceUids.Split(',', StringSplitOptions.RemoveEmptyEntries);
            var archivedImagesCount = await db.DicomImages
                .CountAsync(i => uids.Contains(i.SopInstanceUid), stoppingToken);

            var sopClassMap = await db.DicomImages
                .Where(i => uids.Contains(i.SopInstanceUid))
                .ToDictionaryAsync(i => i.SopInstanceUid, i => i.SopClassUid, stoppingToken);

            if (archivedImagesCount == uids.Length)
            {
                await SendNEventReportAsync(job, sopClassMap);
                job.Status = StorageCommitmentStatus.Completed;
                job.CompletedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(stoppingToken);
            }
            else if (job.CreatedAt < DateTime.UtcNow.AddHours(-1))
            {
                await SendNEventReportFailedAsync(job, sopClassMap);
                job.Status = StorageCommitmentStatus.Failed;
                job.CompletedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(stoppingToken);
            }
        }
    }

    private async Task SendNEventReportAsync(FocusMed.Data.Entities.StorageCommitmentJob job, Dictionary<string, string> sopClassMap)
    {
        if (!_networkingOptions.Value.StorageCommitmentScuMapping.TryGetValue(job.CallingAet, out var endpoint))
        {
            _logger.LogWarning("Cannot send N-EVENT-REPORT. No mapping found for AET: {Aet}", job.CallingAet);
            return;
        }

        var client = DicomClientFactory.Create(endpoint.Ip, endpoint.Port, false, job.CallingAet, _ourAet);
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
                catch
                {
                    _logger.LogWarning(
                        "SopClassUid '{SopClassUid}' not recognized for instance {SopInstanceUid}. Using SecondaryCaptureImageStorage as fallback.",
                        sopClassUid, uid);
                }
            }
            else
            {
                _logger.LogWarning(
                    "SopClassUid not found in DB for instance {SopInstanceUid}. Using SecondaryCaptureImageStorage as fallback.",
                    uid);
            }

            var item = new DicomDataset
            {
                { DicomTag.ReferencedSOPClassUID, referencedSopClass.UID },
                { DicomTag.ReferencedSOPInstanceUID, uid }
            };
            seq.Items.Add(item);
        }
        dataset.Add(seq);

        var storageCommitmentSopClass = DicomUID.Parse("1.2.840.10008.1.20.1");
        var storageCommitmentSopInstance = DicomUID.Parse("1.2.840.10008.1.20.1.1");
        var request = new DicomNEventReportRequest(storageCommitmentSopClass, storageCommitmentSopInstance, 1)
        {
            Dataset = dataset
        };

        await client.AddRequestAsync(request);
        await client.SendAsync();
        _logger.LogInformation("Successfully sent N-EVENT-REPORT for transaction {TransactionUid} to {Ip}:{Port}", job.TransactionUid, endpoint.Ip, endpoint.Port);
    }

    private async Task SendNEventReportFailedAsync(FocusMed.Data.Entities.StorageCommitmentJob job, Dictionary<string, string> sopClassMap)
    {
        if (!_networkingOptions.Value.StorageCommitmentScuMapping.TryGetValue(job.CallingAet, out var endpoint))
            return;

        var client = DicomClientFactory.Create(endpoint.Ip, endpoint.Port, false, job.CallingAet, _ourAet);
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
                catch
                {
                    _logger.LogWarning(
                        "SopClassUid '{SopClassUid}' not recognized for instance {SopInstanceUid}. Using SecondaryCaptureImageStorage as fallback.",
                        sopClassUid, uid);
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

        var storageCommitmentSopClass = DicomUID.Parse("1.2.840.10008.1.20.1");
        var storageCommitmentSopInstance = DicomUID.Parse("1.2.840.10008.1.20.1.1");
        var request = new DicomNEventReportRequest(storageCommitmentSopClass, storageCommitmentSopInstance, 2)
        {
            Dataset = dataset
        };

        await client.AddRequestAsync(request);
        await client.SendAsync();
    }
}
