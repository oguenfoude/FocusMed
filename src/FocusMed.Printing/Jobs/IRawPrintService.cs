namespace FocusMed.Printing.Jobs;

public interface IRawPrintService
{
    Task<bool> PrintPdfAsync(string printerIp, byte[] pdfData, string paperSize = "A4", bool duplex = false, bool shortEdgeBind = false, int port = 9100, int timeoutMs = 30000, CancellationToken ct = default);
    Task<bool> PrintPdfAsync(string printerIp, string pdfFilePath, string paperSize = "A4", bool duplex = false, bool shortEdgeBind = false, int port = 9100, int timeoutMs = 30000, CancellationToken ct = default);
}
