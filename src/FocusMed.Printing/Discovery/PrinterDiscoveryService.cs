using System.Drawing.Printing;
using Microsoft.Extensions.Logging;

namespace FocusMed.Printing.Discovery;

internal sealed class PrinterDiscoveryService(
    ILogger<PrinterDiscoveryService> logger) : IPrinterDiscoveryService
{
    public IReadOnlyList<InstalledPrinter> GetAvailablePrinters()
    {
        try
        {
            var printers = PrinterSettings.InstalledPrinters
                .Cast<string>()
                .Select(name => new InstalledPrinter(Name: name))
                .ToList();

            logger.LogDebug("Discovered {Count} installed printers", printers.Count);
            return printers;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to enumerate installed printers");
            return [];
        }
    }
}
