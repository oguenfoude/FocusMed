using FocusMed.Printing.Profiles;

namespace FocusMed.Printing.Jobs;

public record PrintJobRequest
{
    public required string PrinterName { get; init; }
    public required string PdfPath { get; init; }
    public required PrintProfile Profile { get; init; }
    public int Copies { get; init; } = 1;
    public bool ForceGrayscale { get; init; }
}
