using System.Data;
using System.Data.Common;
using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Knowledge;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ICEHOTT.Persistence.Repositories;

public sealed class PostgresEmbeddingProfileActivationStore(ICEHOTTDbContext db)
    : IEmbeddingProfileActivationStore
{
    public async Task ActivateAsync(
        Guid buildingProfileId,
        Guid evidenceId,
        DateTimeOffset activatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        try
        {
            var connection = db.Database.GetDbConnection();
            var dbTransaction = transaction.GetDbTransaction();
            await ExecuteAsync(
                connection,
                dbTransaction,
                """
                LOCK TABLE embedding_profiles IN SHARE ROW EXCLUSIVE MODE;
                LOCK TABLE knowledge_documents IN SHARE MODE;
                LOCK TABLE knowledge_chunks IN SHARE MODE;
                LOCK TABLE knowledge_chunk_embeddings IN SHARE MODE;
                """,
                cancellationToken);

            var candidate = await db.EmbeddingProfiles.SingleOrDefaultAsync(
                x => x.Id == buildingProfileId,
                cancellationToken)
                ?? throw new InvalidOperationException(
                    "Building embedding profile was not found.");

            if (candidate.Status != EmbeddingProfileStatus.Building)
                throw new InvalidOperationException(
                    "Candidate embedding profile is no longer Building.");

            var active = await db.EmbeddingProfiles.SingleOrDefaultAsync(
                x => x.Status == EmbeddingProfileStatus.Active,
                cancellationToken)
                ?? throw new InvalidOperationException(
                    "Active embedding profile was not found.");
            var evidence = await db.RagEvaluationEvidence
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    x => x.Id == evidenceId,
                    cancellationToken)
                ?? throw new InvalidOperationException(
                    "Evaluation evidence was not found.");

            if (evidence.EmbeddingProfileId != candidate.Id)
                throw new InvalidOperationException(
                    "Evaluation evidence does not belong to the candidate profile.");

            var coverage = await ReadCoverageAsync(
                connection,
                dbTransaction,
                candidate.Id,
                cancellationToken);

            if (!coverage.IsComplete)
                throw new InvalidOperationException(
                    $"Final embedding coverage is incomplete: " +
                    $"{coverage.EmbeddedReadyChunks}/{coverage.ExpectedReadyChunks}.");

            active.Retire();
            await db.SaveChangesAsync(cancellationToken);
            candidate.Activate(activatedAtUtc);
            await db.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
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
                (
                    SELECT COUNT(*)
                    FROM knowledge_chunks c
                    INNER JOIN knowledge_documents d
                        ON d."Id" = c."DocumentId"
                       AND d."WorkspaceId" = c."WorkspaceId"
                    WHERE d."Status" = 'Ready'
                ),
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
                );
            """;

        AddParameter(command, "@profileId", profileId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException(
                "Could not calculate final embedding coverage.");

        return new EmbeddingProfileCoverage(
            reader.GetInt64(0),
            reader.GetInt64(1));
    }

    private static async Task ExecuteAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
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
