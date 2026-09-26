using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Workflows;
using Microsoft.EntityFrameworkCore;

namespace ICEHOTT.Persistence.Repositories;

public sealed class WorkflowRepository(ICEHOTTDbContext db) : IWorkflowRepository
{
    public Task AddDefinitionAsync(
        WorkflowDefinition definition,
        CancellationToken cancellationToken = default) =>
        db.WorkflowDefinitions.AddAsync(definition, cancellationToken).AsTask();

    public Task<WorkflowDefinition?> FindDefinitionAsync(
        Guid workspaceId,
        Guid definitionId,
        CancellationToken cancellationToken = default) =>
        db.WorkflowDefinitions.SingleOrDefaultAsync(
            x => x.WorkspaceId == workspaceId && x.Id == definitionId,
            cancellationToken);

    public async Task<IReadOnlyList<WorkflowDefinition>> ListDefinitionsAsync(
        Guid workspaceId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var capped = Math.Clamp(limit, 1, 200);
        var query = db.WorkflowDefinitions.AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId);

        if (IsSqlite())
        {
            var items = await query.ToListAsync(cancellationToken);
            return items.OrderByDescending(x => x.CreatedAtUtc).Take(capped).ToArray();
        }

        return await query.OrderByDescending(x => x.CreatedAtUtc)
            .Take(capped).ToListAsync(cancellationToken);
    }

    public Task AddVersionAsync(
        WorkflowVersion version,
        CancellationToken cancellationToken = default) =>
        db.WorkflowVersions.AddAsync(version, cancellationToken).AsTask();

    public Task<WorkflowVersion?> FindVersionAsync(
        Guid workspaceId,
        Guid definitionId,
        Guid versionId,
        CancellationToken cancellationToken = default) =>
        db.WorkflowVersions.SingleOrDefaultAsync(
            x => x.WorkspaceId == workspaceId &&
                 x.WorkflowDefinitionId == definitionId &&
                 x.Id == versionId,
            cancellationToken);

    public async Task<IReadOnlyList<WorkflowVersion>> ListVersionsAsync(
        Guid workspaceId,
        Guid definitionId,
        CancellationToken cancellationToken = default)
    {
        var items = await db.WorkflowVersions.AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId &&
                        x.WorkflowDefinitionId == definitionId)
            .ToListAsync(cancellationToken);

        return items.OrderByDescending(x => x.VersionNumber).ToArray();
    }

    public Task AddRunAsync(
        WorkflowRun run,
        CancellationToken cancellationToken = default) =>
        db.WorkflowRuns.AddAsync(run, cancellationToken).AsTask();
    public Task<WorkflowRun?> FindRunAsync(
        Guid workspaceId,
        Guid runId,
        CancellationToken cancellationToken = default) =>
        db.WorkflowRuns.SingleOrDefaultAsync(
            x => x.WorkspaceId == workspaceId && x.Id == runId,
            cancellationToken);

    public async Task<IReadOnlyList<WorkflowRun>> ListRunsAsync(
        Guid workspaceId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var capped = Math.Clamp(limit, 1, 200);
        var query = db.WorkflowRuns.AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId);

        if (IsSqlite())
        {
            var items = await query.ToListAsync(cancellationToken);
            return items.OrderByDescending(x => x.CreatedAtUtc).Take(capped).ToArray();
        }

        return await query.OrderByDescending(x => x.CreatedAtUtc)
            .Take(capped).ToListAsync(cancellationToken);
    }

    public Task AddStepRunAsync(
        WorkflowStepRun stepRun,
        CancellationToken cancellationToken = default) =>
        db.WorkflowStepRuns.AddAsync(stepRun, cancellationToken).AsTask();

    public async Task<IReadOnlyList<WorkflowStepRun>> ListStepRunsAsync(
        Guid workspaceId,
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        var items = await db.WorkflowStepRuns.AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId && x.WorkflowRunId == runId)
            .ToListAsync(cancellationToken);

        return items.OrderBy(x => x.StepKey)
            .ThenBy(x => x.Attempt)
            .ToArray();
    }

    public Task AddCheckpointAsync(
        WorkflowCheckpoint checkpoint,
        CancellationToken cancellationToken = default) =>
        db.WorkflowCheckpoints.AddAsync(checkpoint, cancellationToken).AsTask();

    public Task<WorkflowCheckpoint?> FindCheckpointAsync(
        Guid workspaceId,
        Guid checkpointId,
        CancellationToken cancellationToken = default) =>
        db.WorkflowCheckpoints.SingleOrDefaultAsync(
            x => x.WorkspaceId == workspaceId && x.Id == checkpointId,
            cancellationToken);

    public Task AddTriggerAsync(
        WorkflowTrigger trigger,
        CancellationToken cancellationToken = default) =>
        db.WorkflowTriggers.AddAsync(trigger, cancellationToken).AsTask();

    public Task<WorkflowTrigger?> FindTriggerAsync(
        Guid workspaceId,
        Guid triggerId,
        CancellationToken cancellationToken = default) =>
        db.WorkflowTriggers.SingleOrDefaultAsync(
            x => x.WorkspaceId == workspaceId && x.Id == triggerId,
            cancellationToken);
    public async Task<IReadOnlyList<WorkflowTrigger>> ListTriggersAsync(
        Guid workspaceId,
        Guid definitionId,
        CancellationToken cancellationToken = default)
    {
        var items = await db.WorkflowTriggers.AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId &&
                        x.WorkflowDefinitionId == definitionId)
            .ToListAsync(cancellationToken);

        return items.OrderBy(x => x.CreatedAtUtc).ToArray();
    }

    public Task AddTriggerFireAsync(
        WorkflowTriggerFire fire,
        CancellationToken cancellationToken = default) =>
        db.WorkflowTriggerFires.AddAsync(fire, cancellationToken).AsTask();

    public Task<WorkflowTriggerFire?> FindTriggerFireAsync(
        Guid workspaceId,
        Guid triggerId,
        DateTimeOffset scheduledForUtc,
        CancellationToken cancellationToken = default) =>
        db.WorkflowTriggerFires.SingleOrDefaultAsync(
            x => x.WorkspaceId == workspaceId &&
                 x.TriggerId == triggerId &&
                 x.ScheduledForUtc == scheduledForUtc,
            cancellationToken);

    private bool IsSqlite() =>
        db.Database.ProviderName?.Contains(
            "Sqlite",
            StringComparison.OrdinalIgnoreCase) == true;
}
