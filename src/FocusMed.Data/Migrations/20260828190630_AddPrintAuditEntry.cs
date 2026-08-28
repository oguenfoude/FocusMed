using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FocusMed.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPrintAuditEntry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PrintAuditEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StudyId = table.Column<int>(type: "INTEGER", nullable: true),
                    PatientName = table.Column<string>(type: "TEXT", nullable: true),
                    ProfileName = table.Column<string>(type: "TEXT", nullable: true),
                    PrintMode = table.Column<string>(type: "TEXT", nullable: true),
                    Copies = table.Column<int>(type: "INTEGER", nullable: false),
                    PagesPrinted = table.Column<int>(type: "INTEGER", nullable: false),
                    PaperSize = table.Column<string>(type: "TEXT", nullable: true),
                    Success = table.Column<bool>(type: "INTEGER", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    PrinterName = table.Column<string>(type: "TEXT", nullable: true),
                    PrintedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrintAuditEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PrintAuditEntries_PrintedAt",
                table: "PrintAuditEntries",
                column: "PrintedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PrintAuditEntries");
        }
    }
}
