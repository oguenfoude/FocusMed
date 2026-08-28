namespace FocusMed.Data.Entities;

public class PrintAuditEntry
{
    public int Id { get; set; }
    public int? StudyId { get; set; }
    public string? PatientName { get; set; } = "";
    public string? ProfileName { get; set; } = "";
    public string? PrintMode { get; set; } = "";
    public int Copies { get; set; } = 1;
    public int PagesPrinted { get; set; }
    public string? PaperSize { get; set; } = "";
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; } = "";
    public string? PrinterName { get; set; } = "";
    public DateTime PrintedAt { get; set; } = DateTime.UtcNow;
}
