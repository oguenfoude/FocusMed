namespace FocusMed.Data.Entities;

public class Patient
{
    public int Id { get; set; }
    public string PatientId { get; set; } = string.Empty;
    public string PatientName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Study> Studies { get; set; } = new List<Study>();
}
