namespace FocusMed.Data.Entities;

public class PrintImageBox
{
    public int Id { get; set; }
    public int? FilmBoxId { get; set; }
    public string SopInstanceUid { get; set; } = string.Empty;
    public string ReferencedImageSopUid { get; set; } = string.Empty;
    public int FrameNumber { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public FilmBox? FilmBox { get; set; }
}
