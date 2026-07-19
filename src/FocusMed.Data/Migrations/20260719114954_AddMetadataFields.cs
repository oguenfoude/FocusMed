using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FocusMed.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMetadataFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AccessionNumber",
                table: "Studies",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "Studies",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InstitutionName",
                table: "Studies",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Manufacturer",
                table: "Studies",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReferringPhysicianName",
                table: "Studies",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BirthDate",
                table: "Patients",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Sex",
                table: "Patients",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AccessionNumber",
                table: "Studies");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "Studies");

            migrationBuilder.DropColumn(
                name: "InstitutionName",
                table: "Studies");

            migrationBuilder.DropColumn(
                name: "Manufacturer",
                table: "Studies");

            migrationBuilder.DropColumn(
                name: "ReferringPhysicianName",
                table: "Studies");

            migrationBuilder.DropColumn(
                name: "BirthDate",
                table: "Patients");

            migrationBuilder.DropColumn(
                name: "Sex",
                table: "Patients");
        }
    }
}
