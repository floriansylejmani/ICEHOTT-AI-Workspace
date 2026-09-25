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

    public async Task<IReadOnlyList<ToolExecution>> ListExpiredRunningAsync(
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var query = db.ToolExecutions
            .Where(x => x.Status == ToolExecutionStatus.Running);

        if (db.Database.ProviderName?.Contains(
                "Sqlite",
                StringComparison.OrdinalIgnoreCase) == true)
        {
            var items = await query.ToListAsync(cancellationToken);
            return items
                .Where(x => x.LeaseExpiresAtUtc is null || x.LeaseExpiresAtUtc <= now)
                .OrderBy(x => x.LeaseExpiresAtUtc)
                .Take(Math.Clamp(limit, 1, 100))
                .ToArray();
        }

        return await query
            .Where(x => x.LeaseExpiresAtUtc == null || x.LeaseExpiresAtUtc <= now)
            .OrderBy(x => x.LeaseExpiresAtUtc)
            .Take(Math.Clamp(limit, 1, 100))
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
