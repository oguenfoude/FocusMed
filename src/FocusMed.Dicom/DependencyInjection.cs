using Microsoft.Extensions.DependencyInjection;

namespace FocusMed.Dicom;

public static class DependencyInjection
{
    public static IServiceCollection AddFocusMedDicom(this IServiceCollection services)
    {
        services.AddSingleton<DicomUpsertService>();
        services.AddHostedService<StudyCompletionService>();

        return services;
    }
}
