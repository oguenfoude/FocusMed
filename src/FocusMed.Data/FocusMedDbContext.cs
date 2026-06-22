using FocusMed.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FocusMed.Data;

public class FocusMedDbContext : DbContext
{
    public FocusMedDbContext(DbContextOptions<FocusMedDbContext> options) : base(options)
    {
    }

    public DbSet<Patient> Patients => Set<Patient>();
    public DbSet<Study> Studies => Set<Study>();
    public DbSet<Series> Series => Set<Series>();
    public DbSet<DicomImage> DicomImages => Set<DicomImage>();

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
    }
}
