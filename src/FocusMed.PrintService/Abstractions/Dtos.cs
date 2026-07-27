namespace FocusMed.PrintService.Abstractions;

public record PrintRequest(
    string PdfPath,
    string PrinterName,
    int Copies = 1,
    bool Duplex = false);

public record PrintResult(bool Success, int? JobId, string? ErrorMessage);

public record JobStatus(string State, string? ErrorMessage);

public record PrinterInfo(string Name, bool Enabled, string Protocol);
