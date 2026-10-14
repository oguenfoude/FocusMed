using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FocusMed.Data;

public class FocusMedDbContextFactory : IDesignTimeDbContextFactory<FocusMedDbContext>
{
    public FocusMedDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<FocusMedDbContext>();
        optionsBuilder.UseSqlite(ConnectionStringHelper.Resolve(null));

        return new FocusMedDbContext(optionsBuilder.Options);
    }
}
