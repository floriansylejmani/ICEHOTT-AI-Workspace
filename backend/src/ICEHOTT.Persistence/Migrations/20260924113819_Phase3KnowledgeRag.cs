using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICEHOTT.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase3KnowledgeRag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "conversation_message_citations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChunkId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SourceName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: true),
                    Score = table.Column<double>(type: "double precision", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversation_message_citations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_conversation_message_citations_conversation_messages_Messag~",
                        column: x => x.MessageId,
                        principalTable: "conversation_messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "knowledge_documents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SourceName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: true),
                    Content = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ChunkCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IndexedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_knowledge_documents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_knowledge_documents_users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_knowledge_documents_workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "knowledge_chunks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_knowledge_chunks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_knowledge_chunks_knowledge_documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "knowledge_documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_conversation_message_citations_MessageId",
                table: "conversation_message_citations",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_conversation_message_citations_WorkspaceId_MessageId",
                table: "conversation_message_citations",
                columns: new[] { "WorkspaceId", "MessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_chunks_DocumentId",
                table: "knowledge_chunks",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_chunks_WorkspaceId_DocumentId_Ordinal",
                table: "knowledge_chunks",
                columns: new[] { "WorkspaceId", "DocumentId", "Ordinal" });

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_documents_CreatedByUserId",
                table: "knowledge_documents",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_documents_WorkspaceId_CreatedAtUtc",
                table: "knowledge_documents",
                columns: new[] { "WorkspaceId", "CreatedAtUtc" });

            migrationBuilder.Sql("""
                CREATE EXTENSION IF NOT EXISTS vector;

                CREATE TABLE knowledge_chunk_embeddings (
                    "ChunkId" uuid PRIMARY KEY REFERENCES knowledge_chunks("Id") ON DELETE CASCADE,
                    "WorkspaceId" uuid NOT NULL,
                    "Embedding" vector(64) NOT NULL
                );

                CREATE INDEX "IX_knowledge_chunk_embeddings_WorkspaceId"
                    ON knowledge_chunk_embeddings ("WorkspaceId");

                CREATE INDEX "IX_knowledge_chunk_embeddings_Embedding_Hnsw"
                    ON knowledge_chunk_embeddings
                    USING hnsw ("Embedding" vector_cosine_ops);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS knowledge_chunk_embeddings;");

            migrationBuilder.DropTable(
                name: "conversation_message_citations");

            migrationBuilder.DropTable(
                name: "knowledge_chunks");

            migrationBuilder.DropTable(
                name: "knowledge_documents");
        }
    }
}
