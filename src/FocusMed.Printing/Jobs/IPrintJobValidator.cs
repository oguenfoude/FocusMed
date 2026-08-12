namespace FocusMed.Printing.Jobs;

public interface IPrintJobValidator
{
    Task<ValidationResult> ValidateAsync(PrintJobRequest request, CancellationToken ct = default);
}
