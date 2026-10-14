using FocusMed.Dashboard.Services;
using FocusMed.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FocusMed.Tests;

/// <summary>
/// Operator state persistence: image order (drag/↑↓) and selection (checkboxes)
/// must survive reloads via DicomImage.SortOrder / IsSelected.
/// </summary>
public sealed class StudyServiceStateTests : IDisposable
{
    private readonly TestInfra _infra = new();

    private async Task<int> SeedStudyAsync()
    {
        using var db = _infra.CreateDbContext();
        var patient = new Patient { PatientId = "STATE001", PatientName = "STATE^TEST" };
        db.Patients.Add(patient);
        var study = new Study { Patient = patient, StudyInstanceUid = "1.2.826.0.1.3680043.10.999.500" };
        db.Studies.Add(study);
        var series = new Series { Study = study, SeriesInstanceUid = "1.2.826.0.1.3680043.10.999.501", Modality = "SC" };
        db.Series.Add(series);
        for (var k = 0; k < 3; k++)
            db.DicomImages.Add(new DicomImage
            {
                Series = series,
                SopInstanceUid = $"1.2.826.0.1.3680043.10.999.50{k + 2}",
                FilePath = $"img{k}.dcm",
                SopClassUid = "1.2.840.10008.5.1.4.1.1.7",
                Source = "PRINT"
            });
        await db.SaveChangesAsync();
        return study.Id;
    }

    [Fact]
    public async Task SetImageOrder_PersistsDisplayOrder()
    {
        var studyId = await SeedStudyAsync();
        using var db = _infra.CreateDbContext();
        var svc = new StudyService(db, TestInfra.NullLogger<StudyService>());

        var reversed = new List<string>
        {
            "1.2.826.0.1.3680043.10.999.504",
            "1.2.826.0.1.3680043.10.999.503",
            "1.2.826.0.1.3680043.10.999.502"
        };
        Assert.True(await svc.SetImageOrderAsync(studyId, reversed));

        var ordered = await db.DicomImages
            .Include(i => i.Series)
            .Where(i => i.Series.StudyId == studyId)
            .OrderBy(i => i.SortOrder)
            .Select(i => i.SopInstanceUid)
            .ToListAsync();
        Assert.Equal(reversed, ordered);
    }

    [Fact]
    public async Task SetImageSelection_PersistsCheckedSet()
    {
        var studyId = await SeedStudyAsync();
        using var db = _infra.CreateDbContext();
        var svc = new StudyService(db, TestInfra.NullLogger<StudyService>());

        var selected = new HashSet<string> { "1.2.826.0.1.3680043.10.999.502" };
        Assert.True(await svc.SetImageSelectionAsync(studyId, selected));

        var flags = await db.DicomImages
            .Include(i => i.Series)
            .Where(i => i.Series.StudyId == studyId)
            .OrderBy(i => i.SopInstanceUid)
            .Select(i => i.IsSelected)
            .ToListAsync();
        Assert.Equal(new bool?[] { true, false, false }, flags);
    }

    [Fact]
    public async Task RenamePatient_SoleStudy_UpdatesInPlace()
    {
        var studyId = await SeedStudyAsync();
        using var db = _infra.CreateDbContext();
        var svc = new StudyService(db, TestInfra.NullLogger<StudyService>());

        Assert.True(await svc.RenamePatientAsync(studyId, "NEWID001", "NEW^NAME"));
        var study = await db.Studies.Include(s => s.Patient).SingleAsync(s => s.Id == studyId);
        Assert.Equal("NEWID001", study.Patient.PatientId);
        Assert.Equal("NEW^NAME", study.Patient.PatientName);
        Assert.Equal(1, await db.Patients.CountAsync());
    }

    [Fact]
    public async Task RenamePatient_SharedPatient_ClonesForThisStudyOnly()
    {
        var studyId = await SeedStudyAsync();
        int otherId;
        using (var db = _infra.CreateDbContext())
        {
            var first = await db.Studies.Include(s => s.Patient).SingleAsync(s => s.Id == studyId);
            var other = new Study { PatientId = first.PatientId, StudyInstanceUid = "1.2.826.0.1.3680043.10.999.501" };
            db.Studies.Add(other);
            await db.SaveChangesAsync();
            otherId = other.Id;
        }

        using var db2 = _infra.CreateDbContext();
        var svc = new StudyService(db2, TestInfra.NullLogger<StudyService>());
        Assert.True(await svc.RenamePatientAsync(studyId, "CLONE001", "CLONE^NAME"));

        var renamed = await db2.Studies.Include(s => s.Patient).SingleAsync(s => s.Id == studyId);
        var untouched = await db2.Studies.Include(s => s.Patient).SingleAsync(s => s.Id == otherId);
        Assert.Equal("CLONE001", renamed.Patient.PatientId);
        Assert.Equal("STATE001", untouched.Patient.PatientId);
        Assert.NotEqual(renamed.Patient.Id, untouched.Patient.Id);
    }

    [Fact]
    public async Task RenamePatient_BothEmpty_ReturnsFalse()
    {
        var studyId = await SeedStudyAsync();
        using var db = _infra.CreateDbContext();
        var svc = new StudyService(db, TestInfra.NullLogger<StudyService>());
        Assert.False(await svc.RenamePatientAsync(studyId, "", "  "));
    }

    private async Task<int> SeedEmptyStudyAsync()
    {
        using var db = _infra.CreateDbContext();
        var patient = new Patient { PatientId = "EMPTY001", PatientName = "EMPTY^TEST" };
        db.Patients.Add(patient);
        var study = new Study { Patient = patient, StudyInstanceUid = "1.2.826.0.1.3680043.10.999.600" };
        db.Studies.Add(study);
        await db.SaveChangesAsync();
        return study.Id;
    }

    [Fact]
    public async Task MarkComplete_EmptyStudy_Refused()
    {
        var studyId = await SeedEmptyStudyAsync();
        using var db = _infra.CreateDbContext();
        var svc = new StudyService(db, TestInfra.NullLogger<StudyService>());

        Assert.False(await svc.MarkCompleteAsync(studyId));

        var study = await db.Studies.SingleAsync(s => s.Id == studyId);
        Assert.Equal(StudyStatus.Receiving, study.Status);
    }

    [Fact]
    public async Task MarkComplete_ReceivingWithImages_Completes()
    {
        var studyId = await SeedStudyAsync();
        using var db = _infra.CreateDbContext();
        var svc = new StudyService(db, TestInfra.NullLogger<StudyService>());
        Assert.Equal(StudyStatus.Receiving, (await db.Studies.SingleAsync(s => s.Id == studyId)).Status);

        Assert.True(await svc.MarkCompleteAsync(studyId));

        var study = await db.Studies.SingleAsync(s => s.Id == studyId);
        Assert.Equal(StudyStatus.Complete, study.Status);
    }

    [Fact]
    public async Task MarkComplete_AlreadyComplete_SucceedsIdempotently()
    {
        var studyId = await SeedStudyAsync();
        using var db = _infra.CreateDbContext();
        var svc = new StudyService(db, TestInfra.NullLogger<StudyService>());

        Assert.True(await svc.MarkCompleteAsync(studyId));
        Assert.True(await svc.MarkCompleteAsync(studyId));

        var study = await db.Studies.SingleAsync(s => s.Id == studyId);
        Assert.Equal(StudyStatus.Complete, study.Status);
        Assert.Equal(3, await db.DicomImages.Include(i => i.Series).CountAsync(i => i.Series.StudyId == studyId));
    }

    public void Dispose() => _infra.Dispose();
}
