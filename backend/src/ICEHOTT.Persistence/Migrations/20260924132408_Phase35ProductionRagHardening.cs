using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICEHOTT.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase35ProductionRagHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "knowledge_processing_jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    AvailableAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LockedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    LockedUntilUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_knowledge_processing_jobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_knowledge_processing_jobs_knowledge_documents_DocumentId_Wo~",
                        columns: x => new { x.DocumentId, x.WorkspaceId },
                        principalTable: "knowledge_documents",
                        principalColumns: new[] { "Id", "WorkspaceId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_processing_jobs_DocumentId_WorkspaceId",
                table: "knowledge_processing_jobs",
                columns: new[] { "DocumentId", "WorkspaceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_processing_jobs_Status_AvailableAtUtc",
                table: "knowledge_processing_jobs",
                columns: new[] { "Status", "AvailableAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_processing_jobs_WorkspaceId_CreatedAtUtc",
                table: "knowledge_processing_jobs",
                columns: new[] { "WorkspaceId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "knowledge_processing_jobs");
        }
    }
}
