using System.Data;
using System.Data.Common;
using ICEHOTT.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace ICEHOTT.Persistence.Repositories;

public sealed class PostgresEmbeddingProfileActivationStore(
    ICEHOTTDbContext db) : IEmbeddingProfileActivationStore
{
    public async Task<EmbeddingProfileActivationResult> ActivateAsync(
        Guid candidateProfileId,
        Guid deterministicEvidenceId,
        Guid? offlineEvidenceId,
        DateTimeOffset activatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;

        try
        {
            if (closeWhenDone)
                await connection.OpenAsync(cancellationToken);

            await using var transaction =
                await connection.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    cancellationToken);

            try
            {
                await ExecuteNonQueryAsync(
                    connection,
                    transaction,
                    """
                    LOCK TABLE knowledge_documents IN SHARE ROW EXCLUSIVE MODE;
                    LOCK TABLE knowledge_chunks IN SHARE ROW EXCLUSIVE MODE;
                    LOCK TABLE knowledge_chunk_embeddings IN SHARE ROW EXCLUSIVE MODE;
                    LOCK TABLE embedding_profiles IN SHARE ROW EXCLUSIVE MODE;
                    """,
                    cancellationToken);

                var candidate = await ReadCandidateAsync(
                    connection,
                    transaction,
                    candidateProfileId,
                    cancellationToken);

                if (!string.Equals(
                        candidate.Status,
                        "Building",
                        StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"Candidate profile must still be Building, but is {candidate.Status}.");

                var previousActiveId = await ReadActiveProfileIdAsync(
                    connection,
                    transaction,
                    cancellationToken);

                if (previousActiveId == candidateProfileId)
                    throw new InvalidOperationException(
                        "Candidate profile is already Active.");

                await EnsureEvidenceAsync(
                    connection,
                    transaction,
                    deterministicEvidenceId,
                    candidateProfileId,
                    "Deterministic",
                    cancellationToken);

                if (offlineEvidenceId is not null)
                {
                    await EnsureEvidenceAsync(
                        connection,
                        transaction,
                        offlineEvidenceId.Value,
                        candidateProfileId,
                        "OfflineSemantic",
                        cancellationToken);
                }

                var pendingDocuments = await ExecuteScalarInt64Async(
                    connection,
                    transaction,
                    """
                    SELECT COUNT(*)::bigint
                    FROM knowledge_documents
                    WHERE "Status" IN ('Queued', 'Processing');
                    """,
                    cancellationToken);

                if (pendingDocuments != 0)
                    throw new InvalidOperationException(
                        $"Activation requires a drained ingestion queue; {pendingDocuments} documents are Queued/Processing.");

                var coverage = await ReadCoverageAsync(
                    connection,
                    transaction,
                    candidateProfileId,
                    cancellationToken);

                if (!coverage.IsComplete)
                    throw new InvalidOperationException(
                        $"Candidate coverage changed during activation: {coverage.EmbeddedChunkCount}/{coverage.ReadyChunkCount}.");

                var indexReady = await IsIndexReadyAsync(
                    connection,
                    transaction,
                    candidateProfileId,
                    candidate.Dimensions,
                    candidate.IndexVersion,
                    cancellationToken);

                if (!indexReady)
                    throw new InvalidOperationException(
                        "Candidate HNSW index is not ready during activation.");

                var retired = await ExecuteNonQueryAsync(
                    connection,
                    transaction,
                    """
                    UPDATE embedding_profiles
                    SET "Status" = 'Retired'
                    WHERE "Id" = @activeId
                      AND "Status" = 'Active';
                    """,
                    cancellationToken,
                    ("@activeId", previousActiveId));

                if (retired != 1)
                    throw new InvalidOperationException(
                        "Could not retire the previous Active embedding profile.");

                var activated = await ExecuteNonQueryAsync(
                    connection,
                    transaction,
                    """
                    UPDATE embedding_profiles
                    SET
                        "Status" = 'Active',
                        "ActivatedAtUtc" = @activatedAtUtc
                    WHERE "Id" = @candidateId
                      AND "Status" = 'Building';
                    """,
                    cancellationToken,
                    ("@candidateId", candidateProfileId),
                    ("@activatedAtUtc", activatedAtUtc));

                if (activated != 1)
                    throw new InvalidOperationException(
                        "Could not activate the Building embedding profile.");

                await transaction.CommitAsync(cancellationToken);

                return new EmbeddingProfileActivationResult(
                    previousActiveId,
                    candidateProfileId,
                    activatedAtUtc);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }
        finally
        {
            if (closeWhenDone && connection.State == ConnectionState.Open)
                await connection.CloseAsync();
        }
    }

    private static async Task<CandidateState> ReadCandidateAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid candidateProfileId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT "Status", "Dimensions", "IndexVersion"
            FROM embedding_profiles
            WHERE "Id" = @candidateId
            FOR UPDATE;
            """;
        AddParameter(command, "@candidateId", candidateProfileId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException(
                "Candidate embedding profile was not found during activation.");

        return new CandidateState(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetInt32(2));
    }

    private static async Task<Guid> ReadActiveProfileIdAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT "Id"
            FROM embedding_profiles
            WHERE "Status" = 'Active'
            FOR UPDATE;
            """;

        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            ids.Add(reader.GetGuid(0));

        return ids.Count switch
        {
            1 => ids[0],
            0 => throw new InvalidOperationException(
                "No Active embedding profile exists during activation."),
            _ => throw new InvalidOperationException(
                "More than one Active embedding profile exists.")
        };
    }

    private static async Task EnsureEvidenceAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid evidenceId,
        Guid profileId,
        string expectedKind,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)::bigint
            FROM rag_evaluation_evidence
            WHERE "Id" = @evidenceId
              AND "EmbeddingProfileId" = @profileId
              AND "EvaluationKind" = @kind;
            """;
        AddParameter(command, "@evidenceId", evidenceId);
        AddParameter(command, "@profileId", profileId);
        AddParameter(command, "@kind", expectedKind);

        var count = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken));

        if (count != 1)
            throw new InvalidOperationException(
                $"Required {expectedKind} evaluation evidence is missing or not bound to the candidate profile.");
    }

    private static async Task<EmbeddingProfileCoverage> ReadCoverageAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid profileId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
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

    private static async Task<bool> IsIndexReadyAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid profileId,
        int dimensions,
        int indexVersion,
        CancellationToken cancellationToken)
    {
        var indexName =
            $"IX_kce_hnsw_{profileId:N}_v{indexVersion}";

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
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
        AddParameter(command, "@profilePattern", $"%{profileId:D}%");
        AddParameter(command, "@dimensionPattern", $"%vector({dimensions})%");

        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private static async Task<long> ExecuteScalarInt64Async(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<int> ExecuteNonQueryAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;

        foreach (var parameter in parameters)
            AddParameter(command, parameter.Name, parameter.Value);

        return await command.ExecuteNonQueryAsync(cancellationToken);
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

    private sealed record CandidateState(
        string Status,
        int Dimensions,
        int IndexVersion);
}
