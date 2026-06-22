namespace FocusMed.Data.Entities;

public class Series
{
    public int Id { get; set; }
    public int StudyId { get; set; }
    public string SeriesInstanceUid { get; set; } = string.Empty;
    public string Modality { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Study Study { get; set; } = null!;
    public ICollection<DicomImage> Images { get; set; } = new List<DicomImage>();
}
