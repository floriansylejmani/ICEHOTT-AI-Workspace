using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICEHOTT.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase5CDurableRunner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CancellationRequestedAtUtc",
                table: "workflow_runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CancellationRequestedByUserId",
                table: "workflow_runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflow_runs_CancellationRequestedByUserId",
                table: "workflow_runs",
                column: "CancellationRequestedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_runs_Status_CancellationRequestedAtUtc_LeaseExpire~",
                table: "workflow_runs",
                columns: new[] { "Status", "CancellationRequestedAtUtc", "LeaseExpiresAtUtc" });

            migrationBuilder.AddForeignKey(
                name: "FK_workflow_runs_users_CancellationRequestedByUserId",
                table: "workflow_runs",
                column: "CancellationRequestedByUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_workflow_runs_users_CancellationRequestedByUserId",
                table: "workflow_runs");

            migrationBuilder.DropIndex(
                name: "IX_workflow_runs_CancellationRequestedByUserId",
                table: "workflow_runs");

            migrationBuilder.DropIndex(
                name: "IX_workflow_runs_Status_CancellationRequestedAtUtc_LeaseExpire~",
                table: "workflow_runs");

            migrationBuilder.DropColumn(
                name: "CancellationRequestedAtUtc",
                table: "workflow_runs");

            migrationBuilder.DropColumn(
                name: "CancellationRequestedByUserId",
                table: "workflow_runs");
        }
    }
}
