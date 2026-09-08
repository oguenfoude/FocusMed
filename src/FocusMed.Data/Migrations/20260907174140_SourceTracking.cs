using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FocusMed.Data.Migrations
{
    /// <inheritdoc />
    public partial class SourceTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RemoteIp",
                table: "Studies",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RemoteIp",
                table: "PrintJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Studies_RemoteIp",
                table: "Studies",
                column: "RemoteIp");

            migrationBuilder.CreateIndex(
                name: "IX_PrintJobs_RemoteIp",
                table: "PrintJobs",
                column: "RemoteIp");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Studies_RemoteIp",
                table: "Studies");

            migrationBuilder.DropIndex(
                name: "IX_PrintJobs_RemoteIp",
                table: "PrintJobs");

            migrationBuilder.DropColumn(
                name: "RemoteIp",
                table: "Studies");

            migrationBuilder.DropColumn(
                name: "RemoteIp",
                table: "PrintJobs");
        }
    }
}
