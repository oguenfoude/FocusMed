namespace FocusMed.Data.Entities;

public class PrintJob
{
    public int Id { get; set; }
    public string SopInstanceUid { get; set; } = string.Empty;
    public PrintStatus Status { get; set; } = PrintStatus.Pending;
    public int NumberOfCopies { get; set; } = 1;
    public string PrintPriority { get; set; } = "NORMAL";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int? PatientId { get; set; }
    public Patient? Patient { get; set; }

    public int? StudyId { get; set; }
    public Study? Study { get; set; }

    public ICollection<FilmBox> FilmBoxes { get; set; } = new List<FilmBox>();
}
