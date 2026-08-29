using System.IO;
using FocusMed.Data;
using FocusMed.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FocusMed.Launcher.Services;

public class DatabaseService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DatabaseService> _logger;

    public DatabaseService(IServiceScopeFactory scopeFactory, ILogger<DatabaseService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<List<Study>> GetSelectableStudiesAsync()
    {
        _logger.LogDebug("Querying selectable studies...");
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FocusMedDbContext>();

        var studies = await db.Studies
            .Include(s => s.Patient)
            .Include(s => s.Series)
                .ThenInclude(ss => ss.Images)
            .AsSplitQuery()
            .Where(s => s.Status != StudyStatus.Deleted
                && s.Status != StudyStatus.Archived)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync();

        _logger.LogInformation("Found {Count} selectable studies", studies.Count);
        return studies;
    }

    public async Task<bool> AssignResumeAsync(int studyId, string resumePdfRelativePath)
    {
        _logger.LogInformation("Assigning resume to Study {StudyId}: {Path}", studyId, resumePdfRelativePath);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FocusMedDbContext>();

        var study = await db.Studies.FindAsync(studyId);
        if (study == null)
        {
            _logger.LogWarning("Study {StudyId} not found", studyId);
            return false;
        }

        var oldResume = study.ResumePdfPath;

        if (!string.IsNullOrEmpty(oldResume) && oldResume != resumePdfRelativePath)
        {
            _logger.LogInformation("Replacing previous resume: {Old}", oldResume);
            await DeleteResumeFileAsync(oldResume);
        }

        study.ResumePdfPath = resumePdfRelativePath;
        study.LastUpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        _logger.LogInformation("Resume assigned successfully to Study {StudyId}", studyId);
        return true;
    }

    private async Task DeleteResumeFileAsync(string relativePath)
    {
        var dataDir = Environment.GetEnvironmentVariable("FOCUSMED_DATA")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusMed");
        var absolutePath = Path.Combine(dataDir, relativePath);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                if (File.Exists(absolutePath))
                {
                    File.Delete(absolutePath);
                    _logger.LogInformation("Deleted replaced resume file: {Path}", absolutePath);
                }
                return;
            }
            catch (IOException)
            {
                await Task.Delay(200);
            }
            catch (UnauthorizedAccessException)
            {
                await Task.Delay(200);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete replaced resume file {Path}", absolutePath);
                return;
            }
        }
        _logger.LogWarning("Could not delete replaced resume file after 3 attempts: {Path}", absolutePath);
    }

    public async Task RunMigrationAsync()
    {
        _logger.LogInformation("Checking database migrations...");
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FocusMedDbContext>();
        await db.Database.MigrateAsync();
        _logger.LogInformation("Database is up to date");
    }
}
