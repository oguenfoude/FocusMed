namespace FocusMed.Data.Entities;

public class DicomFrame
{
    public int Id { get; set; }
    public int DicomImageId { get; set; }
    
    public int FrameIndex { get; set; }
    public string? PngPath { get; set; }
    public DateTime ExtractedAt { get; set; } = DateTime.UtcNow;

    public DicomImage DicomImage { get; set; } = null!;
}
