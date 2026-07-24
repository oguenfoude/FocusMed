using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FocusMed.Data.Migrations
{
    /// <inheritdoc />
    public partial class UpdateUserPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EnableCenterStaple",
                table: "UserPreferences");

            migrationBuilder.DropColumn(
                name: "EnableDuplex",
                table: "UserPreferences");

            migrationBuilder.AddColumn<string>(
                name: "PreferredInputBin",
                table: "UserPreferences",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreferredOutputColor",
                table: "UserPreferences",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PreferredInputBin",
                table: "UserPreferences");

            migrationBuilder.DropColumn(
                name: "PreferredOutputColor",
                table: "UserPreferences");

            migrationBuilder.AddColumn<bool>(
                name: "EnableCenterStaple",
                table: "UserPreferences",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "EnableDuplex",
                table: "UserPreferences",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
