namespace FocusMed.Printing.Discovery;

public interface IPrinterCapabilityService
{
    Task<PrinterCapabilitySnapshot> GetSnapshotAsync(string printerName, CancellationToken ct = default);
}
