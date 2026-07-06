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
            .HasIndex(s => s.StudyInstanceUid)
            .IsUnique();

        modelBuilder.Entity<Series>()
            .HasIndex(s => s.SeriesInstanceUid)
            .IsUnique();

        modelBuilder.Entity<DicomImage>()
            .HasIndex(i => i.SopInstanceUid)
            .IsUnique();

        modelBuilder.Entity<Study>()
            .HasIndex(s => s.Status);

        modelBuilder.Entity<Study>()
            .HasIndex(s => s.CreatedAt);

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
            .HasForeignKey(f => f.PrintJobId);

        modelBuilder.Entity<PrintImageBox>()
            .HasOne(i => i.FilmBox)
            .WithMany(f => f.ImageBoxes)
            .HasForeignKey(i => i.FilmBoxId);

        modelBuilder.Entity<AssociationAuditEntry>()
            .HasIndex(e => e.Timestamp);
    }
}
