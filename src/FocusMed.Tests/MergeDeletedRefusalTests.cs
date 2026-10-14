using FocusMed.Dashboard.Services;
using FocusMed.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FocusMed.Tests;

/// <summary>
/// Merge-from-stale-row guard (H/D3): merging FROM a Deleted study must be refused,
/// even when the operator's picker row predates the soft-delete.
/// </summary>
public sealed class MergeDeletedRefusalTests : IDisposable
{
    private readonly TestInfra _infra = new();

    private async Task<(int SourceId, int TargetId)> SeedAsync()
    {
        using var db = _infra.CreateDbContext();
        var patient = new Patient { PatientId = "DELREF001", PatientName = "DELREF^TEST" };
        db.Patients.Add(patient);
        var source = new Study { Patient = patient, StudyInstanceUid = "1.2.826.0.1.3680043.10.999.700", Status = StudyStatus.Deleted };
        var target = new Study { Patient = patient, StudyInstanceUid = "1.2.826.0.1.3680043.10.999.701", Status = StudyStatus.Complete };
        db.Studies.Add(source);
        db.Studies.Add(target);
        await db.SaveChangesAsync();
        return (source.Id, target.Id);
    }

    [Fact]
    public async Task MergeStudy_FromDeletedSource_ReturnsFalse()
    {
        var (sourceId, targetId) = await SeedAsync();
        using var db = _infra.CreateDbContext();
        var svc = new StudyService(db, TestInfra.NullLogger<StudyService>());

        Assert.False(await svc.MergeStudyAsync(sourceId, targetId));
        Assert.NotNull(await db.Studies.FindAsync(sourceId));
        Assert.NotNull(await db.Studies.FindAsync(targetId));
    }

    [Fact]
    public async Task MergeStudy_SelfMerge_ReturnsFalse()
    {
        var (sourceId, _) = await SeedAsync();
        using var db = _infra.CreateDbContext();
        var svc = new StudyService(db, TestInfra.NullLogger<StudyService>());

        Assert.False(await svc.MergeStudyAsync(sourceId, sourceId));
    }

    public void Dispose() => _infra.Dispose();
}
