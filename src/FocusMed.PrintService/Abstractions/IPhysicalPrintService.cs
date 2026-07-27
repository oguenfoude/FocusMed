namespace FocusMed.PrintService.Abstractions;

public interface IPhysicalPrintService
{
    Task<PrintResult> PrintAsync(PrintRequest request);
    Task<JobStatus> GetJobStatusAsync(string printerName, int jobId);
    Task<IReadOnlyList<PrinterInfo>> GetConfiguredPrintersAsync();
}
