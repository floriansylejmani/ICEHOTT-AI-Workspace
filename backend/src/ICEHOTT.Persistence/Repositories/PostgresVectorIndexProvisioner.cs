using System.Data;
using System.Data.Common;
using ICEHOTT.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace ICEHOTT.Persistence.Repositories;

public sealed class PostgresVectorIndexProvisioner(ICEHOTTDbContext db)
    : IVectorIndexProvisioner
{
    public async Task EnsureBuildIndexAsync(
        EmbeddingProfileDescriptor profile,
        CancellationToken cancellationToken = default)
    {
        ValidateHnswProfile(profile);

        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;

        try
        {
            if (closeWhenDone)
                await connection.OpenAsync(cancellationToken);

            await EnsureStatusAsync(
                connection,
                profile,
                requiredStatus: "Building",
                cancellationToken);

            var indexName = GetIndexName(profile);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                CREATE INDEX IF NOT EXISTS "{indexName}"
                ON knowledge_chunk_embeddings
                USING hnsw (("Embedding"::vector({profile.Dimensions})) vector_cosine_ops)
                WHERE "EmbeddingProfileId" = '{profile.Id:D}'::uuid;
                """;

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            if (closeWhenDone && connection.State == ConnectionState.Open)
                await connection.CloseAsync();
        }
    }

    public async Task<bool> IsIndexReadyAsync(
        EmbeddingProfileDescriptor profile,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateHnswProfile(profile);

            var connection = db.Database.GetDbConnection();
            var closeWhenDone = connection.State != ConnectionState.Open;

            try
            {
                if (closeWhenDone)
                    await connection.OpenAsync(cancellationToken);

                var indexName = GetIndexName(profile);
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT EXISTS (
                        SELECT 1
                        FROM pg_indexes
                        WHERE schemaname = ANY (current_schemas(false))
                          AND tablename = 'knowledge_chunk_embeddings'
                          AND indexname = @indexName
                          AND indexdef ILIKE '%USING hnsw%'
                          AND indexdef LIKE @profilePattern
                          AND indexdef LIKE @dimensionPattern
                    );
                    """;

                AddParameter(command, "@indexName", indexName);
                AddParameter(command, "@profilePattern", $"%{profile.Id:D}%");
                AddParameter(command, "@dimensionPattern", $"%vector({profile.Dimensions})%");

                return await command.ExecuteScalarAsync(cancellationToken) is true;
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

    public async Task DropRetiredIndexAsync(
        EmbeddingProfileDescriptor profile,
        CancellationToken cancellationToken = default)
    {
        ValidateHnswProfile(profile);

        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;

        try
        {
            if (closeWhenDone)
                await connection.OpenAsync(cancellationToken);

            await EnsureStatusAsync(
                connection,
                profile,
                requiredStatus: "Retired",
                cancellationToken);

            var indexName = GetIndexName(profile);
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP INDEX IF EXISTS \"{indexName}\";";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            if (closeWhenDone && connection.State == ConnectionState.Open)
                await connection.CloseAsync();
        }
    }

    public static string GetIndexName(EmbeddingProfileDescriptor profile) =>
        $"IX_kce_hnsw_{profile.Id:N}_v{profile.IndexVersion}";

    private static void ValidateHnswProfile(EmbeddingProfileDescriptor profile)
    {
        profile.Validate();

        if (profile.Dimensions > 2000)
            throw new VectorStoreUnavailableException(
                "The current float32 pgvector HNSW path supports at most 2000 dimensions.",
                retryable: false);

        if (!string.Equals(
                profile.DistanceMetric,
                "cosine",
                StringComparison.OrdinalIgnoreCase))
            throw new VectorStoreUnavailableException(
                "Only cosine HNSW indexes are supported.",
                retryable: false);
    }

    private static async Task EnsureStatusAsync(
        DbConnection connection,
        EmbeddingProfileDescriptor profile,
        string requiredStatus,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
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

        var compatible =
            string.Equals(reader.GetString(0), profile.Key, StringComparison.Ordinal) &&
            string.Equals(reader.GetString(1), profile.Provider, StringComparison.Ordinal) &&
            string.Equals(reader.GetString(2), profile.Model, StringComparison.Ordinal) &&
            reader.GetInt32(3) == profile.Dimensions &&
            string.Equals(reader.GetString(4), profile.Version, StringComparison.Ordinal) &&
            reader.GetInt32(5) == profile.IndexVersion &&
            string.Equals(reader.GetString(6), profile.DistanceMetric, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(reader.GetString(7), profile.Normalization, StringComparison.OrdinalIgnoreCase);

        var status = reader.GetString(8);
        await reader.DisposeAsync();

        if (!compatible)
            throw new VectorStoreUnavailableException(
                $"Persisted embedding profile {profile.Key} does not match runtime metadata.",
                retryable: false);

        if (!string.Equals(status, requiredStatus, StringComparison.Ordinal))
            throw new VectorStoreUnavailableException(
                $"Embedding profile {profile.Key} must be {requiredStatus}, but is {status}.",
                retryable: false);
    }

    private static void AddParameter(
        DbCommand command,
        string name,
        object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
