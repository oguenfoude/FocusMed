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
    public async Task Ingest_SameSopResentToOpenStudy_NoDuplicateImage()
    {
        var scopes = _infra.CreateScopeFactory();
        var svc = BuildService(scopes);
        const string studyUid = "1.2.826.0.1.3680043.10.999.31";
        const string sop = "1.2.826.0.1.3680043.10.999.311";

        // First send: 1 image in the open study.
        var first = await svc.StoreFileOnlyAsync(BuildFile("NODUP001", studyUid, "1.2.826.0.1.3680043.10.999.312", sop));
        Assert.Equal(StoreOutcome.Stored, first);

        // Same SOP re-sent while study is still open: no new image row.
        var second = await svc.StoreFileOnlyAsync(BuildFile("NODUP001", studyUid, "1.2.826.0.1.3680043.10.999.312", sop));
        Assert.Equal(StoreOutcome.Stored, second);

        using var db = _infra.CreateDbContext();
        Assert.Equal(1, await db.Studies.CountAsync());
        Assert.Equal(1, await db.DicomImages.CountAsync());
    }

    [Fact]
    public async Task Ingest_ResendSameDay_Appends_NextDayOrComplete_Forks()
    {
        var scopes = _infra.CreateScopeFactory();
        var svc = BuildService(scopes);
        const string studyUid = "1.2.826.0.1.3680043.10.999.21";
        const string seriesUid = "1.2.826.0.1.3680043.10.999.212";

        // Day 1, first send: 1 study, 1 image.
        var first = await svc.StoreFileOnlyAsync(BuildFile("RESEND001", studyUid, seriesUid, "1.2.826.0.1.3680043.10.999.211"));
        Assert.Equal(StoreOutcome.Stored, first);

        // Same UTC day, open study, new SOP → appends (no fork).
        var second = await svc.StoreFileOnlyAsync(BuildFile("RESEND001", studyUid, seriesUid, "1.2.826.0.1.3680043.10.999.212"));
        Assert.Equal(StoreOutcome.Stored, second);

        using (var db = _infra.CreateDbContext())
        {
            Assert.Equal(1, await db.Studies.CountAsync());
            Assert.Equal(2, await db.DicomImages.CountAsync());
        }

        // Next UTC day (backdate the seeded row) → forks a new study.
        using (var db = _infra.CreateDbContext())
        {
            var study = await db.Studies.SingleAsync();
            study.CreatedAt = DateTime.UtcNow.AddDays(-1);
            study.LastUpdatedAt = DateTime.UtcNow.AddDays(-1);
            await db.SaveChangesAsync();
        }

        var third = await svc.StoreFileOnlyAsync(BuildFile("RESEND001", studyUid, seriesUid, "1.2.826.0.1.3680043.10.999.213"));
        Assert.Equal(StoreOutcome.Stored, third);

        using (var db = _infra.CreateDbContext())
        {
            Assert.Equal(2, await db.Studies.CountAsync());
            Assert.Equal(3, await db.DicomImages.CountAsync());
        }

        // Same-day re-send onto a Complete study → still forks (never reopens).
        using (var db = _infra.CreateDbContext())
        {
            var newest = await db.Studies.OrderByDescending(s => s.Id).FirstAsync();
            newest.Status = StudyStatus.Complete;
            await db.SaveChangesAsync();
        }

        var fourth = await svc.StoreFileOnlyAsync(BuildFile("RESEND001", studyUid, seriesUid, "1.2.826.0.1.3680043.10.999.214"));
        Assert.Equal(StoreOutcome.Stored, fourth);

        using (var db = _infra.CreateDbContext())
        {
            Assert.Equal(3, await db.Studies.CountAsync());
            Assert.Equal(4, await db.DicomImages.CountAsync());
        }
    }

    [Fact]
    public async Task Ingest_DifferentStudyUidSamePatient_CreatesTwoStudies()
    {
        var scopes = _infra.CreateScopeFactory();
        var svc = BuildService(scopes);

        // Two different exams (different StudyInstanceUIDs) for the same patient → separate studies.
        await svc.StoreFileOnlyAsync(BuildFile("SEPARATE001", "1.2.826.0.1.3680043.10.999.3", "1.2.826.0.1.3680043.10.999.33", "1.2.826.0.1.3680043.10.999.333", "CT"));
        await svc.StoreFileOnlyAsync(BuildFile("SEPARATE001", "1.2.826.0.1.3680043.10.999.4", "1.2.826.0.1.3680043.10.999.44", "1.2.826.0.1.3680043.10.999.444", "MR"));

        using var db = _infra.CreateDbContext();
        Assert.Equal(2, await db.Studies.CountAsync());
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
    public async Task Print_ExplicitStudyUid_CreatesSeparateStudy()
    {
        var scopes = _infra.CreateScopeFactory();
        var svc = BuildService(scopes);

        // C-STORE creates its own study.
        await svc.StoreFileOnlyAsync(BuildFile("PRINTLINK001", "1.2.826.0.1.3680043.10.999.6", "1.2.826.0.1.3680043.10.999.66", "1.2.826.0.1.3680043.10.999.666", "CT"));

        // Print with a different StudyInstanceUID → creates its own study.
        var printDs = BuildPrintDataset("PRINTLINK001");
        printDs.AddOrUpdate(DicomTag.StudyInstanceUID, "1.2.826.0.1.3680043.10.999.88");
        var stored = await svc.IngestPrintImageAsync(printDs, "PRINTLINK001", "PRINT^LINK", "PRINTAE", "127.0.0.1");
        Assert.NotNull(stored);

        using var db = _infra.CreateDbContext();
        Assert.Equal(2, await db.Studies.CountAsync());
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

    [Fact]
    public async Task Print_EmptyPrintAttachesToNamedSameAe_OneStudy()
    {
        var scopes = _infra.CreateScopeFactory();
        var svc = BuildService(scopes);

        // C-STORE first (named, same AE), then the SonoVision-style empty print:
        // it must land in the named study, keeping the patient identity.
        await svc.StoreFileOnlyAsync(
            BuildFile("ATTACH001", "1.2.826.0.1.3680043.10.999.9", "1.2.826.0.1.3680043.10.999.99", "1.2.826.0.1.3680043.10.999.999", "CT"),
            callingAeTitle: "SVAE", remoteIp: "127.0.0.1");
        var stored = await svc.IngestPrintImageAsync(BuildPrintDataset(string.Empty), string.Empty, string.Empty, "SVAE", "127.0.0.1");
        Assert.NotNull(stored);

        using var db = _infra.CreateDbContext();
        Assert.Equal(1, await db.Studies.CountAsync());
        var study = await db.Studies.Include(s => s.Patient).Include(s => s.Series).ThenInclude(s => s.Images).AsSplitQuery().SingleAsync();
        Assert.Equal("ATTACH001", study.Patient.PatientId);
        Assert.Equal(2, study.Series.SelectMany(s => s.Images).Count());
    }

    [Fact]
    public async Task Print_EmptyPrintsChainAnonymous_OneStudy()
    {
        var scopes = _infra.CreateScopeFactory();
        var svc = BuildService(scopes);

        // Two empty prints from separate film sessions (different jobs) but the same
        // AE within the window must chain into one anonymous SC study (10-print case).
        var first = await svc.IngestPrintImageAsync(BuildPrintDataset(string.Empty), string.Empty, string.Empty, "SVAE", "127.0.0.1", printJobId: 101);
        var second = await svc.IngestPrintImageAsync(BuildPrintDataset(string.Empty), string.Empty, string.Empty, "SVAE", "127.0.0.1", printJobId: 102);
        Assert.NotNull(first);
        Assert.NotNull(second);

        using var db = _infra.CreateDbContext();
        Assert.Equal(1, await db.Studies.CountAsync());
        Assert.Equal(2, await db.DicomImages.CountAsync(i => i.Source == "PRINT"));
    }

    [Fact]
    public async Task Print_EmptyPrintDifferentAe_StaysSeparate()
    {
        var scopes = _infra.CreateScopeFactory();
        var svc = BuildService(scopes);

        // Empty print from a different AE must NOT join the named study (no cross-AE bleed).
        await svc.StoreFileOnlyAsync(
            BuildFile("SEPAE001", "1.2.826.0.1.3680043.10.999.10", "1.2.826.0.1.3680043.10.999.100", "1.2.826.0.1.3680043.10.999.1000", "CT"),
            callingAeTitle: "SVAE", remoteIp: "127.0.0.1");
        var stored = await svc.IngestPrintImageAsync(BuildPrintDataset(string.Empty), string.Empty, string.Empty, "OTHERAE", "127.0.0.1");
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
