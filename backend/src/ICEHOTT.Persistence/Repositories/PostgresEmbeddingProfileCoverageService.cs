using System.Data;
using ICEHOTT.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace ICEHOTT.Persistence.Repositories;

public sealed class PostgresEmbeddingProfileCoverageService(ICEHOTTDbContext db)
    : IEmbeddingProfileCoverageService
{
    public async Task<EmbeddingProfileCoverage> GetCoverageAsync(
        EmbeddingProfileDescriptor profile,
        CancellationToken cancellationToken = default)
    {
        profile.Validate();
        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;

        try
        {
            if (closeWhenDone) await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    (
                        SELECT COUNT(*)
                        FROM knowledge_chunks c
                        INNER JOIN knowledge_documents d
                            ON d."Id" = c."DocumentId"
                           AND d."WorkspaceId" = c."WorkspaceId"
                        WHERE d."Status" = 'Ready'
                    ) AS expected_ready_chunks,
                    (
                        SELECT COUNT(*)
                        FROM knowledge_chunk_embeddings e
                        INNER JOIN knowledge_chunks c
                            ON c."Id" = e."ChunkId"
                           AND c."WorkspaceId" = e."WorkspaceId"
                        INNER JOIN knowledge_documents d
                            ON d."Id" = c."DocumentId"
                           AND d."WorkspaceId" = c."WorkspaceId"
                        WHERE e."EmbeddingProfileId" = @profileId
                          AND d."Status" = 'Ready'
                    ) AS embedded_ready_chunks;
                """;
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@profileId";
            parameter.Value = profile.Id;
            command.Parameters.Add(parameter);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new VectorStoreUnavailableException(
                    "Could not calculate embedding profile coverage.");

            return new EmbeddingProfileCoverage(
                reader.GetInt64(0),
                reader.GetInt64(1));
        }
        finally
        {
            if (closeWhenDone && connection.State == ConnectionState.Open)
                await connection.CloseAsync();
        }
    }
}
