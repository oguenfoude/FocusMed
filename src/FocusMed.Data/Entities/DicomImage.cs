namespace FocusMed.Data.Entities;

public class DicomImage
{
    public int Id { get; set; }
    public int SeriesId { get; set; }
    public string SopInstanceUid { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string SopClassUid { get; set; } = string.Empty;
    public string Source { get; set; } = "C-STORE";
    public string? PngPath { get; set; }
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;

    public Series Series { get; set; } = null!;
    public ICollection<DicomFrame> Frames { get; set; } = new List<DicomFrame>();
}
