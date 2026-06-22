using FocusMed.Data.Interceptors;
using FocusMed.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FocusMed.Data;

public static class DependencyInjection
{
    public static IServiceCollection AddFocusMedData(this IServiceCollection services, string dbPath)
    {
        services.AddScoped<SqliteWalInterceptor>();

        services.AddDbContext<FocusMedDbContext>((sp, options) =>
        {
            var interceptor = sp.GetRequiredService<SqliteWalInterceptor>();
            options.UseSqlite($"Data Source={dbPath}")
                   .AddInterceptors(interceptor);
        });

        services.AddScoped<IPatientRepository, PatientRepository>();
        services.AddScoped<IStudyRepository, StudyRepository>();
        services.AddScoped<ISeriesRepository, SeriesRepository>();
        services.AddScoped<IDicomImageRepository, DicomImageRepository>();

        return services;
    }
}
