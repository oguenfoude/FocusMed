using System.ComponentModel.DataAnnotations;

namespace FocusMed.Data.Entities;

public class StorageCommitmentJob
{
    [Key]
    public int Id { get; set; }

    [Required]
    public string TransactionUid { get; set; } = string.Empty;

    public string RequestedSopInstanceUids { get; set; } = string.Empty;

    public string CallingAet { get; set; } = string.Empty;

    public StorageCommitmentStatus Status { get; set; } = StorageCommitmentStatus.Pending;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}
