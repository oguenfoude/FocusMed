namespace FocusMed.Data.Entities;

public class AssociationAuditEntry
{
    public int Id { get; set; }
    public string CallingAeTitle { get; set; } = string.Empty;
    public string RemoteIp { get; set; } = string.Empty;
    public string CalledAeTitle { get; set; } = string.Empty;
    public string RequestedSopClasses { get; set; } = string.Empty;
    public AssociationOutcome Outcome { get; set; }
    public int? DurationMs { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public enum AssociationOutcome
{
    Success = 0,
    Rejected = 1,
    Failed = 2,
    PartiallyAccepted = 3
}
