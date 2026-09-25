using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Tools;
using Microsoft.EntityFrameworkCore;

namespace ICEHOTT.Persistence.Repositories;

/// <summary>
/// Shared SaveChanges for the tool execution and tool policy repositories
/// (they share one DbContext / unit of work). Classifies the races the tool
/// path relies on instead of letting them surface as 500s; in every non-Saved
/// outcome nothing was committed and all staged changes are detached.
/// </summary>
internal static class ToolPersistence
{
    public static async Task<ToolPersistenceOutcome> SaveAsync(
        ICEHOTTDbContext db,
        CancellationToken cancellationToken)
    {
        var insertedExecution = db.ChangeTracker
            .Entries<ToolExecution>()
            .FirstOrDefault(x => x.State == EntityState.Added)
            ?.Entity;
        var insertedPolicy = db.ChangeTracker
            .Entries<ToolPolicy>()
            .FirstOrDefault(x => x.State == EntityState.Added)
            ?.Entity;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return ToolPersistenceOutcome.Saved;
        }
        catch (DbUpdateConcurrencyException exception)
        {
            // A concurrency token did not match: either execution Status
            // (state-machine race) or policy Version (policy changed).
            var policyConflict = exception.Entries.Any(x => x.Entity is ToolPolicy);
            DetachPendingChanges(db);
            return policyConflict
                ? ToolPersistenceOutcome.PolicyConflict
                : ToolPersistenceOutcome.ConcurrencyConflict;
        }
        catch (DbUpdateException) when (insertedExecution is not null || insertedPolicy is not null)
        {
            DetachPendingChanges(db);

            if (insertedExecution is not null &&
                await db.ToolExecutions.AsNoTracking().AnyAsync(
                    x => x.WorkspaceId == insertedExecution.WorkspaceId &&
                         x.ToolName == insertedExecution.ToolName &&
                         x.IdempotencyKey == insertedExecution.IdempotencyKey,
                    cancellationToken))
                return ToolPersistenceOutcome.DuplicateIdempotencyKey;

            if (insertedPolicy is not null &&
                await db.ToolPolicies.AsNoTracking().AnyAsync(
                    x => x.WorkspaceId == insertedPolicy.WorkspaceId &&
                         x.ToolName == insertedPolicy.ToolName,
                    cancellationToken))
                return ToolPersistenceOutcome.PolicyConflict;

            throw;
        }
    }

    public static void DetachPendingChanges(ICEHOTTDbContext db)
    {
        foreach (var entry in db.ChangeTracker.Entries()
                     .Where(x => x.State is EntityState.Added
                         or EntityState.Modified
                         or EntityState.Deleted)
                     .ToList())
            entry.State = EntityState.Detached;
    }
}
