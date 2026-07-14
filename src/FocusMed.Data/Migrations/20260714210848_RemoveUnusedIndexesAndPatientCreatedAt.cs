using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FocusMed.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveUnusedIndexesAndPatientCreatedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorklistEntries_StudyInstanceUid",
                table: "WorklistEntries");

            migrationBuilder.DropIndex(
                name: "IX_Studies_CreatedAt",
                table: "Studies");

            migrationBuilder.DropIndex(
                name: "IX_AssociationAuditEntries_Timestamp",
                table: "AssociationAuditEntries");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "Patients");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "Patients",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.CreateIndex(
                name: "IX_WorklistEntries_StudyInstanceUid",
                table: "WorklistEntries",
                column: "StudyInstanceUid");

            migrationBuilder.CreateIndex(
                name: "IX_Studies_CreatedAt",
                table: "Studies",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AssociationAuditEntries_Timestamp",
                table: "AssociationAuditEntries",
                column: "Timestamp");
        }
    }
}
