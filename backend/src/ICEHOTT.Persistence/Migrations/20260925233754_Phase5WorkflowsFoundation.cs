using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICEHOTT.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase5WorkflowsFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "workflow_definitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    MinimumRunRole = table.Column<int>(type: "integer", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_definitions", x => x.Id);
                    table.UniqueConstraint("AK_workflow_definitions_Id_WorkspaceId", x => new { x.Id, x.WorkspaceId });
                    table.CheckConstraint("CK_workflow_definitions_MinimumRunRole", "\"MinimumRunRole\" BETWEEN 1 AND 3");
                    table.ForeignKey(
                        name: "FK_workflow_definitions_users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workflow_definitions_workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "workflow_versions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkflowDefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    DefinitionJson = table.Column<string>(type: "text", nullable: false),
                    DefinitionHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActivatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RetiredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_versions", x => x.Id);
                    table.UniqueConstraint("AK_workflow_versions_Id_WorkflowDefinitionId_WorkspaceId", x => new { x.Id, x.WorkflowDefinitionId, x.WorkspaceId });
                    table.UniqueConstraint("AK_workflow_versions_Id_WorkspaceId", x => new { x.Id, x.WorkspaceId });
                    table.CheckConstraint("CK_workflow_versions_VersionNumber", "\"VersionNumber\" >= 1");
                    table.ForeignKey(
                        name: "FK_workflow_versions_users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workflow_versions_workflow_definitions_WorkflowDefinitionId~",
                        columns: x => new { x.WorkflowDefinitionId, x.WorkspaceId },
                        principalTable: "workflow_definitions",
                        principalColumns: new[] { "Id", "WorkspaceId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "workflow_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkflowDefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkflowVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RunAsUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    CurrentStepKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeaseOwnerId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeaseGeneration = table.Column<int>(type: "integer", nullable: false),
                    WaitReason = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ResumeAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_runs", x => x.Id);
                    table.UniqueConstraint("AK_workflow_runs_Id_WorkflowVersionId_WorkspaceId", x => new { x.Id, x.WorkflowVersionId, x.WorkspaceId });
                    table.UniqueConstraint("AK_workflow_runs_Id_WorkspaceId", x => new { x.Id, x.WorkspaceId });
                    table.CheckConstraint("CK_workflow_runs_LeaseGeneration", "\"LeaseGeneration\" >= 0");
                    table.ForeignKey(
                        name: "FK_workflow_runs_users_RequestedByUserId",
                        column: x => x.RequestedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workflow_runs_users_RunAsUserId",
                        column: x => x.RunAsUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workflow_runs_workflow_definitions_WorkflowDefinitionId_Wor~",
                        columns: x => new { x.WorkflowDefinitionId, x.WorkspaceId },
                        principalTable: "workflow_definitions",
                        principalColumns: new[] { "Id", "WorkspaceId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workflow_runs_workflow_versions_WorkflowVersionId_WorkflowD~",
                        columns: x => new { x.WorkflowVersionId, x.WorkflowDefinitionId, x.WorkspaceId },
                        principalTable: "workflow_versions",
                        principalColumns: new[] { "Id", "WorkflowDefinitionId", "WorkspaceId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "workflow_triggers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkflowDefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkflowVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ScheduleExpression = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TimeZoneId = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    RunAsUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    NextRunAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastRunAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_triggers", x => x.Id);
                    table.UniqueConstraint("AK_workflow_triggers_Id_WorkflowVersionId_WorkspaceId", x => new { x.Id, x.WorkflowVersionId, x.WorkspaceId });
                    table.UniqueConstraint("AK_workflow_triggers_Id_WorkspaceId", x => new { x.Id, x.WorkspaceId });
                    table.ForeignKey(
                        name: "FK_workflow_triggers_users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workflow_triggers_users_RunAsUserId",
                        column: x => x.RunAsUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workflow_triggers_workflow_definitions_WorkflowDefinitionId~",
                        columns: x => new { x.WorkflowDefinitionId, x.WorkspaceId },
                        principalTable: "workflow_definitions",
                        principalColumns: new[] { "Id", "WorkspaceId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workflow_triggers_workflow_versions_WorkflowVersionId_Workf~",
                        columns: x => new { x.WorkflowVersionId, x.WorkflowDefinitionId, x.WorkspaceId },
                        principalTable: "workflow_versions",
                        principalColumns: new[] { "Id", "WorkflowDefinitionId", "WorkspaceId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "workflow_step_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkflowRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    StepKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Attempt = table.Column<int>(type: "integer", nullable: false),
                    StepType = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    InputJson = table.Column<string>(type: "text", nullable: false),
                    OutputJson = table.Column<string>(type: "text", nullable: true),
                    ToolExecutionId = table.Column<Guid>(type: "uuid", nullable: true),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_step_runs", x => x.Id);
                    table.UniqueConstraint("AK_workflow_step_runs_Id_WorkflowRunId_WorkspaceId", x => new { x.Id, x.WorkflowRunId, x.WorkspaceId });
                    table.UniqueConstraint("AK_workflow_step_runs_Id_WorkspaceId", x => new { x.Id, x.WorkspaceId });
                    table.CheckConstraint("CK_workflow_step_runs_Attempt", "\"Attempt\" >= 1");
                    table.ForeignKey(
                        name: "FK_workflow_step_runs_tool_executions_ToolExecutionId_Workspac~",
                        columns: x => new { x.ToolExecutionId, x.WorkspaceId },
                        principalTable: "tool_executions",
                        principalColumns: new[] { "Id", "WorkspaceId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workflow_step_runs_workflow_runs_WorkflowRunId_WorkspaceId",
                        columns: x => new { x.WorkflowRunId, x.WorkspaceId },
                        principalTable: "workflow_runs",
                        principalColumns: new[] { "Id", "WorkspaceId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "workflow_trigger_fires",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    TriggerId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkflowVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScheduledForUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FireKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    WorkflowRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_trigger_fires", x => x.Id);
                    table.UniqueConstraint("AK_workflow_trigger_fires_Id_WorkspaceId", x => new { x.Id, x.WorkspaceId });
                    table.ForeignKey(
                        name: "FK_workflow_trigger_fires_workflow_runs_WorkflowRunId_Workflow~",
                        columns: x => new { x.WorkflowRunId, x.WorkflowVersionId, x.WorkspaceId },
                        principalTable: "workflow_runs",
                        principalColumns: new[] { "Id", "WorkflowVersionId", "WorkspaceId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workflow_trigger_fires_workflow_triggers_TriggerId_Workflow~",
                        columns: x => new { x.TriggerId, x.WorkflowVersionId, x.WorkspaceId },
                        principalTable: "workflow_triggers",
                        principalColumns: new[] { "Id", "WorkflowVersionId", "WorkspaceId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "artifacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkflowRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    StepRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    FileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_artifacts", x => x.Id);
                    table.UniqueConstraint("AK_artifacts_Id_WorkspaceId", x => new { x.Id, x.WorkspaceId });
                    table.CheckConstraint("CK_artifacts_SizeBytes", "\"SizeBytes\" >= 0");
                    table.ForeignKey(
                        name: "FK_artifacts_users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_artifacts_workflow_runs_WorkflowRunId_WorkspaceId",
                        columns: x => new { x.WorkflowRunId, x.WorkspaceId },
                        principalTable: "workflow_runs",
                        principalColumns: new[] { "Id", "WorkspaceId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_artifacts_workflow_step_runs_StepRunId_WorkflowRunId_Worksp~",
                        columns: x => new { x.StepRunId, x.WorkflowRunId, x.WorkspaceId },
                        principalTable: "workflow_step_runs",
                        principalColumns: new[] { "Id", "WorkflowRunId", "WorkspaceId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_artifacts_workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "workflow_audit_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkflowDefinitionId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkflowRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkflowStepRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkflowTriggerId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    EventType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    DetailJson = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_audit_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_workflow_audit_events_users_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workflow_audit_events_workflow_definitions_WorkflowDefiniti~",
                        columns: x => new { x.WorkflowDefinitionId, x.WorkspaceId },
                        principalTable: "workflow_definitions",
                        principalColumns: new[] { "Id", "WorkspaceId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workflow_audit_events_workflow_runs_WorkflowRunId_Workspace~",
                        columns: x => new { x.WorkflowRunId, x.WorkspaceId },
                        principalTable: "workflow_runs",
                        principalColumns: new[] { "Id", "WorkspaceId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workflow_audit_events_workflow_step_runs_WorkflowStepRunId_~",
                        columns: x => new { x.WorkflowStepRunId, x.WorkspaceId },
                        principalTable: "workflow_step_runs",
                        principalColumns: new[] { "Id", "WorkspaceId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workflow_audit_events_workflow_triggers_WorkflowTriggerId_W~",
                        columns: x => new { x.WorkflowTriggerId, x.WorkspaceId },
                        principalTable: "workflow_triggers",
                        principalColumns: new[] { "Id", "WorkspaceId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workflow_audit_events_workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "workflow_checkpoints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkflowRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    StepRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    MinimumApproverRole = table.Column<int>(type: "integer", nullable: false),
                    RequiresDifferentApprover = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DecidedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DecidedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_checkpoints", x => x.Id);
                    table.UniqueConstraint("AK_workflow_checkpoints_Id_WorkspaceId", x => new { x.Id, x.WorkspaceId });
                    table.CheckConstraint("CK_workflow_checkpoints_MinimumApproverRole", "\"MinimumApproverRole\" BETWEEN 1 AND 3");
                    table.ForeignKey(
                        name: "FK_workflow_checkpoints_users_DecidedByUserId",
                        column: x => x.DecidedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workflow_checkpoints_users_RequestedByUserId",
                        column: x => x.RequestedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workflow_checkpoints_workflow_runs_WorkflowRunId_WorkspaceId",
                        columns: x => new { x.WorkflowRunId, x.WorkspaceId },
                        principalTable: "workflow_runs",
                        principalColumns: new[] { "Id", "WorkspaceId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_workflow_checkpoints_workflow_step_runs_StepRunId_WorkflowR~",
                        columns: x => new { x.StepRunId, x.WorkflowRunId, x.WorkspaceId },
                        principalTable: "workflow_step_runs",
                        principalColumns: new[] { "Id", "WorkflowRunId", "WorkspaceId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_artifacts_CreatedByUserId",
                table: "artifacts",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_artifacts_StepRunId_WorkflowRunId_WorkspaceId",
                table: "artifacts",
                columns: new[] { "StepRunId", "WorkflowRunId", "WorkspaceId" });

            migrationBuilder.CreateIndex(
                name: "IX_artifacts_StorageKey",
                table: "artifacts",
                column: "StorageKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_artifacts_WorkflowRunId_WorkspaceId",
                table: "artifacts",
                columns: new[] { "WorkflowRunId", "WorkspaceId" });

            migrationBuilder.CreateIndex(
                name: "IX_artifacts_WorkspaceId_CreatedAtUtc",
                table: "artifacts",
                columns: new[] { "WorkspaceId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_audit_events_ActorUserId",
                table: "workflow_audit_events",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_audit_events_WorkflowDefinitionId_WorkspaceId",
                table: "workflow_audit_events",
                columns: new[] { "WorkflowDefinitionId", "WorkspaceId" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_audit_events_WorkflowRunId_WorkspaceId",
                table: "workflow_audit_events",
                columns: new[] { "WorkflowRunId", "WorkspaceId" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_audit_events_WorkflowStepRunId_WorkspaceId",
                table: "workflow_audit_events",
                columns: new[] { "WorkflowStepRunId", "WorkspaceId" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_audit_events_WorkflowTriggerId_WorkspaceId",
                table: "workflow_audit_events",
                columns: new[] { "WorkflowTriggerId", "WorkspaceId" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_audit_events_WorkspaceId_CreatedAtUtc",
                table: "workflow_audit_events",
                columns: new[] { "WorkspaceId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_audit_events_WorkspaceId_WorkflowRunId_CreatedAtUtc",
                table: "workflow_audit_events",
                columns: new[] { "WorkspaceId", "WorkflowRunId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_checkpoints_DecidedByUserId",
                table: "workflow_checkpoints",
                column: "DecidedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_checkpoints_RequestedByUserId",
                table: "workflow_checkpoints",
                column: "RequestedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_checkpoints_StepRunId",
                table: "workflow_checkpoints",
                column: "StepRunId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflow_checkpoints_StepRunId_WorkflowRunId_WorkspaceId",
                table: "workflow_checkpoints",
                columns: new[] { "StepRunId", "WorkflowRunId", "WorkspaceId" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_checkpoints_WorkflowRunId_WorkspaceId",
                table: "workflow_checkpoints",
                columns: new[] { "WorkflowRunId", "WorkspaceId" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_checkpoints_WorkspaceId_Status_CreatedAtUtc",
                table: "workflow_checkpoints",
                columns: new[] { "WorkspaceId", "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_definitions_CreatedByUserId",
                table: "workflow_definitions",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_definitions_WorkspaceId_Status_CreatedAtUtc",
                table: "workflow_definitions",
                columns: new[] { "WorkspaceId", "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_runs_RequestedByUserId",
                table: "workflow_runs",
                column: "RequestedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_runs_RunAsUserId",
                table: "workflow_runs",
                column: "RunAsUserId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_runs_Status_LeaseExpiresAtUtc",
                table: "workflow_runs",
                columns: new[] { "Status", "LeaseExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_runs_Status_ResumeAtUtc",
                table: "workflow_runs",
                columns: new[] { "Status", "ResumeAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_runs_WorkflowDefinitionId_WorkspaceId",
                table: "workflow_runs",
                columns: new[] { "WorkflowDefinitionId", "WorkspaceId" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_runs_WorkflowVersionId_WorkflowDefinitionId_Worksp~",
                table: "workflow_runs",
                columns: new[] { "WorkflowVersionId", "WorkflowDefinitionId", "WorkspaceId" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_runs_WorkspaceId_CreatedAtUtc",
                table: "workflow_runs",
                columns: new[] { "WorkspaceId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_runs_WorkspaceId_WorkflowDefinitionId_IdempotencyK~",
                table: "workflow_runs",
                columns: new[] { "WorkspaceId", "WorkflowDefinitionId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflow_step_runs_ToolExecutionId_WorkspaceId",
                table: "workflow_step_runs",
                columns: new[] { "ToolExecutionId", "WorkspaceId" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_step_runs_WorkflowRunId_StepKey_Attempt",
                table: "workflow_step_runs",
                columns: new[] { "WorkflowRunId", "StepKey", "Attempt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflow_step_runs_WorkflowRunId_WorkspaceId",
                table: "workflow_step_runs",
                columns: new[] { "WorkflowRunId", "WorkspaceId" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_step_runs_WorkspaceId_Status_NextAttemptAtUtc",
                table: "workflow_step_runs",
                columns: new[] { "WorkspaceId", "Status", "NextAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_trigger_fires_FireKey",
                table: "workflow_trigger_fires",
                column: "FireKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflow_trigger_fires_TriggerId_ScheduledForUtc",
                table: "workflow_trigger_fires",
                columns: new[] { "TriggerId", "ScheduledForUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflow_trigger_fires_TriggerId_WorkflowVersionId_Workspac~",
                table: "workflow_trigger_fires",
                columns: new[] { "TriggerId", "WorkflowVersionId", "WorkspaceId" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_trigger_fires_WorkflowRunId_WorkflowVersionId_Work~",
                table: "workflow_trigger_fires",
                columns: new[] { "WorkflowRunId", "WorkflowVersionId", "WorkspaceId" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_trigger_fires_WorkspaceId_Status_ScheduledForUtc",
                table: "workflow_trigger_fires",
                columns: new[] { "WorkspaceId", "Status", "ScheduledForUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_triggers_CreatedByUserId",
                table: "workflow_triggers",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_triggers_RunAsUserId",
                table: "workflow_triggers",
                column: "RunAsUserId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_triggers_WorkflowDefinitionId_WorkspaceId",
                table: "workflow_triggers",
                columns: new[] { "WorkflowDefinitionId", "WorkspaceId" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_triggers_WorkflowVersionId_WorkflowDefinitionId_Wo~",
                table: "workflow_triggers",
                columns: new[] { "WorkflowVersionId", "WorkflowDefinitionId", "WorkspaceId" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_triggers_WorkspaceId_Enabled_NextRunAtUtc",
                table: "workflow_triggers",
                columns: new[] { "WorkspaceId", "Enabled", "NextRunAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_versions_Active",
                table: "workflow_versions",
                column: "WorkflowDefinitionId",
                unique: true,
                filter: "\"Status\" = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_versions_CreatedByUserId",
                table: "workflow_versions",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_versions_WorkflowDefinitionId_VersionNumber",
                table: "workflow_versions",
                columns: new[] { "WorkflowDefinitionId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflow_versions_WorkflowDefinitionId_WorkspaceId",
                table: "workflow_versions",
                columns: new[] { "WorkflowDefinitionId", "WorkspaceId" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_versions_WorkspaceId_WorkflowDefinitionId_CreatedA~",
                table: "workflow_versions",
                columns: new[] { "WorkspaceId", "WorkflowDefinitionId", "CreatedAtUtc" });

            if (ActiveProvider.Contains("Npgsql"))
            {
                migrationBuilder.Sql("""
                    CREATE OR REPLACE FUNCTION icehott_reject_workflow_audit_mutation()
                    RETURNS trigger
                    LANGUAGE plpgsql
                    AS $$
                    BEGIN
                        RAISE EXCEPTION 'workflow audit rows are append-only'
                            USING ERRCODE = '55000';
                    END;
                    $$;

                    CREATE TRIGGER tr_workflow_audit_events_append_only
                    BEFORE UPDATE OR DELETE ON workflow_audit_events
                    FOR EACH ROW EXECUTE FUNCTION icehott_reject_workflow_audit_mutation();
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (ActiveProvider.Contains("Npgsql"))
            {
                migrationBuilder.Sql("""
                    DROP TRIGGER IF EXISTS tr_workflow_audit_events_append_only
                        ON workflow_audit_events;
                    DROP FUNCTION IF EXISTS icehott_reject_workflow_audit_mutation();
                    """);
            }

            migrationBuilder.DropTable(
                name: "artifacts");

            migrationBuilder.DropTable(
                name: "workflow_audit_events");

            migrationBuilder.DropTable(
                name: "workflow_checkpoints");

            migrationBuilder.DropTable(
                name: "workflow_trigger_fires");

            migrationBuilder.DropTable(
                name: "workflow_step_runs");

            migrationBuilder.DropTable(
                name: "workflow_triggers");

            migrationBuilder.DropTable(
                name: "workflow_runs");

            migrationBuilder.DropTable(
                name: "workflow_versions");

            migrationBuilder.DropTable(
                name: "workflow_definitions");
        }
    }
}
