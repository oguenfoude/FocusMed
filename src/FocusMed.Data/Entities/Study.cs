namespace FocusMed.Data.Entities;

public class Study
{
    public int Id { get; set; }
    public int PatientId { get; set; }
    public string StudyInstanceUid { get; set; } = string.Empty;
    public DateTime? StudyDate { get; set; }
    public StudyStatus Status { get; set; } = StudyStatus.Receiving;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;

    public Patient Patient { get; set; } = null!;
    public ICollection<Series> Series { get; set; } = new List<Series>();
}
