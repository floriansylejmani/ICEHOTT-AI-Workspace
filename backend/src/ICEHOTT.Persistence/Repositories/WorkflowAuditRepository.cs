using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Workflows;
using Microsoft.EntityFrameworkCore;

namespace ICEHOTT.Persistence.Repositories;

public sealed class WorkflowAuditRepository(ICEHOTTDbContext db)
    : IWorkflowAuditRepository
{
    public Task AddAsync(
        WorkflowAuditEvent auditEvent,
        CancellationToken cancellationToken = default) =>
        db.WorkflowAuditEvents.AddAsync(auditEvent, cancellationToken).AsTask();

    public async Task<IReadOnlyList<WorkflowAuditEvent>> ListWorkspaceAsync(
        Guid workspaceId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var capped = Math.Clamp(limit, 1, 500);
        var query = db.WorkflowAuditEvents.AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId);

        if (IsSqlite())
        {
            var items = await query.ToListAsync(cancellationToken);
            return items.OrderByDescending(x => x.CreatedAtUtc)
                .ThenByDescending(x => x.Id)
                .Take(capped)
                .ToArray();
        }

        return await query.OrderByDescending(x => x.CreatedAtUtc)
            .ThenByDescending(x => x.Id)
            .Take(capped)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WorkflowAuditEvent>> ListRunAsync(
        Guid workspaceId,
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        var items = await db.WorkflowAuditEvents.AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId &&
                        x.WorkflowRunId == runId)
            .ToListAsync(cancellationToken);

        return items.OrderBy(x => x.CreatedAtUtc)
            .ThenBy(x => x.Id)
            .ToArray();
    }

    private bool IsSqlite() =>
        db.Database.ProviderName?.Contains(
            "Sqlite",
            StringComparison.OrdinalIgnoreCase) == true;
}
