using FocusMed.Printing.Discovery;
using FocusMed.Printing.Imposition;
using FocusMed.Printing.Jobs;
using FocusMed.Printing.Profiles;
using FocusMed.Printing.Verification;
using Microsoft.Extensions.DependencyInjection;

namespace FocusMed.Printing;

public static class DependencyInjection
{
    public static IServiceCollection AddFocusMedPrinting(this IServiceCollection services)
    {
        services.AddSingleton<ModernCapabilityProvider>();
        services.AddSingleton<LegacyCapabilityProvider>();
        services.AddSingleton<Win32CapabilityProvider>();

        services.AddSingleton<ICapabilityConfirmationStore, CapabilityConfirmationStore>();
        services.AddSingleton<IPrinterCapabilityService, PrinterCapabilityService>();
        services.AddSingleton<IPrinterDiscoveryService, PrinterDiscoveryService>();
        services.AddSingleton<ITestPageService, TestPageService>();

        services.AddSingleton<IPrintProfileBuilder, PrintProfileBuilder>();
        services.AddSingleton<IPrinterSettingsStore, PrinterSettingsStore>();
        services.AddSingleton<IBookletImpositionService, BookletImpositionService>();

        services.AddSingleton<IPrintJobValidator, PrintJobValidator>();
        services.AddSingleton<IPrintExecutionService, PrintExecutionService>();

        return services;
    }
}
