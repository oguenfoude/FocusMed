namespace FocusMed.Printing.Profiles;

public record PrintSettings
{
    public string? DefaultPrinterName { get; init; }
    public string? DefaultProfileName { get; init; } = "Booklet A3";
    public int DefaultCopies { get; init; } = 1;
}
