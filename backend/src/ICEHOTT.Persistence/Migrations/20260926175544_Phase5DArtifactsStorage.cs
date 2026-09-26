using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICEHOTT.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase5DArtifactsStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FailedAtUtc",
                table: "artifacts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                table: "artifacts",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StagingKey",
                table: "artifacts",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StorageDeletedAtUtc",
                table: "artifacts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_artifacts_Status_CreatedAtUtc",
                table: "artifacts",
                columns: new[] { "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_artifacts_Status_StorageDeletedAtUtc",
                table: "artifacts",
                columns: new[] { "Status", "StorageDeletedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_artifacts_WorkspaceId_IdempotencyKey",
                table: "artifacts",
                columns: new[] { "WorkspaceId", "IdempotencyKey" },
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_artifacts_Status_CreatedAtUtc",
                table: "artifacts");

            migrationBuilder.DropIndex(
                name: "IX_artifacts_Status_StorageDeletedAtUtc",
                table: "artifacts");

            migrationBuilder.DropIndex(
                name: "IX_artifacts_WorkspaceId_IdempotencyKey",
                table: "artifacts");

            migrationBuilder.DropColumn(
                name: "FailedAtUtc",
                table: "artifacts");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "artifacts");

            migrationBuilder.DropColumn(
                name: "StagingKey",
                table: "artifacts");

            migrationBuilder.DropColumn(
                name: "StorageDeletedAtUtc",
                table: "artifacts");
        }
    }
}
