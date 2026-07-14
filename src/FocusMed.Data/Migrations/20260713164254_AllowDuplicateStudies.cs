using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FocusMed.Data.Migrations
{
    /// <inheritdoc />
    public partial class AllowDuplicateStudies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Studies_StudyInstanceUid",
                table: "Studies");

            migrationBuilder.DropIndex(
                name: "IX_Series_SeriesInstanceUid",
                table: "Series");

            migrationBuilder.DropIndex(
                name: "IX_DicomImages_SopInstanceUid",
                table: "DicomImages");

            migrationBuilder.CreateIndex(
                name: "IX_Studies_StudyInstanceUid",
                table: "Studies",
                column: "StudyInstanceUid");

            migrationBuilder.CreateIndex(
                name: "IX_Series_SeriesInstanceUid",
                table: "Series",
                column: "SeriesInstanceUid");

            migrationBuilder.CreateIndex(
                name: "IX_DicomImages_SopInstanceUid",
                table: "DicomImages",
                column: "SopInstanceUid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Studies_StudyInstanceUid",
                table: "Studies");

            migrationBuilder.DropIndex(
                name: "IX_Series_SeriesInstanceUid",
                table: "Series");

            migrationBuilder.DropIndex(
                name: "IX_DicomImages_SopInstanceUid",
                table: "DicomImages");

            migrationBuilder.CreateIndex(
                name: "IX_Studies_StudyInstanceUid",
                table: "Studies",
                column: "StudyInstanceUid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Series_SeriesInstanceUid",
                table: "Series",
                column: "SeriesInstanceUid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DicomImages_SopInstanceUid",
                table: "DicomImages",
                column: "SopInstanceUid",
                unique: true);
        }
    }
}
