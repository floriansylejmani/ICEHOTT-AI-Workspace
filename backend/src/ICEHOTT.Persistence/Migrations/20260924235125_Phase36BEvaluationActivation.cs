using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICEHOTT.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase36BEvaluationActivation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "rag_evaluation_evidence",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DatasetVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    EmbeddingProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Dimensions = table.Column<int>(type: "integer", nullable: false),
                    IndexVersion = table.Column<int>(type: "integer", nullable: false),
                    HitRateAtK = table.Column<double>(type: "double precision", nullable: false),
                    MeanRecallAtK = table.Column<double>(type: "double precision", nullable: false),
                    MeanPrecisionAtK = table.Column<double>(type: "double precision", nullable: false),
                    CitationCorrectness = table.Column<double>(type: "double precision", nullable: false),
                    TenantLeakageCount = table.Column<int>(type: "integer", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    RunnerVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rag_evaluation_evidence", x => x.Id);
                    table.ForeignKey(
                        name: "FK_rag_evaluation_evidence_embedding_profiles_EmbeddingProfile~",
                        column: x => x.EmbeddingProfileId,
                        principalTable: "embedding_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_embedding_profiles_Building",
                table: "embedding_profiles",
                column: "Status",
                unique: true,
                filter: "\"Status\" = 'Building'");

            migrationBuilder.CreateIndex(
                name: "IX_rag_evaluation_evidence_DatasetVersion_EmbeddingProfileId",
                table: "rag_evaluation_evidence",
                columns: new[] { "DatasetVersion", "EmbeddingProfileId" });

            migrationBuilder.CreateIndex(
                name: "IX_rag_evaluation_evidence_EmbeddingProfileId_Kind_CompletedAt~",
                table: "rag_evaluation_evidence",
                columns: new[] { "EmbeddingProfileId", "Kind", "CompletedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rag_evaluation_evidence");

            migrationBuilder.DropIndex(
                name: "IX_embedding_profiles_Building",
                table: "embedding_profiles");
        }
    }
}
