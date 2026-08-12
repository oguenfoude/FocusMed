namespace FocusMed.Printing.Jobs;

public record ValidationResult
{
    public bool IsValid { get; init; }
    public string? ErrorMessage { get; init; }
}
