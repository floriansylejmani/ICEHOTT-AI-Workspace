using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using ICEHOTT.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace ICEHOTT.Persistence.Repositories;

public sealed class PostgresVectorStore(ICEHOTTDbContext db) : IVectorStore
{
    public async Task<bool> IsProfileReadyAsync(
        EmbeddingProfileDescriptor profile,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateProfile(profile);

            var connection = db.Database.GetDbConnection();
            var closeWhenDone = connection.State != ConnectionState.Open;

            try
            {
                if (closeWhenDone) await connection.OpenAsync(cancellationToken);

                await EnsureProfileCompatibleAsync(
                    connection,
                    transaction: null,
                    profile,
                    requireActive: true,
                    cancellationToken);

                return await HasCompatibleHnswIndexAsync(
                    connection,
                    profile,
                    cancellationToken);
            }
            finally
            {
                if (closeWhenDone && connection.State == ConnectionState.Open)
                    await connection.CloseAsync();
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    public async Task StoreManyAsync(
        Guid workspaceId,
        EmbeddingProfileDescriptor profile,
        IReadOnlyList<VectorEmbedding> embeddings,
        CancellationToken cancellationToken = default)
    {
        ValidateProfile(profile);

        if (embeddings.Any(x => x.Values.Count != profile.Dimensions))
            throw new VectorStoreUnavailableException(
                $"Embedding vectors must match profile {profile.Key} dimensions ({profile.Dimensions}).",
                retryable: false);

        if (embeddings.Count == 0) return;

        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;

        try
        {
            if (closeWhenDone) await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            await EnsureProfileCompatibleAsync(
                connection,
                transaction,
                profile,
                requireActive: false,
                cancellationToken);

            foreach (var embedding in embeddings)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO knowledge_chunk_embeddings
                        ("ChunkId", "WorkspaceId", "EmbeddingProfileId", "Embedding")
                    SELECT c."Id", c."WorkspaceId", @profileId, CAST(@embedding AS vector)
                    FROM knowledge_chunks c
                    WHERE c."Id" = @chunkId
                      AND c."WorkspaceId" = @workspaceId
                    ON CONFLICT ("ChunkId", "EmbeddingProfileId")
                    DO UPDATE SET
                        "WorkspaceId" = EXCLUDED."WorkspaceId",
                        "Embedding" = EXCLUDED."Embedding";
                    """;

                AddParameter(command, "@chunkId", embedding.ChunkId);
                AddParameter(command, "@workspaceId", workspaceId);
                AddParameter(command, "@profileId", profile.Id);
                AddParameter(command, "@embedding", FormatVector(embedding.Values));

                var affected = await command.ExecuteNonQueryAsync(cancellationToken);
                if (affected != 1)
                    throw new VectorStoreUnavailableException(
                        "Knowledge chunk does not belong to the requested workspace.",
                        retryable: false);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch (VectorStoreUnavailableException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new VectorStoreUnavailableException(
                "Could not persist pgvector embeddings.",
                retryable: true,
                exception);
        }
        finally
        {
            if (closeWhenDone && connection.State == ConnectionState.Open)
                await connection.CloseAsync();
        }
    }

    public async Task<IReadOnlyList<KnowledgeMatch>> SearchAsync(
        Guid workspaceId,
        EmbeddingProfileDescriptor profile,
        string queryText,
        IReadOnlyList<float> queryEmbedding,
        int limit,
        bool allowBuildingProfile = false,
        CancellationToken cancellationToken = default)
    {
        ValidateProfile(profile);

        if (queryEmbedding.Count != profile.Dimensions)
            throw new VectorStoreUnavailableException(
                $"Query embedding must match profile {profile.Key} dimensions ({profile.Dimensions}).",
                retryable: false);

        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;

        try
        {
            if (closeWhenDone) await connection.OpenAsync(cancellationToken);

            await EnsureProfileCompatibleAsync(
                connection,
                transaction: null,
                profile,
                requireActive: !allowBuildingProfile,
                cancellationToken);

            await using var command = connection.CreateCommand();
            var dimensions = profile.Dimensions;
            var allowedProfileStatus = allowBuildingProfile
                ? "IN ('Active', 'Building')"
                : "= 'Active'";
            command.CommandText = $"""
                WITH ranked AS (
                    SELECT
                        c."Id",
                        c."DocumentId",
                        d."Title",
                        d."SourceName",
                        c."Content",
                        GREATEST(
                            0::double precision,
                            1 - (
                                (e."Embedding"::vector({dimensions}))
                                <=>
                                CAST(@embedding AS vector({dimensions}))
                            )
                        ) AS vector_score,
                        LEAST(
                            1::real,
                            ts_rank_cd(
                                to_tsvector('simple', c."Content"),
                                plainto_tsquery('simple', @query)
                            )
                        )::double precision AS lexical_score
                    FROM knowledge_chunk_embeddings e
                    INNER JOIN embedding_profiles p
                        ON p."Id" = e."EmbeddingProfileId"
                    INNER JOIN knowledge_chunks c
                        ON c."Id" = e."ChunkId"
                       AND c."WorkspaceId" = e."WorkspaceId"
                    INNER JOIN knowledge_documents d
                        ON d."Id" = c."DocumentId"
                       AND d."WorkspaceId" = c."WorkspaceId"
                    WHERE e."WorkspaceId" = @workspaceId
                      AND e."EmbeddingProfileId" = @profileId
                      AND p."Status" {allowedProfileStatus}
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
            AddParameter(command, "@profileId", profile.Id);
            AddParameter(command, "@query", queryText);
            AddParameter(command, "@embedding", FormatVector(queryEmbedding));
            AddParameter(command, "@limit", Math.Clamp(limit, 1, 50));

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
                    reader.IsDBNull(5)
                        ? 0
                        : Convert.ToDouble(reader.GetValue(5), CultureInfo.InvariantCulture)));
            }

            return matches;
        }
        catch (VectorStoreUnavailableException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new VectorStoreUnavailableException(
                "Could not query pgvector knowledge.",
                retryable: true,
                exception);
        }
        finally
        {
            if (closeWhenDone && connection.State == ConnectionState.Open)
                await connection.CloseAsync();
        }
    }

