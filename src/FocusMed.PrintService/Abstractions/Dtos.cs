namespace FocusMed.PrintService.Abstractions;

public record PrintRequest(
    string PdfPath,
    string PrinterName,
    int Copies = 1,
    bool Duplex = false,
    bool BookletMode = false);

public record PrintResult(bool Success, int? JobId, string? ErrorMessage);

public record JobStatus(string State, string? ErrorMessage);

public record PrinterInfo(string Name, bool Enabled, string Protocol, bool CanDuplex, int PaperSizeCount);

public record PaperSizeInfo(string Name, int WidthHundredthsMm, int HeightHundredthsMm, string Kind);

public record PrinterCapabilities(
    string Name,
    bool IsAvailable,
    bool CanDuplex,
    IReadOnlyList<string> SupportedDuplexModes,
    IReadOnlyList<PaperSizeInfo> SupportedPaperSizes);
