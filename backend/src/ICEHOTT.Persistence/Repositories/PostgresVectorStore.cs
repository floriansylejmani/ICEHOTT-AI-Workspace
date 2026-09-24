using System.Data;
using System.Globalization;
using System.Text;
using ICEHOTT.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace ICEHOTT.Persistence.Repositories;

public sealed class PostgresVectorStore(ICEHOTTDbContext db) : IVectorStore
{
    private const int Dimensions = 64;

    public async Task StoreManyAsync(
        Guid workspaceId,
        IReadOnlyList<VectorEmbedding> embeddings,
        CancellationToken cancellationToken = default)
    {
        if (embeddings.Any(x => x.Values.Count != Dimensions))
            throw new VectorStoreUnavailableException($"Expected {Dimensions}-dimension embeddings.");

        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;

        try
        {
            if (closeWhenDone) await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            foreach (var embedding in embeddings)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO knowledge_chunk_embeddings ("ChunkId", "WorkspaceId", "Embedding")
                    SELECT c."Id", c."WorkspaceId", CAST(@embedding AS vector)
                    FROM knowledge_chunks c
                    WHERE c."Id" = @chunkId
                      AND c."WorkspaceId" = @workspaceId
                    ON CONFLICT ("ChunkId")
                    DO UPDATE SET
                        "WorkspaceId" = EXCLUDED."WorkspaceId",
                        "Embedding" = EXCLUDED."Embedding";
                    """;

                AddParameter(command, "@chunkId", embedding.ChunkId);
                AddParameter(command, "@workspaceId", workspaceId);
                AddParameter(command, "@embedding", FormatVector(embedding.Values));

                var affected = await command.ExecuteNonQueryAsync(cancellationToken);
                if (affected != 1)
                    throw new VectorStoreUnavailableException(
                        "Knowledge chunk does not belong to the requested workspace.");
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not VectorStoreUnavailableException)
        {
            throw new VectorStoreUnavailableException("Could not persist pgvector embeddings.", exception);
        }
        finally
        {
            if (closeWhenDone && connection.State == ConnectionState.Open)
                await connection.CloseAsync();
        }
    }

    public async Task<IReadOnlyList<KnowledgeMatch>> SearchAsync(
        Guid workspaceId,
        string queryText,
        IReadOnlyList<float> queryEmbedding,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (queryEmbedding.Count != Dimensions)
            throw new VectorStoreUnavailableException($"Expected a {Dimensions}-dimension query embedding.");

        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;

        try
        {
            if (closeWhenDone) await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                WITH ranked AS (
                    SELECT
                        c."Id",
                        c."DocumentId",
                        d."Title",
                        d."SourceName",
                        c."Content",
                        GREATEST(0::double precision, 1 - (e."Embedding" <=> CAST(@embedding AS vector))) AS vector_score,
                        LEAST(
                            1::real,
                            ts_rank_cd(
                                to_tsvector('simple', c."Content"),
                                plainto_tsquery('simple', @query)
                            )
                        )::double precision AS lexical_score
                    FROM knowledge_chunk_embeddings e
                    INNER JOIN knowledge_chunks c
                        ON c."Id" = e."ChunkId"
                       AND c."WorkspaceId" = e."WorkspaceId"
                    INNER JOIN knowledge_documents d
                        ON d."Id" = c."DocumentId"
                       AND d."WorkspaceId" = c."WorkspaceId"
                    WHERE e."WorkspaceId" = @workspaceId
                      AND c."WorkspaceId" = @workspaceId
                      AND d."WorkspaceId" = @workspaceId
                      AND d."Status" = 'Ready'
                )
                SELECT
                    "Id",
                    "DocumentId",
                    "Title",
                    "SourceName",
                    "Content",
                    (vector_score * 0.80 + lexical_score * 0.20) AS "Score"
                FROM ranked
                ORDER BY "Score" DESC
                LIMIT @limit;
                """;

            AddParameter(command, "@workspaceId", workspaceId);
            AddParameter(command, "@query", queryText);
            AddParameter(command, "@embedding", FormatVector(queryEmbedding));
            AddParameter(command, "@limit", Math.Clamp(limit, 1, 10));

            var matches = new List<KnowledgeMatch>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                matches.Add(new KnowledgeMatch(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? 0 : Convert.ToDouble(reader.GetValue(5), CultureInfo.InvariantCulture)));
            }

            return matches;
        }
        catch (Exception exception) when (exception is not VectorStoreUnavailableException)
        {
            throw new VectorStoreUnavailableException("Could not query pgvector knowledge.", exception);
        }
        finally
        {
            if (closeWhenDone && connection.State == ConnectionState.Open)
                await connection.CloseAsync();
        }
    }

    private static string FormatVector(IReadOnlyList<float> values)
    {
        var builder = new StringBuilder("[");
        for (var index = 0; index < values.Count; index++)
        {
            if (index > 0) builder.Append(',');
            builder.Append(values[index].ToString("R", CultureInfo.InvariantCulture));
        }

        return builder.Append(']').ToString();
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
