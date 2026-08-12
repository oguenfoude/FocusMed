namespace FocusMed.Printing.Profiles;

public record PrintProfile
{
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public bool IsBooklet { get; init; }
    public bool RequiresDuplex { get; init; }
    public bool UseDuplexShortEdge { get; init; }
    public bool ForceGrayscale { get; init; }
    public string? PaperSizeName { get; init; }
}
