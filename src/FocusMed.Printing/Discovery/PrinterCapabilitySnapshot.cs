namespace FocusMed.Printing.Discovery;

public record PaperTrayInfo
{
    public required string Name { get; init; }
    public int BinNumber { get; init; }
}

public record ResolutionInfo
{
    public int DpiX { get; init; }
    public int DpiY { get; init; }
    public bool IsDefault { get; init; }
}

public record PrinterCapabilitySnapshot
{
    public required string PrinterName { get; init; }
    public required string DriverName { get; init; }
    public bool SupportsDuplex { get; init; }
    public bool SupportsColor { get; init; }
    public bool SupportsCollation { get; init; }
    public IReadOnlyList<PaperSizeInfo> PaperSizes { get; init; } = [];
    public IReadOnlyList<PaperTrayInfo> PaperTrays { get; init; } = [];
    public IReadOnlyList<ResolutionInfo> Resolutions { get; init; } = [];
    public string DiscoverySource { get; init; } = "Unknown";
    /// <summary>Maps paper size name -> tray bin number. Used to set PaperSource explicitly.</summary>
    public IReadOnlyDictionary<string, int> PaperToTrayMap { get; init; } = new Dictionary<string, int>();
}

public record PaperSizeInfo
{
    public required string Name { get; init; }
    public float WidthMm { get; init; }
    public float HeightMm { get; init; }
    public int PaperKindId { get; init; }
}
