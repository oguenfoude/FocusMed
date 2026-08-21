using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FocusMed.Data;

public static class DependencyInjection
{
    public static IServiceCollection AddFocusMedData(this IServiceCollection services, string connectionString)
    {
        services.AddDbContextFactory<FocusMedDbContext>((_, options) =>
        {
            options.UseSqlite(connectionString);
        });

        return services;
    }
}
