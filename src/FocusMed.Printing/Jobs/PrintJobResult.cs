namespace FocusMed.Printing.Jobs;

public record PrintJobResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public string? PaperSizeUsed { get; init; }
    public bool Landscape { get; init; }
    public bool Duplex { get; init; }
    public int PagesPrinted { get; init; }
    public string? ImposedPdfPath { get; init; }
    public string? DetectedBookletPaper { get; init; }
}
