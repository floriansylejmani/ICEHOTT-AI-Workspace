using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Tools;
using Microsoft.EntityFrameworkCore;

namespace ICEHOTT.Persistence.Repositories;

public sealed class ToolPolicyRepository(ICEHOTTDbContext db) : IToolPolicyRepository
{
    public Task<ToolPolicy?> FindAsync(
        Guid workspaceId,
        string toolName,
        CancellationToken cancellationToken = default) =>
        db.ToolPolicies.SingleOrDefaultAsync(
            x => x.WorkspaceId == workspaceId && x.ToolName == toolName,
            cancellationToken);

    public async Task<IReadOnlyList<ToolPolicy>> ListAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default) =>
        await db.ToolPolicies
            .AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ToolPolicyAuditEvent>> ListAuditEventsAsync(
        Guid workspaceId,
        string toolName,
        CancellationToken cancellationToken = default) =>
        await db.ToolPolicyAuditEvents
            .AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId && x.ToolName == toolName)
            .OrderBy(x => x.NewVersion)
            .ToListAsync(cancellationToken);

    public Task AddAsync(
        ToolPolicy policy,
        CancellationToken cancellationToken = default) =>
        db.ToolPolicies.AddAsync(policy, cancellationToken).AsTask();

    public Task AddAuditEventAsync(
        ToolPolicyAuditEvent auditEvent,
        CancellationToken cancellationToken = default) =>
        db.ToolPolicyAuditEvents.AddAsync(auditEvent, cancellationToken).AsTask();

    public async Task GuardUnchangedAsync(
        ToolPolicy? observed,
        Guid workspaceId,
        string toolName,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (observed is null)
        {
            // "No overlay" was observed. Insert the version-0 default row: a
            // concurrent first policy insert collides on the primary key, so
            // exactly one of the two commits.
            await db.ToolPolicies.AddAsync(
                ToolPolicy.Default(workspaceId, toolName, now),
                cancellationToken);
            return;
        }

        // Re-assert the observed version: EF emits
        // UPDATE ... SET "Version" = v WHERE ... AND "Version" = v,
        // which affects 0 rows if an Owner update committed in between.
        if (db.Entry(observed).State == EntityState.Detached)
            db.ToolPolicies.Attach(observed);

        db.Entry(observed).Property(x => x.Version).IsModified = true;
    }

    public Task<ToolPersistenceOutcome> SaveChangesAsync(
        CancellationToken cancellationToken = default) =>
        ToolPersistence.SaveAsync(db, cancellationToken);
}
