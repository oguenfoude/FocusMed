using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FocusMed.Data;

public class FocusMedDbContextFactory : IDesignTimeDbContextFactory<FocusMedDbContext>
{
    public FocusMedDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<FocusMedDbContext>();
        var dbPath = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "data", "db", "focusmed.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        optionsBuilder.UseSqlite($"Data Source={dbPath}");

        return new FocusMedDbContext(optionsBuilder.Options);
    }
}
