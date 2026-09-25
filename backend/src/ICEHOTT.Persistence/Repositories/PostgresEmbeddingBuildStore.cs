using System.Data;
using ICEHOTT.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace ICEHOTT.Persistence.Repositories;

public sealed class PostgresEmbeddingBuildStore(ICEHOTTDbContext db) : IEmbeddingBuildStore
{
    public async Task<IReadOnlyList<EmbeddingBuildChunk>> GetMissingReadyChunksAsync(
        Guid profileId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var safeLimit = Math.Clamp(limit, 1, 1000);
        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;

        try
        {
            if (closeWhenDone)
                await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT c."Id", c."WorkspaceId", c."Content"
                FROM knowledge_chunks c
                INNER JOIN knowledge_documents d
                    ON d."Id" = c."DocumentId"
                   AND d."WorkspaceId" = c."WorkspaceId"
                WHERE d."Status" = 'Ready'
                  AND NOT EXISTS (
                      SELECT 1
                      FROM knowledge_chunk_embeddings e
                      WHERE e."ChunkId" = c."Id"
                        AND e."WorkspaceId" = c."WorkspaceId"
                        AND e."EmbeddingProfileId" = @profileId
                  )
                ORDER BY c."WorkspaceId", c."Id"
                LIMIT @limit;
                """;

            AddParameter(command, "@profileId", profileId);
            AddParameter(command, "@limit", safeLimit);

            var result = new List<EmbeddingBuildChunk>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(new EmbeddingBuildChunk(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetString(2)));
            }

            return result;
        }
        finally
        {
            if (closeWhenDone && connection.State == ConnectionState.Open)
                await connection.CloseAsync();
        }
    }

    public async Task<EmbeddingProfileCoverage> GetCoverageAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;

        try
        {
            if (closeWhenDone)
                await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    COUNT(*)::bigint AS ready_chunks,
                    COUNT(*) FILTER (
                        WHERE EXISTS (
                            SELECT 1
                            FROM knowledge_chunk_embeddings e
                            WHERE e."ChunkId" = c."Id"
                              AND e."WorkspaceId" = c."WorkspaceId"
                              AND e."EmbeddingProfileId" = @profileId
                        )
                    )::bigint AS embedded_chunks
                FROM knowledge_chunks c
                INNER JOIN knowledge_documents d
                    ON d."Id" = c."DocumentId"
                   AND d."WorkspaceId" = c."WorkspaceId"
                WHERE d."Status" = 'Ready';
                """;

            AddParameter(command, "@profileId", profileId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return new EmbeddingProfileCoverage(0, 0);

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

    private static void AddParameter(
        System.Data.Common.DbCommand command,
        string name,
        object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
