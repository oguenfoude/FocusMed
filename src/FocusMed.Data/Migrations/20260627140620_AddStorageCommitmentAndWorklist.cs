using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FocusMed.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStorageCommitmentAndWorklist : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Patients",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PatientId = table.Column<string>(type: "text", nullable: false),
                    PatientName = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Patients", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PrintJobs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SopInstanceUid = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    NumberOfCopies = table.Column<int>(type: "integer", nullable: false),
                    PrintPriority = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrintJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StorageCommitmentJobs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TransactionUid = table.Column<string>(type: "text", nullable: false),
                    RequestedSopInstanceUids = table.Column<string>(type: "text", nullable: false),
                    CallingAet = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorageCommitmentJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorklistEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PatientId = table.Column<string>(type: "text", nullable: false),
                    PatientName = table.Column<string>(type: "text", nullable: false),
                    AccessionNumber = table.Column<string>(type: "text", nullable: false),
                    Modality = table.Column<string>(type: "text", nullable: false),
                    ScheduledProcedureStepStartDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ScheduledProcedureStepId = table.Column<string>(type: "text", nullable: false),
                    RequestedProcedureId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorklistEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Studies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PatientId = table.Column<int>(type: "integer", nullable: false),
                    StudyInstanceUid = table.Column<string>(type: "text", nullable: false),
                    StudyDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                name: "FilmBoxes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PrintJobId = table.Column<int>(type: "integer", nullable: false),
                    SopInstanceUid = table.Column<string>(type: "text", nullable: false),
                    FilmSize = table.Column<string>(type: "text", nullable: false),
                    Orientation = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FilmBoxes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FilmBoxes_PrintJobs_PrintJobId",
                        column: x => x.PrintJobId,
                        principalTable: "PrintJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Series",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StudyId = table.Column<int>(type: "integer", nullable: false),
                    SeriesInstanceUid = table.Column<string>(type: "text", nullable: false),
                    Modality = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                name: "PrintImageBoxes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FilmBoxId = table.Column<int>(type: "integer", nullable: false),
                    SopInstanceUid = table.Column<string>(type: "text", nullable: false),
                    ReferencedImageSopUid = table.Column<string>(type: "text", nullable: false),
                    FrameNumber = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrintImageBoxes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PrintImageBoxes_FilmBoxes_FilmBoxId",
                        column: x => x.FilmBoxId,
                        principalTable: "FilmBoxes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DicomImages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SeriesId = table.Column<int>(type: "integer", nullable: false),
                    SopInstanceUid = table.Column<string>(type: "text", nullable: false),
                    FilePath = table.Column<string>(type: "text", nullable: false),
                    PngPath = table.Column<string>(type: "text", nullable: true),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                name: "DicomFrames",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DicomImageId = table.Column<int>(type: "integer", nullable: false),
                    FrameIndex = table.Column<int>(type: "integer", nullable: false),
                    PngPath = table.Column<string>(type: "text", nullable: false),
                    ExtractedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                column: "SopInstanceUid",
                unique: true);

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
                name: "IX_PrintJobs_SopInstanceUid",
                table: "PrintJobs",
                column: "SopInstanceUid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Series_SeriesInstanceUid",
                table: "Series",
                column: "SeriesInstanceUid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Series_StudyId",
                table: "Series",
                column: "StudyId");

            migrationBuilder.CreateIndex(
                name: "IX_Studies_CreatedAt",
                table: "Studies",
                column: "CreatedAt");

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
                column: "StudyInstanceUid",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
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
