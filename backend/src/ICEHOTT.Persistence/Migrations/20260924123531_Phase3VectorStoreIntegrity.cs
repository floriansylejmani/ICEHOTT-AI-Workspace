using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICEHOTT.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase3VectorStoreIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE knowledge_chunk_embeddings
                    ADD CONSTRAINT "FK_knowledge_chunk_embeddings_workspaces_WorkspaceId"
                    FOREIGN KEY ("WorkspaceId")
                    REFERENCES workspaces("Id")
                    ON DELETE CASCADE;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE knowledge_chunk_embeddings
                    DROP CONSTRAINT IF EXISTS "FK_knowledge_chunk_embeddings_workspaces_WorkspaceId";
                """);
        }
    }
}
