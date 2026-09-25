using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICEHOTT.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase4AgentToolsExecution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tool_executions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToolName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    RiskLevel = table.Column<int>(type: "integer", nullable: false),
                    ArgumentsJson = table.Column<string>(type: "text", nullable: false),
                    ArgumentsHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ApprovedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ApprovedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ResultJson = table.Column<string>(type: "text", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tool_executions", x => x.Id);
                    table.UniqueConstraint("AK_tool_executions_Id_WorkspaceId", x => new { x.Id, x.WorkspaceId });
                    table.ForeignKey(
                        name: "FK_tool_executions_users_ApprovedByUserId",
                        column: x => x.ApprovedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_tool_executions_users_RequestedByUserId",
                        column: x => x.RequestedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_tool_executions_workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tool_execution_audit_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ExecutionId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<int>(type: "integer", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tool_execution_audit_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tool_execution_audit_events_tool_executions_ExecutionId_Wor~",
                        columns: x => new { x.ExecutionId, x.WorkspaceId },
                        principalTable: "tool_executions",
                        principalColumns: new[] { "Id", "WorkspaceId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_tool_execution_audit_events_users_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "workspace_audit_notes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToolExecutionId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workspace_audit_notes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_workspace_audit_notes_tool_executions_ToolExecutionId_Works~",
                        columns: x => new { x.ToolExecutionId, x.WorkspaceId },
                        principalTable: "tool_executions",
                        principalColumns: new[] { "Id", "WorkspaceId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_workspace_audit_notes_users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tool_execution_audit_events_ActorUserId",
                table: "tool_execution_audit_events",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_tool_execution_audit_events_ExecutionId_WorkspaceId",
                table: "tool_execution_audit_events",
                columns: new[] { "ExecutionId", "WorkspaceId" });

            migrationBuilder.CreateIndex(
                name: "IX_tool_execution_audit_events_WorkspaceId_ExecutionId_Occurre~",
                table: "tool_execution_audit_events",
                columns: new[] { "WorkspaceId", "ExecutionId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_tool_executions_ApprovedByUserId",
                table: "tool_executions",
                column: "ApprovedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_tool_executions_RequestedByUserId",
                table: "tool_executions",
                column: "RequestedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_tool_executions_WorkspaceId_RequestedAtUtc",
                table: "tool_executions",
                columns: new[] { "WorkspaceId", "RequestedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_tool_executions_WorkspaceId_ToolName_IdempotencyKey",
                table: "tool_executions",
                columns: new[] { "WorkspaceId", "ToolName", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workspace_audit_notes_CreatedByUserId",
                table: "workspace_audit_notes",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_workspace_audit_notes_ToolExecutionId",
                table: "workspace_audit_notes",
                column: "ToolExecutionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workspace_audit_notes_ToolExecutionId_WorkspaceId",
                table: "workspace_audit_notes",
                columns: new[] { "ToolExecutionId", "WorkspaceId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tool_execution_audit_events");

            migrationBuilder.DropTable(
                name: "workspace_audit_notes");

            migrationBuilder.DropTable(
                name: "tool_executions");
        }
    }
}
