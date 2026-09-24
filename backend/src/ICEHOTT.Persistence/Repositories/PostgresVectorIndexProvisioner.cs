using System.Data;
using System.Data.Common;
using ICEHOTT.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace ICEHOTT.Persistence.Repositories;

public sealed class PostgresVectorIndexProvisioner(ICEHOTTDbContext db)
    : IVectorIndexProvisioner
{
    private const int MaxHnswVectorDimensions = 2000;

    public async Task EnsureBuildIndexAsync(
        EmbeddingProfileDescriptor profile,
        CancellationToken cancellationToken = default)
    {
        ValidateForHnsw(profile);
        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;

        try
        {
            if (closeWhenDone) await connection.OpenAsync(cancellationToken);
            await EnsurePersistedProfileAsync(
                connection, profile, "Building", cancellationToken);
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
            ValidateForHnsw(profile);
            var connection = db.Database.GetDbConnection();
            var closeWhenDone = connection.State != ConnectionState.Open;
            try
            {
                if (closeWhenDone) await connection.OpenAsync(cancellationToken);

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
                AddParameter(command, "@indexName", GetIndexName(profile));
                AddParameter(command, "@profilePattern", $"%{profile.Id:D}%");
                AddParameter(command, "@dimensionPattern", $"%vector({profile.Dimensions})%");
                var result = await command.ExecuteScalarAsync(cancellationToken);
                return result is true;
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
        ValidateForHnsw(profile);
        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;

        try
        {
            if (closeWhenDone) await connection.OpenAsync(cancellationToken);
            await EnsurePersistedProfileAsync(
                connection, profile, "Retired", cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP INDEX IF EXISTS \"{GetIndexName(profile)}\";";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            if (closeWhenDone && connection.State == ConnectionState.Open)
                await connection.CloseAsync();
        }
    }

    public static string GetIndexName(EmbeddingProfileDescriptor profile)
    {
        profile.Validate();
        var prefix = profile.Id.ToString("N")[..12];
        return $"IX_kce_hnsw_{prefix}_v{profile.IndexVersion}";
    }

    private static void ValidateForHnsw(EmbeddingProfileDescriptor profile)
    {
        profile.Validate();
        if (profile.Dimensions > MaxHnswVectorDimensions)
            throw new VectorStoreUnavailableException(
                $"HNSW vector indexes support at most {MaxHnswVectorDimensions} dimensions.",
                retryable: false);
        if (!string.Equals(profile.DistanceMetric, "cosine", StringComparison.OrdinalIgnoreCase))
            throw new VectorStoreUnavailableException(
                "Phase 3.6B HNSW provisioning supports cosine distance only.",
                retryable: false);
    }

    private static async Task EnsurePersistedProfileAsync(
        DbConnection connection,
        EmbeddingProfileDescriptor profile,
        string requiredStatus,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "Key", "Provider", "Model", "Dimensions", "Version",
                   "IndexVersion", "DistanceMetric", "Normalization", "Status"
            FROM embedding_profiles
            WHERE "Id" = @profileId;
            """;
        AddParameter(command, "@profileId", profile.Id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new VectorStoreUnavailableException(
                $"Embedding profile {profile.Id} is not registered.",
                retryable: false);
        var matches =
            reader.GetString(0) == profile.Key &&
            reader.GetString(1) == profile.Provider &&
            reader.GetString(2) == profile.Model &&
            reader.GetInt32(3) == profile.Dimensions &&
            reader.GetString(4) == profile.Version &&
            reader.GetInt32(5) == profile.IndexVersion &&
            string.Equals(reader.GetString(6), profile.DistanceMetric, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(reader.GetString(7), profile.Normalization, StringComparison.OrdinalIgnoreCase);

        if (!matches)
            throw new VectorStoreUnavailableException(
                $"Embedding profile {profile.Key} metadata does not match persisted state.",
                retryable: false);

        var status = reader.GetString(8);
        if (!string.Equals(status, requiredStatus, StringComparison.Ordinal))
            throw new VectorStoreUnavailableException(
                $"Embedding profile {profile.Key} must be {requiredStatus}, not {status}.",
                retryable: false);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
