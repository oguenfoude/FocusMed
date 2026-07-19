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

        services.Configure<PngExtractionOptions>(
            configuration.GetSection(PngExtractionOptions.SectionName));

        services.AddSingleton<DicomUpsertService>();
        services.AddSingleton<PngExtractionService>();
        services.AddSingleton<IPrintScuService, PrintScuService>();
        services.AddSingleton<PrintExecutionService>();
        services.AddHostedService<StudyCompletionService>();
        services.AddHostedService<StorageCommitmentScuService>();
        services.AddSingleton<IStorageForwardQueue, StorageForwardQueue>();
        services.AddHostedService<StorageForwardService>();

        return services;
    }
}
