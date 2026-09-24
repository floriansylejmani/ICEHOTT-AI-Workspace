using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICEHOTT.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase36ProfileInvariants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_embedding_profiles_Status",
                table: "embedding_profiles");

            migrationBuilder.CreateIndex(
                name: "IX_embedding_profiles_Status",
                table: "embedding_profiles",
                column: "Status",
                unique: true,
                filter: "\"Status\" = 'Active'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_embedding_profiles_Status",
                table: "embedding_profiles");

            migrationBuilder.CreateIndex(
                name: "IX_embedding_profiles_Status",
                table: "embedding_profiles",
                column: "Status");
        }
    }
}
