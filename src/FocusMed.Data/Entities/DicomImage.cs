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
    /// <summary>Operator-defined display order within a study (StudyDetails drag-drop).
    /// 0 = arrival order fallback (insertion Id = first-sent on top, never random).</summary>
    public int SortOrder { get; set; }
    /// <summary>Operator selection (StudyDetails checkboxes). Null = never touched,
    /// fall back to the default rule (PRINT-only in mixed studies, all in pure C-STORE).</summary>
    public bool? IsSelected { get; set; }

    public Series Series { get; set; } = null!;
    public ICollection<DicomFrame> Frames { get; set; } = new List<DicomFrame>();
}
