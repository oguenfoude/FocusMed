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
/// (dedup on re-send, one study per distinct StudyInstanceUID even for the same
/// patient, same-UID multi-series stays in one study).
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

        var first = await svc.StoreFileOnlyAsync(BuildFile("DEDUP001", "1.2.826.0.1.3680043.10.999.2", "1.2.826.0.1.3680043.10.999.22", sop));
        var second = await svc.StoreFileOnlyAsync(BuildFile("DEDUP001", "1.2.826.0.1.3680043.10.999.2", "1.2.826.0.1.3680043.10.999.22", sop));
        Assert.Equal(StoreOutcome.Stored, first);
        Assert.Equal(StoreOutcome.DedupedSameStudy, second);

        using var db = _infra.CreateDbContext();
        Assert.Equal(1, await db.DicomImages.CountAsync());
        Assert.Equal(1, await db.Studies.CountAsync());
    }

    [Fact]
    public async Task Ingest_ResendAfterComplete_BumpsTimestampKeepsStatus()
    {
        var scopes = _infra.CreateScopeFactory();
        var svc = BuildService(scopes);
        const string studyUid = "1.2.826.0.1.3680043.10.999.21";
        const string sop = "1.2.826.0.1.3680043.10.999.211";

        var first = await svc.StoreFileOnlyAsync(BuildFile("DEDUPCMP001", studyUid, "1.2.826.0.1.3680043.10.999.212", sop));
        Assert.Equal(StoreOutcome.Stored, first);

        // Simulate the completion loop marking the study Complete.
        DateTime before;
        using (var db = _infra.CreateDbContext())
        {
            var study = await db.Studies.SingleAsync();
            study.Status = StudyStatus.Complete;
            study.LastUpdatedAt = DateTime.UtcNow.AddMinutes(-10);
            await db.SaveChangesAsync();
            before = study.LastUpdatedAt;
        }

        // Identical retry: no new row, timestamp bumped, Complete NOT reopened.
        var second = await svc.StoreFileOnlyAsync(BuildFile("DEDUPCMP001", studyUid, "1.2.826.0.1.3680043.10.999.212", sop));
        Assert.Equal(StoreOutcome.DedupedSameStudy, second);

        using (var db = _infra.CreateDbContext())
        {
            Assert.Equal(1, await db.DicomImages.CountAsync());
            var study = await db.Studies.SingleAsync();
            Assert.Equal(StudyStatus.Complete, study.Status);
            Assert.True(study.LastUpdatedAt > before, "dedupe must bump LastUpdatedAt as proof of receipt");
        }
    }

    [Fact]
    public async Task Ingest_DifferentStudyUidSamePatient_CreatesTwoStudies()
    {
        var scopes = _infra.CreateScopeFactory();
        var svc = BuildService(scopes);

        // Two different exams (different StudyInstanceUIDs) for the same patient on the
        // same day must NEVER merge — each keeps its own study row.
        await svc.StoreFileOnlyAsync(BuildFile("SEPARATE001", "1.2.826.0.1.3680043.10.999.3", "1.2.826.0.1.3680043.10.999.33", "1.2.826.0.1.3680043.10.999.333", "CT"));
        await svc.StoreFileOnlyAsync(BuildFile("SEPARATE001", "1.2.826.0.1.3680043.10.999.4", "1.2.826.0.1.3680043.10.999.44", "1.2.826.0.1.3680043.10.999.444", "MR"));

        using var db = _infra.CreateDbContext();
        Assert.Equal(2, await db.Studies.CountAsync());
        var uids = await db.Studies.Select(s => s.StudyInstanceUid).OrderBy(u => u).ToListAsync();
        Assert.Contains("1.2.826.0.1.3680043.10.999.3", uids);
        Assert.Contains("1.2.826.0.1.3680043.10.999.4", uids);
        Assert.Equal(2, await db.Series.CountAsync());
    }

    [Fact]
    public async Task Ingest_SameStudyUidTwoSeries_OneStudy()
    {
        var scopes = _infra.CreateScopeFactory();
        var svc = BuildService(scopes);

        // Same exam arriving in pieces (two series, e.g. two associations) stays one study.
        await svc.StoreFileOnlyAsync(BuildFile("PIECES001", "1.2.826.0.1.3680043.10.999.5", "1.2.826.0.1.3680043.10.999.55", "1.2.826.0.1.3680043.10.999.555", "CT"));
        await svc.StoreFileOnlyAsync(BuildFile("PIECES001", "1.2.826.0.1.3680043.10.999.5", "1.2.826.0.1.3680043.10.999.56", "1.2.826.0.1.3680043.10.999.556", "CT"));

        using var db = _infra.CreateDbContext();
        Assert.Equal(1, await db.Studies.CountAsync());
        var study = await db.Studies.Include(s => s.Series).SingleAsync();
        Assert.Equal(2, study.Series.Count);
    }

    [Fact]
    public async Task Print_TwoNSetsSameJob_OneStudy()
    {
        var scopes = _infra.CreateScopeFactory();
        var svc = BuildService(scopes);

        // Simulate the N-SET handler: first N-SET creates the study and links the
        // PrintJob; the second N-SET of the same job must rejoin it, not split.
        int printJobId;
        using (var db = _infra.CreateDbContext())
        {
            var job = new PrintJob { SopInstanceUid = "1.2.826.0.1.3680043.10.999.900", Status = PrintStatus.Pending };
            db.PrintJobs.Add(job);
            await db.SaveChangesAsync();
            printJobId = job.Id;
        }

        var first = await svc.IngestPrintImageAsync(BuildPrintDataset("PRINTJOB001"), "PRINTJOB001", "PRINT^JOB", "PRINTAE", "127.0.0.1", printJobId);
        Assert.NotNull(first);
        var firstStudyUid = first.Dataset.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, string.Empty);

        // Link the job like the N-SET handler does after ingest.
        using (var db = _infra.CreateDbContext())
        {
            var study = await db.Studies.SingleAsync(s => s.StudyInstanceUid == firstStudyUid);
            var job = await db.PrintJobs.SingleAsync(p => p.Id == printJobId);
            job.StudyId = study.Id;
            job.PatientId = study.PatientId;
            await db.SaveChangesAsync();
        }

        var second = await svc.IngestPrintImageAsync(BuildPrintDataset("PRINTJOB001"), "PRINTJOB001", "PRINT^JOB", "PRINTAE", "127.0.0.1", printJobId);
        Assert.NotNull(second);
        Assert.Equal(firstStudyUid, second.Dataset.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, string.Empty));

        using (var db = _infra.CreateDbContext())
        {
            Assert.Equal(1, await db.Studies.CountAsync());
            Assert.Equal(2, await db.Series.CountAsync());
            Assert.Equal(2, await db.DicomImages.CountAsync(i => i.Source == "PRINT"));
        }
    }

    [Fact]
    public async Task Print_ExplicitStudyUid_MergesIntoCStoreStudy()
    {
        var scopes = _infra.CreateScopeFactory();
        var svc = BuildService(scopes);

        const string cstoreUid = "1.2.826.0.1.3680043.10.999.6";
        await svc.StoreFileOnlyAsync(BuildFile("PRINTLINK001", cstoreUid, "1.2.826.0.1.3680043.10.999.66", "1.2.826.0.1.3680043.10.999.666", "CT"));

        // Print carrying the C-STORE study's UID (OriginalImageSequence) joins it.
        var printDs = BuildPrintDataset("PRINTLINK001");
        printDs.AddOrUpdate(DicomTag.StudyInstanceUID, cstoreUid);
        var stored = await svc.IngestPrintImageAsync(printDs, "PRINTLINK001", "PRINT^LINK", "PRINTAE", "127.0.0.1");
        Assert.NotNull(stored);
        Assert.Equal(cstoreUid, stored.Dataset.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, string.Empty));

        using var db = _infra.CreateDbContext();
        Assert.Equal(1, await db.Studies.CountAsync());
    }

    [Fact]
    public async Task Print_NoUidNoJob_CreatesOwnStudy()
    {
        var scopes = _infra.CreateScopeFactory();
        var svc = BuildService(scopes);

        // A print with no UID link and no job must NOT merge into the patient's C-STORE study.
        await svc.StoreFileOnlyAsync(BuildFile("PRINTSEP001", "1.2.826.0.1.3680043.10.999.7", "1.2.826.0.1.3680043.10.999.77", "1.2.826.0.1.3680043.10.999.777", "CT"));
        var stored = await svc.IngestPrintImageAsync(BuildPrintDataset("PRINTSEP001"), "PRINTSEP001", "PRINT^SEP", "PRINTAE", "127.0.0.1");
        Assert.NotNull(stored);

        using var db = _infra.CreateDbContext();
        Assert.Equal(2, await db.Studies.CountAsync());
    }

    private static DicomDataset BuildPrintDataset(string patientId)
    {
        return new DicomDataset
        {
            { DicomTag.PatientID, patientId },
            { DicomTag.PatientName, "PRINT^TEST" },
            { DicomTag.SamplesPerPixel, (ushort)1 },
            { DicomTag.PhotometricInterpretation, "MONOCHROME2" },
            { DicomTag.Rows, (ushort)8 },
            { DicomTag.Columns, (ushort)8 },
            { DicomTag.BitsAllocated, (ushort)8 }
        };
    }

    public void Dispose() => _infra.Dispose();
}
