using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FocusMed.Data.Migrations
{
    /// <inheritdoc />
    public partial class PrintMergeSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CallingAeTitle",
                table: "Studies",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CallingAeTitle",
                table: "PrintJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Studies_CallingAeTitle",
                table: "Studies",
                column: "CallingAeTitle");

            migrationBuilder.CreateIndex(
                name: "IX_PrintJobs_CallingAeTitle_CreatedAt",
                table: "PrintJobs",
                columns: new[] { "CallingAeTitle", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Studies_CallingAeTitle",
                table: "Studies");

            migrationBuilder.DropIndex(
                name: "IX_PrintJobs_CallingAeTitle_CreatedAt",
                table: "PrintJobs");

            migrationBuilder.DropColumn(
                name: "CallingAeTitle",
                table: "Studies");

            migrationBuilder.DropColumn(
                name: "CallingAeTitle",
                table: "PrintJobs");
        }
    }
}
