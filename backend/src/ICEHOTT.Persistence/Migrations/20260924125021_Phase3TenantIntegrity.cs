using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICEHOTT.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase3TenantIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE knowledge_chunk_embeddings
                    DROP CONSTRAINT IF EXISTS "knowledge_chunk_embeddings_ChunkId_fkey";
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_conversation_message_citations_conversation_messages_Messag~",
                table: "conversation_message_citations");

            migrationBuilder.DropForeignKey(
                name: "FK_conversation_messages_conversations_ConversationId",
                table: "conversation_messages");

            migrationBuilder.DropForeignKey(
                name: "FK_knowledge_chunks_knowledge_documents_DocumentId",
                table: "knowledge_chunks");

            migrationBuilder.DropIndex(
                name: "IX_knowledge_chunks_DocumentId",
                table: "knowledge_chunks");

            migrationBuilder.DropIndex(
                name: "IX_conversation_messages_ConversationId",
                table: "conversation_messages");

            migrationBuilder.DropIndex(
                name: "IX_conversation_message_citations_MessageId",
                table: "conversation_message_citations");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_knowledge_documents_Id_WorkspaceId",
                table: "knowledge_documents",
                columns: new[] { "Id", "WorkspaceId" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_knowledge_chunks_Id_WorkspaceId",
                table: "knowledge_chunks",
                columns: new[] { "Id", "WorkspaceId" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_conversations_Id_WorkspaceId",
                table: "conversations",
                columns: new[] { "Id", "WorkspaceId" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_conversation_messages_Id_WorkspaceId",
                table: "conversation_messages",
                columns: new[] { "Id", "WorkspaceId" });

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_chunks_DocumentId_WorkspaceId",
                table: "knowledge_chunks",
                columns: new[] { "DocumentId", "WorkspaceId" });

            migrationBuilder.CreateIndex(
                name: "IX_conversation_messages_ConversationId_WorkspaceId",
                table: "conversation_messages",
                columns: new[] { "ConversationId", "WorkspaceId" });

            migrationBuilder.CreateIndex(
                name: "IX_conversation_message_citations_MessageId_WorkspaceId",
                table: "conversation_message_citations",
                columns: new[] { "MessageId", "WorkspaceId" });

            migrationBuilder.Sql("""
                ALTER TABLE knowledge_chunk_embeddings
                    ADD CONSTRAINT "FK_knowledge_chunk_embeddings_knowledge_chunks_ChunkId_WorkspaceId"
                    FOREIGN KEY ("ChunkId", "WorkspaceId")
                    REFERENCES knowledge_chunks("Id", "WorkspaceId")
                    ON DELETE CASCADE;
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_conversation_message_citations_conversation_messages_Messag~",
                table: "conversation_message_citations",
                columns: new[] { "MessageId", "WorkspaceId" },
                principalTable: "conversation_messages",
                principalColumns: new[] { "Id", "WorkspaceId" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_conversation_messages_conversations_ConversationId_Workspac~",
                table: "conversation_messages",
                columns: new[] { "ConversationId", "WorkspaceId" },
                principalTable: "conversations",
                principalColumns: new[] { "Id", "WorkspaceId" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_knowledge_chunks_knowledge_documents_DocumentId_WorkspaceId",
                table: "knowledge_chunks",
                columns: new[] { "DocumentId", "WorkspaceId" },
                principalTable: "knowledge_documents",
                principalColumns: new[] { "Id", "WorkspaceId" },
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE knowledge_chunk_embeddings
                    DROP CONSTRAINT IF EXISTS "FK_knowledge_chunk_embeddings_knowledge_chunks_ChunkId_WorkspaceId";
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_conversation_message_citations_conversation_messages_Messag~",
                table: "conversation_message_citations");

            migrationBuilder.DropForeignKey(
                name: "FK_conversation_messages_conversations_ConversationId_Workspac~",
                table: "conversation_messages");

            migrationBuilder.DropForeignKey(
                name: "FK_knowledge_chunks_knowledge_documents_DocumentId_WorkspaceId",
                table: "knowledge_chunks");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_knowledge_documents_Id_WorkspaceId",
                table: "knowledge_documents");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_knowledge_chunks_Id_WorkspaceId",
                table: "knowledge_chunks");

            migrationBuilder.DropIndex(
                name: "IX_knowledge_chunks_DocumentId_WorkspaceId",
                table: "knowledge_chunks");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_conversations_Id_WorkspaceId",
                table: "conversations");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_conversation_messages_Id_WorkspaceId",
                table: "conversation_messages");

            migrationBuilder.DropIndex(
                name: "IX_conversation_messages_ConversationId_WorkspaceId",
                table: "conversation_messages");

            migrationBuilder.DropIndex(
                name: "IX_conversation_message_citations_MessageId_WorkspaceId",
                table: "conversation_message_citations");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_chunks_DocumentId",
                table: "knowledge_chunks",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_conversation_messages_ConversationId",
                table: "conversation_messages",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_conversation_message_citations_MessageId",
                table: "conversation_message_citations",
                column: "MessageId");

            migrationBuilder.Sql("""
                ALTER TABLE knowledge_chunk_embeddings
                    ADD CONSTRAINT "knowledge_chunk_embeddings_ChunkId_fkey"
                    FOREIGN KEY ("ChunkId")
                    REFERENCES knowledge_chunks("Id")
                    ON DELETE CASCADE;
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_conversation_message_citations_conversation_messages_Messag~",
                table: "conversation_message_citations",
                column: "MessageId",
                principalTable: "conversation_messages",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_conversation_messages_conversations_ConversationId",
                table: "conversation_messages",
                column: "ConversationId",
                principalTable: "conversations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_knowledge_chunks_knowledge_documents_DocumentId",
                table: "knowledge_chunks",
                column: "DocumentId",
                principalTable: "knowledge_documents",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
