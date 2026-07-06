using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FocusMed.Data;

public class FocusMedDbContextFactory : IDesignTimeDbContextFactory<FocusMedDbContext>
{
    public FocusMedDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<FocusMedDbContext>();
        optionsBuilder.UseNpgsql("Host=localhost;Port=5432;Database=focusmed;Username=postgres;Password=admin");

        return new FocusMedDbContext(optionsBuilder.Options);
    }
}
