namespace FocusMed.Data.Entities;

public class Patient
{
    public int Id { get; set; }
    public string PatientId { get; set; } = string.Empty;
    public string PatientName { get; set; } = string.Empty;
    public string? BirthDate { get; set; }
    public string? Sex { get; set; }

    public ICollection<Study> Studies { get; set; } = new List<Study>();
}
