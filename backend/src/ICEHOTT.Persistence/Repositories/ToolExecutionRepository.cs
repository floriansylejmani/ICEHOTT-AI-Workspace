using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Tools;
using Microsoft.EntityFrameworkCore;

namespace ICEHOTT.Persistence.Repositories;

public sealed class ToolExecutionRepository(ICEHOTTDbContext db)
    : IToolExecutionRepository
{
    public Task<ToolExecution?> FindAsync(
        Guid workspaceId,
        Guid executionId,
        CancellationToken cancellationToken = default) =>
        db.ToolExecutions.SingleOrDefaultAsync(
            x => x.WorkspaceId == workspaceId && x.Id == executionId,
            cancellationToken);

    public Task<ToolExecution?> FindByIdempotencyAsync(
        Guid workspaceId,
        string toolName,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        db.ToolExecutions.SingleOrDefaultAsync(
            x => x.WorkspaceId == workspaceId &&
                 x.ToolName == toolName &&
                 x.IdempotencyKey == idempotencyKey,
            cancellationToken);

    public async Task<IReadOnlyList<ToolExecution>> ListAsync(
        Guid workspaceId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var query = db.ToolExecutions
            .AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId);

        if (db.Database.ProviderName?.Contains(
                "Sqlite",
                StringComparison.OrdinalIgnoreCase) == true)
        {
            var items = await query.ToListAsync(cancellationToken);
            return items
                .OrderByDescending(x => x.RequestedAtUtc)
                .Take(limit)
                .ToArray();
        }

        return await query
            .OrderByDescending(x => x.RequestedAtUtc)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ToolExecutionAuditEvent>> ListAuditEventsAsync(
        Guid workspaceId,
        Guid executionId,
        CancellationToken cancellationToken = default)
    {
        var items = await db.ToolExecutionAuditEvents
            .AsNoTracking()
            .Where(x =>
                x.WorkspaceId == workspaceId &&
                x.ExecutionId == executionId)
            .ToListAsync(cancellationToken);

        return items
            .OrderBy(x => x.OccurredAtUtc)
            .ThenBy(x => x.Id)
            .ToArray();
    }

    public Task AddAsync(
        ToolExecution execution,
        CancellationToken cancellationToken = default) =>
        db.ToolExecutions.AddAsync(execution, cancellationToken).AsTask();

    public Task AddAuditEventAsync(
        ToolExecutionAuditEvent auditEvent,
        CancellationToken cancellationToken = default) =>
        db.ToolExecutionAuditEvents
            .AddAsync(auditEvent, cancellationToken)
            .AsTask();

    // Status is a concurrency token: a state-machine race, an idempotency
    // insert race or a policy-version race is reported as an outcome and
    // nothing staged by this request (audit events, side effects) leaks out.
    public Task<ToolPersistenceOutcome> SaveChangesAsync(
        CancellationToken cancellationToken = default) =>
        ToolPersistence.SaveAsync(db, cancellationToken);

    public async Task<ToolPersistenceOutcome> SaveAdmissionAsync(
        ToolQuotaCharge charge,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // The charge is taken first so concurrent admissions for the same
        // (workspace, tool) serialise on the counter row before any other lock.
        if (!await TryChargeAsync(charge, cancellationToken))
        {
            ToolPersistence.DetachPendingChanges(db);
            await transaction.RollbackAsync(CancellationToken.None);
            return ToolPersistenceOutcome.QuotaExceeded;
        }

        // EF wraps this SaveChanges in a savepoint, so a failed insert leaves
        // the transaction usable for ToolPersistence's race classification.
        var outcome = await ToolPersistence.SaveAsync(db, cancellationToken);
        if (outcome != ToolPersistenceOutcome.Saved)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return outcome;
        }

        await transaction.CommitAsync(cancellationToken);
        return outcome;
    }

    /// <summary>
    /// Single-statement conditional upsert. A new window resets the count; the
    /// current window increments only below the limit. PostgreSQL row-locks the
    /// conflicting row and re-evaluates the WHERE on its latest committed
    /// version, so concurrent charges can never overshoot the limit. A row
    /// with a later window (clock skew between nodes) is treated as current.
    /// Returns whether a row was inserted or updated.
    /// </summary>
    private async Task<bool> TryChargeAsync(
        ToolQuotaCharge charge,
        CancellationToken cancellationToken)
    {
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO tool_quota_counters AS c ("WorkspaceId", "ToolName", "WindowStartUnixSeconds", "Count")
            VALUES ({charge.WorkspaceId}, {charge.ToolName}, {charge.WindowStartUnixSeconds}, 1)
            ON CONFLICT ("WorkspaceId", "ToolName") DO UPDATE SET
                "Count" = CASE WHEN excluded."WindowStartUnixSeconds" > c."WindowStartUnixSeconds"
                               THEN 1 ELSE c."Count" + 1 END,
                "WindowStartUnixSeconds" = CASE WHEN excluded."WindowStartUnixSeconds" > c."WindowStartUnixSeconds"
                               THEN excluded."WindowStartUnixSeconds" ELSE c."WindowStartUnixSeconds" END
            WHERE excluded."WindowStartUnixSeconds" > c."WindowStartUnixSeconds"
               OR c."Count" < {charge.PermitLimit}
            """, cancellationToken);

        return affected > 0;
    }

    public void DiscardPendingSideEffects(ToolExecution execution)
    {
        foreach (var entry in db.ChangeTracker.Entries().ToList())
        {
            if (ReferenceEquals(entry.Entity, execution))
                continue;

            switch (entry.State)
            {
                case EntityState.Added:
                    entry.State = EntityState.Detached;
                    break;
                case EntityState.Modified:
                    entry.CurrentValues.SetValues(entry.OriginalValues);
                    entry.State = EntityState.Unchanged;
                    break;
                case EntityState.Deleted:
                    entry.State = EntityState.Unchanged;
                    break;
            }
        }
    }
}
