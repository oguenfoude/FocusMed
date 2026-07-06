using FocusMed.Dicom.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FocusMed.Dicom;

public static class DependencyInjection
{
    public static IServiceCollection AddFocusMedDicom(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DicomNetworkingOptions>(
            configuration.GetSection(DicomNetworkingOptions.SectionName));

        services.AddSingleton<DicomUpsertService>();
        services.AddHostedService<StudyCompletionService>();
        services.AddHostedService<StorageCommitmentScuService>();
        services.AddSingleton<IStorageForwardQueue, StorageForwardQueue>();
        services.AddHostedService<StorageForwardService>();
        services.AddSingleton<IPrintScuService, PrintScuService>();
        services.AddTransient<ScalingEngine>();

        return services;
    }
}