    private static void ValidateProfile(EmbeddingProfileDescriptor profile)
    {
        try
        {
            profile.Validate();
        }
        catch (Exception exception)
        {
            throw new VectorStoreUnavailableException(
                "Embedding profile configuration is invalid.",
                retryable: false,
                exception);
        }

        if (profile.Dimensions > 16000)
            throw new VectorStoreUnavailableException(
                "Embedding dimensions exceed pgvector vector type limits.",
                retryable: false);
    }

    private static async Task EnsureProfileCompatibleAsync(
        DbConnection connection,
        DbTransaction? transaction,
        EmbeddingProfileDescriptor profile,
        bool requireActive,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                "Key",
                "Provider",
                "Model",
                "Dimensions",
                "Version",
                "IndexVersion",
                "DistanceMetric",
                "Normalization",
                "Status"
            FROM embedding_profiles
            WHERE "Id" = @profileId;
            """;

        AddParameter(command, "@profileId", profile.Id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new VectorStoreUnavailableException(
                $"Embedding profile {profile.Id} is not registered.",
                retryable: false);

        var key = reader.GetString(0);
        var provider = reader.GetString(1);
        var model = reader.GetString(2);
        var dimensions = reader.GetInt32(3);
        var version = reader.GetString(4);
        var indexVersion = reader.GetInt32(5);
        var distanceMetric = reader.GetString(6);
        var normalization = reader.GetString(7);
        var status = reader.GetString(8);

        if (!string.Equals(key, profile.Key, StringComparison.Ordinal) ||
            !string.Equals(provider, profile.Provider, StringComparison.Ordinal) ||
            !string.Equals(model, profile.Model, StringComparison.Ordinal) ||
            dimensions != profile.Dimensions ||
            !string.Equals(version, profile.Version, StringComparison.Ordinal) ||
            indexVersion != profile.IndexVersion ||
            !string.Equals(distanceMetric, profile.DistanceMetric, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(normalization, profile.Normalization, StringComparison.OrdinalIgnoreCase))
        {
            throw new VectorStoreUnavailableException(
                $"Configured embedding profile {profile.Key} does not match persisted metadata.",
                retryable: false);
        }

        var allowed = requireActive
            ? string.Equals(status, "Active", StringComparison.Ordinal)
            : status is "Active" or "Building";

        if (!allowed)
            throw new VectorStoreUnavailableException(
                $"Embedding profile {profile.Key} is not available for {(requireActive ? "retrieval" : "indexing")}.",
                retryable: false);
    }

    private static async Task<bool> HasCompatibleHnswIndexAsync(
        DbConnection connection,
        EmbeddingProfileDescriptor profile,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM pg_indexes
                WHERE schemaname = ANY (current_schemas(false))
                  AND tablename = 'knowledge_chunk_embeddings'
                  AND indexdef ILIKE '%USING hnsw%'
                  AND indexdef LIKE @profilePattern
                  AND indexdef LIKE @dimensionPattern
            );
            """;

        AddParameter(
            command,
            "@profilePattern",
            $"%{profile.Id:D}%");
        AddParameter(
            command,
            "@dimensionPattern",
            $"%vector({profile.Dimensions})%");

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is true;
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

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
