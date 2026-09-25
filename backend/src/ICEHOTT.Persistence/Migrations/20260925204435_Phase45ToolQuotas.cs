using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICEHOTT.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase45ToolQuotas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tool_quota_counters",
                columns: table => new
                {
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToolName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    WindowStartUnixSeconds = table.Column<long>(type: "bigint", nullable: false),
                    Count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tool_quota_counters", x => new { x.WorkspaceId, x.ToolName });
                    table.CheckConstraint("CK_tool_quota_counters_Count", "\"Count\" >= 0");
                    table.CheckConstraint("CK_tool_quota_counters_WindowStartUnixSeconds", "\"WindowStartUnixSeconds\" >= 0");
                    table.ForeignKey(
                        name: "FK_tool_quota_counters_workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tool_quota_counters");
        }
    }
}
