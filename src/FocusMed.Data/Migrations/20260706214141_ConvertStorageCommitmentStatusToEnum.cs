using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FocusMed.Data.Migrations
{
    /// <inheritdoc />
    public partial class ConvertStorageCommitmentStatusToEnum : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                UPDATE ""StorageCommitmentJobs""
                SET ""Status"" = CASE ""Status""
                    WHEN 'Completed' THEN 1
                    WHEN 'Failed' THEN 2
                    ELSE 0
                END");

            migrationBuilder.Sql(@"ALTER TABLE ""StorageCommitmentJobs"" ALTER COLUMN ""Status"" TYPE integer USING ""Status""::integer");

            migrationBuilder.CreateIndex(
                name: "IX_WorklistEntries_PatientName",
                table: "WorklistEntries",
                column: "PatientName");

            migrationBuilder.CreateIndex(
                name: "IX_WorklistEntries_StudyInstanceUid",
                table: "WorklistEntries",
                column: "StudyInstanceUid");

            migrationBuilder.CreateIndex(
                name: "IX_StorageCommitmentJobs_Status",
                table: "StorageCommitmentJobs",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorklistEntries_PatientName",
                table: "WorklistEntries");

            migrationBuilder.DropIndex(
                name: "IX_WorklistEntries_StudyInstanceUid",
                table: "WorklistEntries");

            migrationBuilder.DropIndex(
                name: "IX_StorageCommitmentJobs_Status",
                table: "StorageCommitmentJobs");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "StorageCommitmentJobs",
                type: "text",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer");
        }
    }
}
