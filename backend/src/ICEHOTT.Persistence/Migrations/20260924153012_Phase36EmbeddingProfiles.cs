using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICEHOTT.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase36EmbeddingProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "embedding_profiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Provider = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Dimensions = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    IndexVersion = table.Column<int>(type: "integer", nullable: false),
                    DistanceMetric = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Normalization = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActivatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_embedding_profiles", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "embedding_profiles",
                columns: new[] { "Id", "ActivatedAtUtc", "CreatedAtUtc", "Dimensions", "DistanceMetric", "IndexVersion", "Key", "Model", "Normalization", "Provider", "Status", "Version" },
                values: new object[] { new Guid("3f36a640-0d25-4a76-9d0d-640000000001"), new DateTimeOffset(new DateTime(2026, 9, 24, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), new DateTimeOffset(new DateTime(2026, 9, 24, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), 64, "cosine", 1, "local-deterministic-64-v1", "deterministic-64d", "unit", "icehott-ai-runtime", "Active", "1" });

            migrationBuilder.CreateIndex(
                name: "IX_embedding_profiles_Key",
                table: "embedding_profiles",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_embedding_profiles_Provider_Model_Version_IndexVersion",
                table: "embedding_profiles",
                columns: new[] { "Provider", "Model", "Version", "IndexVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_embedding_profiles_Status",
                table: "embedding_profiles",
                column: "Status");

            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS "IX_knowledge_chunk_embeddings_Embedding_Hnsw";

                ALTER TABLE knowledge_chunk_embeddings
                    ADD COLUMN "EmbeddingProfileId" uuid;

                UPDATE knowledge_chunk_embeddings
                SET "EmbeddingProfileId" = '3f36a640-0d25-4a76-9d0d-640000000001'
                WHERE "EmbeddingProfileId" IS NULL;

                ALTER TABLE knowledge_chunk_embeddings
                    ALTER COLUMN "EmbeddingProfileId" SET NOT NULL;

                ALTER TABLE knowledge_chunk_embeddings
                    DROP CONSTRAINT knowledge_chunk_embeddings_pkey;

                ALTER TABLE knowledge_chunk_embeddings
                    ADD CONSTRAINT "PK_knowledge_chunk_embeddings"
                    PRIMARY KEY ("ChunkId", "EmbeddingProfileId");

                ALTER TABLE knowledge_chunk_embeddings
                    ADD CONSTRAINT "FK_knowledge_chunk_embeddings_embedding_profiles_EmbeddingProfileId"
                    FOREIGN KEY ("EmbeddingProfileId")
                    REFERENCES embedding_profiles("Id")
                    ON DELETE RESTRICT;

                ALTER TABLE knowledge_chunk_embeddings
                    ALTER COLUMN "Embedding" TYPE vector
                    USING "Embedding"::vector;

                CREATE INDEX "IX_knowledge_chunk_embeddings_WorkspaceId_EmbeddingProfileId"
                    ON knowledge_chunk_embeddings ("WorkspaceId", "EmbeddingProfileId");

                CREATE INDEX "IX_knowledge_chunk_embeddings_Embedding_Hnsw"
                    ON knowledge_chunk_embeddings
                    USING hnsw (("Embedding"::vector(64)) vector_cosine_ops)
                    WHERE "EmbeddingProfileId" = '3f36a640-0d25-4a76-9d0d-640000000001';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS "IX_knowledge_chunk_embeddings_Embedding_Hnsw";
                DROP INDEX IF EXISTS "IX_knowledge_chunk_embeddings_WorkspaceId_EmbeddingProfileId";

                DELETE FROM knowledge_chunk_embeddings
                WHERE "EmbeddingProfileId" <> '3f36a640-0d25-4a76-9d0d-640000000001';

                ALTER TABLE knowledge_chunk_embeddings
                    DROP CONSTRAINT IF EXISTS "FK_knowledge_chunk_embeddings_embedding_profiles_EmbeddingProfileId";

                ALTER TABLE knowledge_chunk_embeddings
                    DROP CONSTRAINT "PK_knowledge_chunk_embeddings";

                ALTER TABLE knowledge_chunk_embeddings
                    ALTER COLUMN "Embedding" TYPE vector(64)
                    USING "Embedding"::vector(64);

                ALTER TABLE knowledge_chunk_embeddings
                    DROP COLUMN "EmbeddingProfileId";

                ALTER TABLE knowledge_chunk_embeddings
                    ADD CONSTRAINT knowledge_chunk_embeddings_pkey
                    PRIMARY KEY ("ChunkId");

                CREATE INDEX "IX_knowledge_chunk_embeddings_Embedding_Hnsw"
                    ON knowledge_chunk_embeddings
                    USING hnsw ("Embedding" vector_cosine_ops);
                """);

            migrationBuilder.DropTable(
                name: "embedding_profiles");
        }
    }
}
