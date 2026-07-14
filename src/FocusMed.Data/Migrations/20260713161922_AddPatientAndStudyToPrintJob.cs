using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FocusMed.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPatientAndStudyToPrintJob : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PatientId",
                table: "PrintJobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StudyId",
                table: "PrintJobs",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PrintJobs_PatientId",
                table: "PrintJobs",
                column: "PatientId");

            migrationBuilder.CreateIndex(
                name: "IX_PrintJobs_StudyId",
                table: "PrintJobs",
                column: "StudyId");

            migrationBuilder.AddForeignKey(
                name: "FK_PrintJobs_Patients_PatientId",
                table: "PrintJobs",
                column: "PatientId",
                principalTable: "Patients",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_PrintJobs_Studies_StudyId",
                table: "PrintJobs",
                column: "StudyId",
                principalTable: "Studies",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PrintJobs_Patients_PatientId",
                table: "PrintJobs");

            migrationBuilder.DropForeignKey(
                name: "FK_PrintJobs_Studies_StudyId",
                table: "PrintJobs");

            migrationBuilder.DropIndex(
                name: "IX_PrintJobs_PatientId",
                table: "PrintJobs");

            migrationBuilder.DropIndex(
                name: "IX_PrintJobs_StudyId",
                table: "PrintJobs");

            migrationBuilder.DropColumn(
                name: "PatientId",
                table: "PrintJobs");

            migrationBuilder.DropColumn(
                name: "StudyId",
                table: "PrintJobs");
        }
    }
}
