using FocusMed.Data;
using FocusMed.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FocusMed.Tests;

// Proves the exact technique BackupService uses: VACUUM INTO with a filename
// LITERAL (bound parameters are rejected by SQLite) produces a consistent,
// independently-openable copy.
public class BackupVacuumTests
{
    [Fact]
    public async Task VacuumInto_CreatesConsistentCopy()
    {
        var dir = Path.Combine(Path.GetTempPath(), "focusmed-tests");
        Directory.CreateDirectory(dir);
        var src = Path.Combine(dir, $"src-{Guid.NewGuid():N}.db");
        var dst = Path.Combine(dir, $"dst-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<FocusMedDbContext>()
                .UseSqlite($"Data Source={src}").Options;
            await using (var db = new FocusMedDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                db.Patients.Add(new Patient { PatientId = "T1", PatientName = "Test^Patient" });
                await db.SaveChangesAsync();
                var sql = "VACUUM INTO '" + dst.Replace("'", "''") + "'";
                await db.Database.ExecuteSqlRawAsync(sql);
            }

            Assert.True(File.Exists(dst));
            var checkOptions = new DbContextOptionsBuilder<FocusMedDbContext>()
                .UseSqlite($"Data Source={dst}").Options;
            await using (var check = new FocusMedDbContext(checkOptions))
            {
                Assert.Equal(1, await check.Patients.CountAsync());
                Assert.Equal("Test^Patient", await check.Patients.Select(p => p.PatientName).FirstAsync());
            }
        }
        finally
        {
            try { File.Delete(src); } catch { }
            try { File.Delete(dst); } catch { }
        }
    }
}
