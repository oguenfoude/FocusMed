namespace FocusMed.Printing.Discovery;

public interface IPrinterDiscoveryService
{
    IReadOnlyList<InstalledPrinter> GetAvailablePrinters();
}
