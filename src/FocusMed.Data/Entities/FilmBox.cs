namespace FocusMed.Data.Entities;

public class FilmBox
{
    public int Id { get; set; }
    public int? PrintJobId { get; set; }
    public string SopInstanceUid { get; set; } = string.Empty;
    public string FilmSize { get; set; } = string.Empty;
    public string Orientation { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public PrintJob? PrintJob { get; set; }
    public ICollection<PrintImageBox> ImageBoxes { get; set; } = new List<PrintImageBox>();
}
