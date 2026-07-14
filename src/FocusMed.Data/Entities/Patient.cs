namespace FocusMed.Data.Entities;

public class Patient
{
    public int Id { get; set; }
    public string PatientId { get; set; } = string.Empty;
    public string PatientName { get; set; } = string.Empty;

    public ICollection<Study> Studies { get; set; } = new List<Study>();
}
