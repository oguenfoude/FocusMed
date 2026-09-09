using FocusMed.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// Sequential execution: integration tests mutate the process-wide FOCUSMED_DATA
// env var and write to temp dirs, so parallel test collections would race.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace FocusMed.Tests;

/// <summary>
/// Shared scaffolding for pipeline integration tests. Each test gets an isolated
/// temp data dir (FOCUSMED_DATA) + a file-backed SQLite DB with the real schema.
/// Everything is deleted on dispose. No production paths are ever touched.
/// </summary>
public sealed class TestInfra : IDisposable
{
    static TestInfra()
    {
        // Mirrors Dashboard Program.cs: QuestPDF throws without a license set.
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
    }

    public string DataDir { get; }
    public string DbPath { get; }
    private readonly string? _previousDataDir;
    private bool _disposed;

    public TestInfra()
    {
        DataDir = Path.Combine(Path.GetTempPath(), "focusmed-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DataDir);
        DbPath = Path.Combine(DataDir, "test.db");

        _previousDataDir = Environment.GetEnvironmentVariable("FOCUSMED_DATA");
        Environment.SetEnvironmentVariable("FOCUSMED_DATA", DataDir);

        using var db = CreateDbContext();
        db.Database.EnsureCreated();
    }

    public FocusMedDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<FocusMedDbContext>()
            .UseSqlite($"Data Source={DbPath}")
            .Options;
        return new FocusMedDbContext(options);
    }

    /// <summary>Scope factory backed by throwaway contexts on the same temp DB file.</summary>
    public IServiceScopeFactory CreateScopeFactory()
    {
        var services = new ServiceCollection();
        services.AddDbContext<FocusMedDbContext>(o => o.UseSqlite($"Data Source={DbPath}"));
        services.AddLogging(b => { });
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    public static ILogger<T> NullLogger<T>() =>
        LoggerFactory.Create(b => { }).CreateLogger<T>();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Environment.SetEnvironmentVariable("FOCUSMED_DATA", _previousDataDir);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try { Directory.Delete(DataDir, recursive: true); return; }
            catch (IOException) { Task.Delay(100).Wait(); }
            catch (UnauthorizedAccessException) { Task.Delay(100).Wait(); }
        }
    }
}
