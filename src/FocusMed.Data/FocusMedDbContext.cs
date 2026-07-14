using FocusMed.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FocusMed.Data;

public class FocusMedDbContext : DbContext
{
    public FocusMedDbContext(DbContextOptions<FocusMedDbContext> options) : base(options)
    {
    }

    public DbSet<Patient> Patients { get; set; } = null!;
    public DbSet<Study> Studies { get; set; } = null!;
    public DbSet<Series> Series { get; set; } = null!;
    public DbSet<DicomImage> DicomImages { get; set; } = null!;
    public DbSet<DicomFrame> DicomFrames { get; set; } = null!;
    public DbSet<PrintJob> PrintJobs { get; set; } = null!;
    public DbSet<FilmBox> FilmBoxes { get; set; } = null!;
    public DbSet<PrintImageBox> PrintImageBoxes { get; set; } = null!;
    public DbSet<WorklistEntry> WorklistEntries { get; set; } = null!;
    public DbSet<StorageCommitmentJob> StorageCommitmentJobs { get; set; } = null!;
    public DbSet<AssociationAuditEntry> AssociationAuditEntries { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Study>()
            .HasIndex(s => s.StudyInstanceUid);

        modelBuilder.Entity<Series>()
            .HasIndex(s => s.SeriesInstanceUid);

        modelBuilder.Entity<DicomImage>()
            .HasIndex(i => i.SopInstanceUid);

        modelBuilder.Entity<DicomImage>()
            .HasIndex(i => i.Source);

        modelBuilder.Entity<Study>()
            .HasIndex(s => s.Status);

        modelBuilder.Entity<Study>()
            .HasIndex(s => s.LastUpdatedAt);

        modelBuilder.Entity<Patient>()
            .HasIndex(p => p.PatientId);

        modelBuilder.Entity<PrintJob>()
            .HasIndex(p => p.SopInstanceUid)
            .IsUnique();

        modelBuilder.Entity<FilmBox>()
            .HasIndex(f => f.SopInstanceUid)
            .IsUnique();

        modelBuilder.Entity<PrintImageBox>()
            .HasIndex(i => i.SopInstanceUid)
            .IsUnique();

        modelBuilder.Entity<FilmBox>()
            .HasOne(f => f.PrintJob)
            .WithMany(p => p.FilmBoxes)
            .HasForeignKey(f => f.PrintJobId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<PrintImageBox>()
            .HasOne(i => i.FilmBox)
            .WithMany(f => f.ImageBoxes)
            .HasForeignKey(i => i.FilmBoxId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<PrintJob>()
            .HasOne(p => p.Patient)
            .WithMany()
            .HasForeignKey(p => p.PatientId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<PrintJob>()
            .HasOne(p => p.Study)
            .WithMany()
            .HasForeignKey(p => p.StudyId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<StorageCommitmentJob>()
            .Property(j => j.Status)
            .HasConversion<int>();

        modelBuilder.Entity<StorageCommitmentJob>()
            .HasIndex(j => j.Status);

        modelBuilder.Entity<PrintJob>()
            .Property(j => j.Status)
            .HasConversion<int>();

        modelBuilder.Entity<Study>()
            .Property(s => s.Status)
            .HasConversion<int>();

        modelBuilder.Entity<AssociationAuditEntry>()
            .Property(e => e.Outcome)
            .HasConversion<int>();

        modelBuilder.Entity<WorklistEntry>()
            .HasIndex(w => w.PatientName);

        modelBuilder.Entity<PrintJob>()
            .HasIndex(p => p.PatientId);

        modelBuilder.Entity<PrintJob>()
            .HasIndex(p => p.StudyId);

        modelBuilder.Entity<FilmBox>()
            .HasIndex(f => f.PrintJobId);

        modelBuilder.Entity<PrintImageBox>()
            .HasIndex(i => i.FilmBoxId);

        modelBuilder.Entity<Study>()
            .HasIndex(s => s.PatientId);

        modelBuilder.Entity<Series>()
            .HasIndex(s => s.StudyId);

        modelBuilder.Entity<DicomFrame>()
            .HasIndex(f => f.DicomImageId);

        modelBuilder.Entity<DicomImage>()
            .HasIndex(i => i.SeriesId);
    }
}
