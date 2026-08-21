using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FocusMed.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialSqlite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AssociationAuditEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CallingAeTitle = table.Column<string>(type: "TEXT", nullable: false),
                    RemoteIp = table.Column<string>(type: "TEXT", nullable: false),
                    CalledAeTitle = table.Column<string>(type: "TEXT", nullable: false),
                    RequestedSopClasses = table.Column<string>(type: "TEXT", nullable: false),
                    Outcome = table.Column<int>(type: "INTEGER", nullable: false),
                    DurationMs = table.Column<int>(type: "INTEGER", nullable: true),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssociationAuditEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Patients",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PatientId = table.Column<string>(type: "TEXT", nullable: false),
                    PatientName = table.Column<string>(type: "TEXT", nullable: false),
                    BirthDate = table.Column<string>(type: "TEXT", nullable: true),
                    Sex = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Patients", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StorageCommitmentJobs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TransactionUid = table.Column<string>(type: "TEXT", nullable: false),
                    RequestedSopInstanceUids = table.Column<string>(type: "TEXT", nullable: false),
                    CallingAet = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorageCommitmentJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorklistEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PatientId = table.Column<string>(type: "TEXT", nullable: false),
                    PatientName = table.Column<string>(type: "TEXT", nullable: false),
                    AccessionNumber = table.Column<string>(type: "TEXT", nullable: false),
                    Modality = table.Column<string>(type: "TEXT", nullable: false),
                    ScheduledProcedureStepStartDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ScheduledProcedureStepId = table.Column<string>(type: "TEXT", nullable: false),
                    RequestedProcedureId = table.Column<string>(type: "TEXT", nullable: false),
                    StudyInstanceUid = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorklistEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Studies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PatientId = table.Column<int>(type: "INTEGER", nullable: false),
                    StudyInstanceUid = table.Column<string>(type: "TEXT", nullable: false),
                    StudyDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    AccessionNumber = table.Column<string>(type: "TEXT", nullable: true),
                    InstitutionName = table.Column<string>(type: "TEXT", nullable: true),
                    Manufacturer = table.Column<string>(type: "TEXT", nullable: true),
                    ReferringPhysicianName = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ResumePdfPath = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastUpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Studies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Studies_Patients_PatientId",
                        column: x => x.PatientId,
                        principalTable: "Patients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PrintJobs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SopInstanceUid = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    NumberOfCopies = table.Column<int>(type: "INTEGER", nullable: false),
                    PrintPriority = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PatientId = table.Column<int>(type: "INTEGER", nullable: true),
                    StudyId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrintJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PrintJobs_Patients_PatientId",
                        column: x => x.PatientId,
                        principalTable: "Patients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PrintJobs_Studies_StudyId",
                        column: x => x.StudyId,
                        principalTable: "Studies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Series",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StudyId = table.Column<int>(type: "INTEGER", nullable: false),
                    SeriesInstanceUid = table.Column<string>(type: "TEXT", nullable: false),
                    Modality = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Series", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Series_Studies_StudyId",
                        column: x => x.StudyId,
                        principalTable: "Studies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FilmBoxes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PrintJobId = table.Column<int>(type: "INTEGER", nullable: true),
                    SopInstanceUid = table.Column<string>(type: "TEXT", nullable: false),
                    FilmSize = table.Column<string>(type: "TEXT", nullable: false),
                    Orientation = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FilmBoxes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FilmBoxes_PrintJobs_PrintJobId",
                        column: x => x.PrintJobId,
                        principalTable: "PrintJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DicomImages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SeriesId = table.Column<int>(type: "INTEGER", nullable: false),
                    SopInstanceUid = table.Column<string>(type: "TEXT", nullable: false),
                    FilePath = table.Column<string>(type: "TEXT", nullable: false),
                    SopClassUid = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    PngPath = table.Column<string>(type: "TEXT", nullable: true),
                    ReceivedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DicomImages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DicomImages_Series_SeriesId",
                        column: x => x.SeriesId,
                        principalTable: "Series",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PrintImageBoxes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FilmBoxId = table.Column<int>(type: "INTEGER", nullable: true),
                    SopInstanceUid = table.Column<string>(type: "TEXT", nullable: false),
                    ReferencedImageSopUid = table.Column<string>(type: "TEXT", nullable: false),
                    FrameNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrintImageBoxes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PrintImageBoxes_FilmBoxes_FilmBoxId",
                        column: x => x.FilmBoxId,
                        principalTable: "FilmBoxes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DicomFrames",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DicomImageId = table.Column<int>(type: "INTEGER", nullable: false),
                    FrameIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    PngPath = table.Column<string>(type: "TEXT", nullable: true),
                    ExtractedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DicomFrames", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DicomFrames_DicomImages_DicomImageId",
                        column: x => x.DicomImageId,
                        principalTable: "DicomImages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DicomFrames_DicomImageId",
                table: "DicomFrames",
                column: "DicomImageId");

            migrationBuilder.CreateIndex(
                name: "IX_DicomImages_SeriesId",
                table: "DicomImages",
                column: "SeriesId");

            migrationBuilder.CreateIndex(
                name: "IX_DicomImages_SopInstanceUid",
                table: "DicomImages",
                column: "SopInstanceUid");

            migrationBuilder.CreateIndex(
                name: "IX_DicomImages_Source",
                table: "DicomImages",
                column: "Source");

            migrationBuilder.CreateIndex(
                name: "IX_FilmBoxes_PrintJobId",
                table: "FilmBoxes",
                column: "PrintJobId");

            migrationBuilder.CreateIndex(
                name: "IX_FilmBoxes_SopInstanceUid",
                table: "FilmBoxes",
                column: "SopInstanceUid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Patients_PatientId",
                table: "Patients",
                column: "PatientId");

            migrationBuilder.CreateIndex(
                name: "IX_PrintImageBoxes_FilmBoxId",
                table: "PrintImageBoxes",
                column: "FilmBoxId");

            migrationBuilder.CreateIndex(
                name: "IX_PrintImageBoxes_SopInstanceUid",
                table: "PrintImageBoxes",
                column: "SopInstanceUid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PrintJobs_PatientId",
                table: "PrintJobs",
                column: "PatientId");

            migrationBuilder.CreateIndex(
                name: "IX_PrintJobs_SopInstanceUid",
                table: "PrintJobs",
                column: "SopInstanceUid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PrintJobs_StudyId",
                table: "PrintJobs",
                column: "StudyId");

            migrationBuilder.CreateIndex(
                name: "IX_Series_SeriesInstanceUid",
                table: "Series",
                column: "SeriesInstanceUid");

            migrationBuilder.CreateIndex(
                name: "IX_Series_StudyId",
                table: "Series",
                column: "StudyId");

            migrationBuilder.CreateIndex(
                name: "IX_StorageCommitmentJobs_Status",
                table: "StorageCommitmentJobs",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Studies_LastUpdatedAt",
                table: "Studies",
                column: "LastUpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Studies_PatientId",
                table: "Studies",
                column: "PatientId");

            migrationBuilder.CreateIndex(
                name: "IX_Studies_Status",
                table: "Studies",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Studies_StudyInstanceUid",
                table: "Studies",
                column: "StudyInstanceUid");

            migrationBuilder.CreateIndex(
                name: "IX_WorklistEntries_PatientName",
                table: "WorklistEntries",
                column: "PatientName");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssociationAuditEntries");

            migrationBuilder.DropTable(
                name: "DicomFrames");

            migrationBuilder.DropTable(
                name: "PrintImageBoxes");

            migrationBuilder.DropTable(
                name: "StorageCommitmentJobs");

            migrationBuilder.DropTable(
                name: "WorklistEntries");

            migrationBuilder.DropTable(
                name: "DicomImages");

            migrationBuilder.DropTable(
                name: "FilmBoxes");

            migrationBuilder.DropTable(
                name: "Series");

            migrationBuilder.DropTable(
                name: "PrintJobs");

            migrationBuilder.DropTable(
                name: "Studies");

            migrationBuilder.DropTable(
                name: "Patients");
        }
    }
}
