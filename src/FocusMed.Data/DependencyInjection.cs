using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FocusMed.Data;

public static class DependencyInjection
{
    public static IServiceCollection AddFocusMedData(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<FocusMedDbContext>((_, options) =>
        {
            options.UseNpgsql(connectionString);
        });

        services.AddSingleton<Messaging.IStudyEventBus, Messaging.InMemoryStudyEventBus>();

        return services;
    }
}
