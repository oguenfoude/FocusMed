using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FocusMed.Data;

public class FocusMedDbContextFactory : IDesignTimeDbContextFactory<FocusMedDbContext>
{
    public FocusMedDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<FocusMedDbContext>();
        optionsBuilder.UseSqlite(
            Environment.GetEnvironmentVariable("FOCUSMED_DB_CONNECTION") ?? "Data Source=focusmed.db");

        return new FocusMedDbContext(optionsBuilder.Options);
    }
}
