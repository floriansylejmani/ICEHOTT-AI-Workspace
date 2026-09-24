using System.Data;
using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Knowledge;
using Microsoft.EntityFrameworkCore;

namespace ICEHOTT.Persistence.Repositories;

public sealed class KnowledgeJobQueue(
    ICEHOTTDbContext db,
    TimeProvider clock) : IKnowledgeJobQueue
{
    public async Task EnqueueAsync(
        Guid documentId,
        Guid workspaceId,
        CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var existing = await db.KnowledgeProcessingJobs.SingleOrDefaultAsync(
            x => x.DocumentId == documentId && x.WorkspaceId == workspaceId,
            cancellationToken);

        if (existing is null)
        {
            await db.KnowledgeProcessingJobs.AddAsync(
                new KnowledgeProcessingJob(
                    Guid.NewGuid(),
                    documentId,
                    workspaceId,
                    now),
                cancellationToken);
            return;
        }

        if (existing.Status is KnowledgeProcessingStatus.Completed or KnowledgeProcessingStatus.Failed)
            existing.Reset(now);
    }

    public async Task<KnowledgeJobLease?> LeaseNextAsync(
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workerId))
            throw new ArgumentException("Worker ID is required.", nameof(workerId));

        var safeLeaseDuration = TimeSpan.FromSeconds(
            Math.Clamp(leaseDuration.TotalSeconds, 5, 3600));

        if (db.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true)
            return await LeaseSqliteAsync(workerId, safeLeaseDuration, cancellationToken);

        return await LeasePostgresAsync(workerId, safeLeaseDuration, cancellationToken);
    }

    public async Task<bool> RenewLeaseAsync(
        Guid jobId,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workerId)) return false;

        var now = clock.GetUtcNow();
        var safeLeaseDuration = TimeSpan.FromSeconds(
            Math.Clamp(leaseDuration.TotalSeconds, 5, 3600));

        if (IsSqlite())
        {
            var job = await db.KnowledgeProcessingJobs.SingleOrDefaultAsync(
                x => x.Id == jobId,
                cancellationToken);

            if (job is null ||
                job.Status != KnowledgeProcessingStatus.Processing ||
                !string.Equals(job.LockedBy, workerId, StringComparison.Ordinal))
                return false;

            job.RenewLease(workerId, now, safeLeaseDuration);
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }

        var lockedUntil = now.Add(safeLeaseDuration);

        var affected = await db.KnowledgeProcessingJobs
            .Where(x =>
                x.Id == jobId &&
                x.Status == KnowledgeProcessingStatus.Processing &&
                x.LockedBy == workerId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.LockedUntilUtc, lockedUntil)
                .SetProperty(x => x.UpdatedAtUtc, now),
                cancellationToken);

        return affected == 1;
    }

    public async Task<bool> CompleteAsync(
        Guid jobId,
        string workerId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workerId)) return false;

        var now = clock.GetUtcNow();

        if (IsSqlite())
        {
            var job = await db.KnowledgeProcessingJobs.SingleOrDefaultAsync(
                x => x.Id == jobId,
                cancellationToken);

            if (job is null ||
                job.Status != KnowledgeProcessingStatus.Processing ||
                !string.Equals(job.LockedBy, workerId, StringComparison.Ordinal))
                return false;

            job.Complete(now);
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }

        var affected = await db.KnowledgeProcessingJobs
            .Where(x =>
                x.Id == jobId &&
                x.Status == KnowledgeProcessingStatus.Processing &&
                x.LockedBy == workerId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, KnowledgeProcessingStatus.Completed)
                .SetProperty(x => x.LockedBy, (string?)null)
                .SetProperty(x => x.LockedUntilUtc, (DateTimeOffset?)null)
                .SetProperty(x => x.LastError, (string?)null)
                .SetProperty(x => x.CompletedAtUtc, now)
                .SetProperty(x => x.UpdatedAtUtc, now),
                cancellationToken);

        return affected == 1;
    }

    public async Task<bool> RetryAsync(
        Guid jobId,
        string workerId,
        string error,
        DateTimeOffset availableAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workerId)) return false;

        var now = clock.GetUtcNow();
        var normalizedError = NormalizeError(error);

        if (IsSqlite())
        {
            var job = await db.KnowledgeProcessingJobs.SingleOrDefaultAsync(
                x => x.Id == jobId,
                cancellationToken);

            if (job is null ||
                job.Status != KnowledgeProcessingStatus.Processing ||
                !string.Equals(job.LockedBy, workerId, StringComparison.Ordinal))
                return false;

            job.Retry(normalizedError, availableAtUtc, now);
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }

        var affected = await db.KnowledgeProcessingJobs
            .Where(x =>
                x.Id == jobId &&
                x.Status == KnowledgeProcessingStatus.Processing &&
                x.LockedBy == workerId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, KnowledgeProcessingStatus.Queued)
                .SetProperty(x => x.LockedBy, (string?)null)
                .SetProperty(x => x.LockedUntilUtc, (DateTimeOffset?)null)
                .SetProperty(x => x.LastError, normalizedError)
                .SetProperty(x => x.AvailableAtUtc, availableAtUtc)
                .SetProperty(x => x.UpdatedAtUtc, now),
                cancellationToken);

        return affected == 1;
    }

    public async Task<bool> FailAsync(
        Guid jobId,
        string workerId,
        string error,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workerId)) return false;

        var now = clock.GetUtcNow();
        var normalizedError = NormalizeError(error);

        if (IsSqlite())
        {
            var job = await db.KnowledgeProcessingJobs.SingleOrDefaultAsync(
                x => x.Id == jobId,
                cancellationToken);

            if (job is null ||
                job.Status != KnowledgeProcessingStatus.Processing ||
                !string.Equals(job.LockedBy, workerId, StringComparison.Ordinal))
                return false;

            job.Fail(normalizedError, now);
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }

        var affected = await db.KnowledgeProcessingJobs
            .Where(x =>
                x.Id == jobId &&
                x.Status == KnowledgeProcessingStatus.Processing &&
                x.LockedBy == workerId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, KnowledgeProcessingStatus.Failed)
                .SetProperty(x => x.LockedBy, (string?)null)
                .SetProperty(x => x.LockedUntilUtc, (DateTimeOffset?)null)
                .SetProperty(x => x.LastError, normalizedError)
                .SetProperty(x => x.CompletedAtUtc, now)
                .SetProperty(x => x.UpdatedAtUtc, now),
                cancellationToken);

        return affected == 1;
    }

    private async Task<KnowledgeJobLease?> LeasePostgresAsync(
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var lockedUntil = now.Add(leaseDuration);
        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;

        try
        {
            if (closeWhenDone)
                await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                WITH next_job AS (
                    SELECT "Id"
                    FROM knowledge_processing_jobs
                    WHERE "Attempts" < "MaxAttempts"
                      AND (
                        ("Status" = 'Queued' AND "AvailableAtUtc" <= @now)
                        OR
                        ("Status" = 'Processing' AND "LockedUntilUtc" IS NOT NULL AND "LockedUntilUtc" <= @now)
                      )
                    ORDER BY "AvailableAtUtc", "CreatedAtUtc", "Id"
                    FOR UPDATE SKIP LOCKED
                    LIMIT 1
                )
                UPDATE knowledge_processing_jobs AS job
                SET
                    "Status" = 'Processing',
                    "Attempts" = job."Attempts" + 1,
                    "LockedBy" = @workerId,
                    "LockedUntilUtc" = @lockedUntil,
                    "LastError" = NULL,
                    "UpdatedAtUtc" = @now
                FROM next_job
                WHERE job."Id" = next_job."Id"
                RETURNING
                    job."Id",
                    job."DocumentId",
                    job."WorkspaceId",
                    job."Attempts",
                    job."MaxAttempts",
                    job."LockedBy";
                """;

            AddParameter(command, "@now", now);
            AddParameter(command, "@workerId", workerId);
            AddParameter(command, "@lockedUntil", lockedUntil);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;

            return new KnowledgeJobLease(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetString(5));
        }
        finally
        {
            if (closeWhenDone && connection.State == ConnectionState.Open)
                await connection.CloseAsync();
        }
    }

    private async Task<KnowledgeJobLease?> LeaseSqliteAsync(
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        var jobs = await db.KnowledgeProcessingJobs
            .ToListAsync(cancellationToken);

        var job = jobs
            .Where(x =>
                x.Attempts < x.MaxAttempts &&
                (
                    (x.Status == KnowledgeProcessingStatus.Queued && x.AvailableAtUtc <= now) ||
                    (x.Status == KnowledgeProcessingStatus.Processing &&
                     x.LockedUntilUtc is not null &&
                     x.LockedUntilUtc <= now)
                ))
            .OrderBy(x => x.AvailableAtUtc)
            .ThenBy(x => x.CreatedAtUtc)
            .ThenBy(x => x.Id)
            .FirstOrDefault();

        if (job is null) return null;

        job.Lease(workerId, now, leaseDuration);
        await db.SaveChangesAsync(cancellationToken);

        return new KnowledgeJobLease(
            job.Id,
            job.DocumentId,
            job.WorkspaceId,
            job.Attempts,
            job.MaxAttempts,
            workerId);
    }

    private bool IsSqlite() =>
        db.Database.ProviderName?.Contains(
            "Sqlite",
            StringComparison.OrdinalIgnoreCase) == true;

    private static string NormalizeError(string error)
    {
        var normalized = string.IsNullOrWhiteSpace(error)
            ? "processing_failed"
            : error.Trim();

        return normalized.Length <= 2000
            ? normalized
            : normalized[..2000];
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
