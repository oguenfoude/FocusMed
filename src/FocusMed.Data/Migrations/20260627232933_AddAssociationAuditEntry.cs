using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FocusMed.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAssociationAuditEntry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AssociationAuditEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CallingAeTitle = table.Column<string>(type: "text", nullable: false),
                    RemoteIp = table.Column<string>(type: "text", nullable: false),
                    CalledAeTitle = table.Column<string>(type: "text", nullable: false),
                    RequestedSopClasses = table.Column<string>(type: "text", nullable: false),
                    Outcome = table.Column<int>(type: "integer", nullable: false),
                    DurationMs = table.Column<int>(type: "integer", nullable: true),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssociationAuditEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssociationAuditEntries_Timestamp",
                table: "AssociationAuditEntries",
                column: "Timestamp");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssociationAuditEntries");
        }
    }
}
