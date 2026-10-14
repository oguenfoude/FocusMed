using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FocusMed.Data.Migrations
{
    /// <inheritdoc />
    public partial class StudyHotPathIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Studies_CallingAeTitle_CreatedAt",
                table: "Studies",
                columns: new[] { "CallingAeTitle", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Studies_Status_LastUpdatedAt",
                table: "Studies",
                columns: new[] { "Status", "LastUpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Studies_StudyInstanceUid_Status",
                table: "Studies",
                columns: new[] { "StudyInstanceUid", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Studies_CallingAeTitle_CreatedAt",
                table: "Studies");

            migrationBuilder.DropIndex(
                name: "IX_Studies_Status_LastUpdatedAt",
                table: "Studies");

            migrationBuilder.DropIndex(
                name: "IX_Studies_StudyInstanceUid_Status",
                table: "Studies");
        }
    }
}
