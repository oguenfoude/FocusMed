using FellowOakDicom;
using FocusMed.Data.Entities;
using FocusMed.Dicom;
using FocusMed.Dicom.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FocusMed.Tests;

/// <summary>
/// End-to-end ingest: minimal DICOM dataset -> StoreFileOnlyAsync ->
/// Study/Series/DicomImage rows + .dcm file on disk. Covers the receive path
/// (dedup on re-send, cross-modality merge for the same patient).
/// </summary>
public sealed class UpsertIngestTests : IDisposable
{
    private readonly TestInfra _infra = new();

    private static DicomFile BuildFile(string patientId, string studyUid, string seriesUid, string sopUid, string modality = "CT")
    {
        var ds = new DicomDataset
        {
            { DicomTag.PatientID, patientId },
            { DicomTag.PatientName, "TEST^PATIENT" },
            { DicomTag.StudyInstanceUID, studyUid },
            { DicomTag.SeriesInstanceUID, seriesUid },
            { DicomTag.SOPInstanceUID, sopUid },
            { DicomTag.SOPClassUID, DicomUID.CTImageStorage },
            { DicomTag.Modality, modality },
            { DicomTag.StudyDate, new DateTime(2026, 9, 8) }
        };
        return new DicomFile(ds);
    }

    private DicomUpsertService BuildService(IServiceScopeFactory scopes) => new(
        scopes,
        TestInfra.NullLogger<DicomUpsertService>(),
        new StorageForwardQueue(),
        new StudyNotificationService(),
        Options.Create(new DicomNetworkingOptions { PrintMergeWindowSeconds = 300 }));

    [Fact]
    public async Task Ingest_CreatesStudySeriesImageAndFile()
    {
        var scopes = _infra.CreateScopeFactory();
        var svc = BuildService(scopes);

        await svc.StoreFileOnlyAsync(
            BuildFile("INGEST001", "1.2.826.0.1.3680043.10.999.1", "1.2.826.0.1.3680043.10.999.11", "1.2.826.0.1.3680043.10.999.111"),
            callingAeTitle: "TESTAE", remoteIp: "127.0.0.1");

        using var db = _infra.CreateDbContext();
        var study = await db.Studies.Include(s => s.Patient).Include(s => s.Series).ThenInclude(s => s.Images).AsSplitQuery().SingleAsync();
        Assert.Equal("INGEST001", study.Patient.PatientId);
        Assert.Equal(StudyStatus.Receiving, study.Status);
        Assert.Equal("TESTAE", study.CallingAeTitle);
        var image = study.Series.Single().Images.Single();
        Assert.True(File.Exists(image.FilePath), "ingested .dcm must exist on disk");
    }

    [Fact]
    public async Task Ingest_SameSopUidTwice_Dedupes()
    {
        var scopes = _infra.CreateScopeFactory();
        var svc = BuildService(scopes);
        const string sop = "1.2.826.0.1.3680043.10.999.222";

        await svc.StoreFileOnlyAsync(BuildFile("DEDUP001", "1.2.826.0.1.3680043.10.999.2", "1.2.826.0.1.3680043.10.999.22", sop));
        await svc.StoreFileOnlyAsync(BuildFile("DEDUP001", "1.2.826.0.1.3680043.10.999.2", "1.2.826.0.1.3680043.10.999.22", sop));

        using var db = _infra.CreateDbContext();
        Assert.Equal(1, await db.DicomImages.CountAsync());
        Assert.Equal(1, await db.Studies.CountAsync());
    }

    [Fact]
    public async Task Ingest_SecondModalitySamePatient_MergesIntoOneStudy()
    {
        var scopes = _infra.CreateScopeFactory();
        var svc = BuildService(scopes);

        await svc.StoreFileOnlyAsync(BuildFile("MERGE001", "1.2.826.0.1.3680043.10.999.3", "1.2.826.0.1.3680043.10.999.33", "1.2.826.0.1.3680043.10.999.333", "CT"));
        await svc.StoreFileOnlyAsync(BuildFile("MERGE001", "1.2.826.0.1.3680043.10.999.4", "1.2.826.0.1.3680043.10.999.44", "1.2.826.0.1.3680043.10.999.444", "MR"));

        using var db = _infra.CreateDbContext();
        Assert.Equal(1, await db.Studies.CountAsync());
        var study = await db.Studies.Include(s => s.Series).SingleAsync();
        Assert.Equal(2, study.Series.Count);
    }

    public void Dispose() => _infra.Dispose();
}
