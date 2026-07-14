using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FocusMed.Data.Migrations
{
    /// <inheritdoc />
    public partial class MakePrintJobAndFilmBoxIdsNullable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_FilmBoxes_PrintJobs_PrintJobId",
                table: "FilmBoxes");

            migrationBuilder.DropForeignKey(
                name: "FK_PrintImageBoxes_FilmBoxes_FilmBoxId",
                table: "PrintImageBoxes");

            migrationBuilder.AlterColumn<int>(
                name: "FilmBoxId",
                table: "PrintImageBoxes",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<int>(
                name: "PrintJobId",
                table: "FilmBoxes",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddForeignKey(
                name: "FK_FilmBoxes_PrintJobs_PrintJobId",
                table: "FilmBoxes",
                column: "PrintJobId",
                principalTable: "PrintJobs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PrintImageBoxes_FilmBoxes_FilmBoxId",
                table: "PrintImageBoxes",
                column: "FilmBoxId",
                principalTable: "FilmBoxes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_FilmBoxes_PrintJobs_PrintJobId",
                table: "FilmBoxes");

            migrationBuilder.DropForeignKey(
                name: "FK_PrintImageBoxes_FilmBoxes_FilmBoxId",
                table: "PrintImageBoxes");

            migrationBuilder.AlterColumn<int>(
                name: "FilmBoxId",
                table: "PrintImageBoxes",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "PrintJobId",
                table: "FilmBoxes",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_FilmBoxes_PrintJobs_PrintJobId",
                table: "FilmBoxes",
                column: "PrintJobId",
                principalTable: "PrintJobs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_PrintImageBoxes_FilmBoxes_FilmBoxId",
                table: "PrintImageBoxes",
                column: "FilmBoxId",
                principalTable: "FilmBoxes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
