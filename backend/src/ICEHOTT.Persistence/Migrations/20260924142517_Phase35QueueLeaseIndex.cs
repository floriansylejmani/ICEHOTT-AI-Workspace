using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICEHOTT.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase35QueueLeaseIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_knowledge_processing_jobs_Status_LockedUntilUtc",
                table: "knowledge_processing_jobs",
                columns: new[] { "Status", "LockedUntilUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_knowledge_processing_jobs_Status_LockedUntilUtc",
                table: "knowledge_processing_jobs");
        }
    }
}
