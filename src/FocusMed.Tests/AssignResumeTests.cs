using FocusMed.Data.Entities;
using FocusMed.Launcher.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FocusMed.Tests;

/// <summary>
/// Resume assignment: DatabaseService against the real schema. Proves the
/// study-state re-validation (Deleted/Archived refused) and the happy path.
/// </summary>
public sealed class AssignResumeTests : IDisposable
{
    private readonly TestInfra _infra = new();

    private async Task<int> SeedStudyAsync(StudyStatus status)
    {
        using var db = _infra.CreateDbContext();
        var patient = new Patient { PatientId = "RESUME" + Guid.NewGuid().ToString("N")[..6], PatientName = "RESUME^TEST" };
        db.Patients.Add(patient);
        await db.SaveChangesAsync();
        var study = new Study
        {
            PatientId = patient.Id,
            StudyInstanceUid = "1.2.826.0.1.3680043.10.998." + Guid.NewGuid().ToString("N")[..8],
            Status = status,
            CreatedAt = DateTime.UtcNow,
            LastUpdatedAt = DateTime.UtcNow
        };
        db.Studies.Add(study);
        await db.SaveChangesAsync();
        return study.Id;
    }

    private DatabaseService BuildService(IServiceScopeFactory scopes) => new(
        scopes,
        scopes.CreateScope().ServiceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DatabaseService>>());

    [Fact]
    public async Task Assign_ToCompleteStudy_SucceedsAndSetsPath()
    {
        var scopes = _infra.CreateScopeFactory();
        var id = await SeedStudyAsync(StudyStatus.Complete);

        var ok = await BuildService(scopes).AssignResumeAsync(id, "resumes/resume_test.pdf");

        Assert.True(ok);
        using var db = _infra.CreateDbContext();
        Assert.Equal("resumes/resume_test.pdf", (await db.Studies.FindAsync(id))!.ResumePdfPath);
    }

    [Fact]
    public async Task Assign_ToDeletedStudy_Refused()
    {
        var scopes = _infra.CreateScopeFactory();
        var id = await SeedStudyAsync(StudyStatus.Deleted);

        var ok = await BuildService(scopes).AssignResumeAsync(id, "resumes/resume_test.pdf");

        Assert.False(ok);
        using var db = _infra.CreateDbContext();
        Assert.Null((await db.Studies.FindAsync(id))!.ResumePdfPath);
    }

    [Fact]
    public async Task Assign_ToArchivedStudy_Refused()
    {
        var scopes = _infra.CreateScopeFactory();
        var id = await SeedStudyAsync(StudyStatus.Archived);

        var ok = await BuildService(scopes).AssignResumeAsync(id, "resumes/resume_test.pdf");

        Assert.False(ok);
        using var db = _infra.CreateDbContext();
        Assert.Null((await db.Studies.FindAsync(id))!.ResumePdfPath);
    }

    [Fact]
    public async Task Assign_ToMissingStudy_ReturnsFalse()
    {
        var scopes = _infra.CreateScopeFactory();

        var ok = await BuildService(scopes).AssignResumeAsync(int.MaxValue, "resumes/resume_test.pdf");

        Assert.False(ok);
    }

    public void Dispose() => _infra.Dispose();
}
