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

    public async Task<ToolPersistenceOutcome> SaveChangesAsync(
        CancellationToken cancellationToken = default)
    {
        var insertedExecution = db.ChangeTracker
            .Entries<ToolExecution>()
            .FirstOrDefault(x => x.State == EntityState.Added)
            ?.Entity;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return ToolPersistenceOutcome.Saved;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Status is a concurrency token: another request already moved
            // this execution to a different state. Drop everything staged
            // by this request so no audit event or side effect leaks out.
            DetachPendingChanges();
            return ToolPersistenceOutcome.ConcurrencyConflict;
        }
        catch (DbUpdateException) when (insertedExecution is not null)
        {
            DetachPendingChanges();

            var winnerExists = await db.ToolExecutions
                .AsNoTracking()
                .AnyAsync(
                    x => x.WorkspaceId == insertedExecution.WorkspaceId &&
                         x.ToolName == insertedExecution.ToolName &&
                         x.IdempotencyKey == insertedExecution.IdempotencyKey,
                    cancellationToken);

            if (winnerExists)
                return ToolPersistenceOutcome.DuplicateIdempotencyKey;

            throw;
        }
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

    private void DetachPendingChanges()
    {
        foreach (var entry in db.ChangeTracker.Entries()
                     .Where(x => x.State is EntityState.Added
                         or EntityState.Modified
                         or EntityState.Deleted)
                     .ToList())
            entry.State = EntityState.Detached;
    }
}
