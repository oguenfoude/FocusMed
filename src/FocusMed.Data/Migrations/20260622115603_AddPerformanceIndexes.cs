using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FocusMed.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPerformanceIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Studies_CreatedAt",
                table: "Studies",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Studies_Status",
                table: "Studies",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Patients_PatientId",
                table: "Patients",
                column: "PatientId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Studies_CreatedAt",
                table: "Studies");

            migrationBuilder.DropIndex(
                name: "IX_Studies_Status",
                table: "Studies");

            migrationBuilder.DropIndex(
                name: "IX_Patients_PatientId",
                table: "Patients");
        }
    }
}
