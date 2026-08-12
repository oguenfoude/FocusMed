namespace FocusMed.Printing.Jobs;

public interface IPrintExecutionService
{
    Task<PrintJobResult> PrintAsync(PrintJobRequest request, CancellationToken ct = default);
}
