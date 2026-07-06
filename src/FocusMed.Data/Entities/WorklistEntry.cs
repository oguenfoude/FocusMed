using System.ComponentModel.DataAnnotations;

namespace FocusMed.Data.Entities;

public class WorklistEntry
{
    [Key]
    public int Id { get; set; }

    public string PatientId { get; set; } = string.Empty;
    public string PatientName { get; set; } = string.Empty;
    public string AccessionNumber { get; set; } = string.Empty;
    public string Modality { get; set; } = string.Empty;
    public DateTime? ScheduledProcedureStepStartDate { get; set; }
    public string ScheduledProcedureStepId { get; set; } = string.Empty;
    public string RequestedProcedureId { get; set; } = string.Empty;
    public string StudyInstanceUid { get; set; } = string.Empty;
}
