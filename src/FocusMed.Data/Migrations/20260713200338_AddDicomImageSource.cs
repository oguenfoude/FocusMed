using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FocusMed.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDicomImageSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "DicomImages",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_DicomImages_Source",
                table: "DicomImages",
                column: "Source");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DicomImages_Source",
                table: "DicomImages");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "DicomImages");
        }
    }
}
