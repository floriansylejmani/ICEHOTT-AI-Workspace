using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICEHOTT.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase45ToolPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PolicyMaxArgumentLength",
                table: "tool_executions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PolicyMinimumApproverRole",
                table: "tool_executions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PolicyMinimumRequesterRole",
                table: "tool_executions",
                type: "integer",
                nullable: false,
                defaultValue: 1); // Member; see backfill below

            migrationBuilder.AddColumn<bool>(
                name: "PolicyRequiresApproval",
                table: "tool_executions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "PolicyVersion",
                table: "tool_executions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // D4 backfill: rows admitted before Phase 4.5 get policy version 0
            // with the built-in values of their tool. Anything approval-gated
            // (the built-in SensitiveWrite tool, or any row still pending
            // approval) snapshots requester Admin / approver Admin /
            // requires approval. Must run before the check constraints below.
            migrationBuilder.Sql(
                """
                UPDATE tool_executions
                SET "PolicyMinimumRequesterRole" = 2,
                    "PolicyMinimumApproverRole" = 2,
                    "PolicyRequiresApproval" = TRUE
                WHERE "ToolName" = 'workspace.audit-note.create'
                   OR "Status" = 1;
                """);

            migrationBuilder.CreateTable(
                name: "tool_policies",
                columns: table => new
                {
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToolName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    MinimumRequesterRole = table.Column<int>(type: "integer", nullable: true),
                    MinimumApproverRole = table.Column<int>(type: "integer", nullable: true),
                    RequiresApproval = table.Column<bool>(type: "boolean", nullable: false),
                    MaxArgumentLength = table.Column<int>(type: "integer", nullable: true),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tool_policies", x => new { x.WorkspaceId, x.ToolName });
                    table.CheckConstraint("CK_tool_policies_MaxArgumentLength", "\"MaxArgumentLength\" IS NULL OR \"MaxArgumentLength\" > 0");
                    table.CheckConstraint("CK_tool_policies_MinimumApproverRole", "\"MinimumApproverRole\" IS NULL OR \"MinimumApproverRole\" BETWEEN 2 AND 3");
                    table.CheckConstraint("CK_tool_policies_MinimumRequesterRole", "\"MinimumRequesterRole\" IS NULL OR \"MinimumRequesterRole\" BETWEEN 1 AND 3");
                    table.CheckConstraint("CK_tool_policies_Version", "\"Version\" >= 0");
                    table.ForeignKey(
                        name: "FK_tool_policies_users_UpdatedByUserId",
                        column: x => x.UpdatedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_tool_policies_workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tool_policy_audit_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToolName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PreviousVersion = table.Column<int>(type: "integer", nullable: false),
                    NewVersion = table.Column<int>(type: "integer", nullable: false),
                    PreviousPolicyJson = table.Column<string>(type: "text", nullable: true),
                    NewPolicyJson = table.Column<string>(type: "text", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tool_policy_audit_events", x => x.Id);
                    table.CheckConstraint("CK_tool_policy_audit_events_Versions", "\"NewVersion\" = \"PreviousVersion\" + 1");
                    table.ForeignKey(
                        name: "FK_tool_policy_audit_events_users_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_tool_policy_audit_events_workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_tool_executions_PolicyApprover",
                table: "tool_executions",
                sql: "(\"PolicyRequiresApproval\" AND \"PolicyMinimumApproverRole\" IS NOT NULL AND \"PolicyMinimumApproverRole\" BETWEEN 2 AND 3) OR (NOT \"PolicyRequiresApproval\" AND \"PolicyMinimumApproverRole\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_tool_executions_PolicyMinimumRequesterRole",
                table: "tool_executions",
                sql: "\"PolicyMinimumRequesterRole\" BETWEEN 1 AND 3");

            migrationBuilder.AddCheckConstraint(
                name: "CK_tool_executions_PolicyVersion",
                table: "tool_executions",
                sql: "\"PolicyVersion\" >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_tool_policies_UpdatedByUserId",
                table: "tool_policies",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_tool_policy_audit_events_ActorUserId",
                table: "tool_policy_audit_events",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_tool_policy_audit_events_WorkspaceId_ToolName_NewVersion",
                table: "tool_policy_audit_events",
                columns: new[] { "WorkspaceId", "ToolName", "NewVersion" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tool_policies");

            migrationBuilder.DropTable(
                name: "tool_policy_audit_events");

            migrationBuilder.DropCheckConstraint(
                name: "CK_tool_executions_PolicyApprover",
                table: "tool_executions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_tool_executions_PolicyMinimumRequesterRole",
                table: "tool_executions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_tool_executions_PolicyVersion",
                table: "tool_executions");

            migrationBuilder.DropColumn(
                name: "PolicyMaxArgumentLength",
                table: "tool_executions");

            migrationBuilder.DropColumn(
                name: "PolicyMinimumApproverRole",
                table: "tool_executions");

            migrationBuilder.DropColumn(
                name: "PolicyMinimumRequesterRole",
                table: "tool_executions");

            migrationBuilder.DropColumn(
                name: "PolicyRequiresApproval",
                table: "tool_executions");

            migrationBuilder.DropColumn(
                name: "PolicyVersion",
                table: "tool_executions");
        }
    }
}
