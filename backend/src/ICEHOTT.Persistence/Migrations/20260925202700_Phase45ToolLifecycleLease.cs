using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICEHOTT.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase45ToolLifecycleLease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeadlineAtUtc",
                table: "tool_executions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LeaseExpiresAtUtc",
                table: "tool_executions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LeaseOwnerId",
                table: "tool_executions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_tool_executions_Status_LeaseExpiresAtUtc",
                table: "tool_executions",
                columns: new[] { "Status", "LeaseExpiresAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_tool_executions_Status_LeaseExpiresAtUtc",
                table: "tool_executions");

            migrationBuilder.DropColumn(
                name: "DeadlineAtUtc",
                table: "tool_executions");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresAtUtc",
                table: "tool_executions");

            migrationBuilder.DropColumn(
                name: "LeaseOwnerId",
                table: "tool_executions");
        }
    }
}
