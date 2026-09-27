using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Workflows;
using ICEHOTT.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace ICEHOTT.Persistence.Repositories;

public sealed class WorkflowAdministrationStore(ICEHOTTDbContext db)
    : IWorkflowAdministrationStore
{
    public async Task<WorkflowAdministrationPersistenceResult> CreateDefinitionAsync(
        Guid actorUserId, Guid workspaceId, string name, string? description,
        WorkspaceRole minimumRunRole, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var membership = await LockMembershipAsync(actorUserId, workspaceId, cancellationToken);
        if (membership is null)
            return await RollbackAsync(tx, WorkflowAdministrationPersistenceOutcome.WorkspaceNotFound);
        if (membership.Role < WorkspaceRole.Admin)
            return await RollbackAsync(tx, WorkflowAdministrationPersistenceOutcome.Forbidden);

        var definition = new WorkflowDefinition(
            Guid.NewGuid(), workspaceId, name, description,
            minimumRunRole, actorUserId, now);
        db.WorkflowDefinitions.Add(definition);
        db.WorkflowAuditEvents.Add(new WorkflowAuditEvent(
            Guid.NewGuid(), workspaceId, WorkflowAuditEventType.DefinitionCreated,
            now, workflowDefinitionId: definition.Id, actorUserId: actorUserId));

        return await CommitAsync(tx,
            new(WorkflowAdministrationPersistenceOutcome.Saved, Definition: definition),
            cancellationToken);
    }

    public async Task<WorkflowAdministrationPersistenceResult> CreateVersionAsync(
        Guid actorUserId, Guid workspaceId, Guid workflowId,
        string definitionJson, string definitionHash, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var membership = await LockMembershipAsync(actorUserId, workspaceId, cancellationToken);
        if (membership is null)
            return await RollbackAsync(tx, WorkflowAdministrationPersistenceOutcome.WorkspaceNotFound);
        if (membership.Role < WorkspaceRole.Admin)
            return await RollbackAsync(tx, WorkflowAdministrationPersistenceOutcome.Forbidden);

        var definition = await LockDefinitionAsync(workspaceId, workflowId, cancellationToken);
        if (definition is null)
            return await RollbackAsync(tx, WorkflowAdministrationPersistenceOutcome.WorkflowNotFound);
        if (definition.Status == WorkflowDefinitionStatus.Archived)
            return await RollbackAsync(tx, WorkflowAdministrationPersistenceOutcome.InvalidState);

        var maxVersion = await db.WorkflowVersions
            .Where(x => x.WorkspaceId == workspaceId &&
                        x.WorkflowDefinitionId == workflowId)
            .Select(x => (int?)x.VersionNumber)
            .MaxAsync(cancellationToken) ?? 0;

        var version = new WorkflowVersion(
            Guid.NewGuid(), workflowId, workspaceId, checked(maxVersion + 1),
            definitionJson, definitionHash, actorUserId, now);
        db.WorkflowVersions.Add(version);
        db.WorkflowAuditEvents.Add(new WorkflowAuditEvent(
            Guid.NewGuid(), workspaceId, WorkflowAuditEventType.VersionCreated,
            now, workflowDefinitionId: workflowId, actorUserId: actorUserId,
            detailJson: $"{{\"versionId\":\"{version.Id}\",\"versionNumber\":{version.VersionNumber}}}"));

        return await CommitAsync(tx,
            new(WorkflowAdministrationPersistenceOutcome.Saved,
                Definition: definition, Version: version),
            cancellationToken);
    }

    public async Task<WorkflowAdministrationPersistenceResult> ActivateVersionAsync(
        Guid actorUserId, Guid workspaceId, Guid workflowId, Guid versionId,
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var membership = await LockMembershipAsync(actorUserId, workspaceId, cancellationToken);
        if (membership is null)
            return await RollbackAsync(tx, WorkflowAdministrationPersistenceOutcome.WorkspaceNotFound);
        if (membership.Role < WorkspaceRole.Admin)
            return await RollbackAsync(tx, WorkflowAdministrationPersistenceOutcome.Forbidden);

        var definition = await LockDefinitionAsync(workspaceId, workflowId, cancellationToken);
        if (definition is null)
            return await RollbackAsync(tx, WorkflowAdministrationPersistenceOutcome.WorkflowNotFound);
        if (definition.Status == WorkflowDefinitionStatus.Archived)
            return await RollbackAsync(tx, WorkflowAdministrationPersistenceOutcome.InvalidState);

        var version = await db.WorkflowVersions.SingleOrDefaultAsync(
            x => x.WorkspaceId == workspaceId &&
                 x.WorkflowDefinitionId == workflowId &&
                 x.Id == versionId, cancellationToken);
        if (version is null)
            return await RollbackAsync(tx, WorkflowAdministrationPersistenceOutcome.WorkflowVersionNotFound);
        if (version.Status != WorkflowVersionStatus.Draft)
            return await RollbackAsync(tx, WorkflowAdministrationPersistenceOutcome.InvalidState);

        var active = await db.WorkflowVersions.SingleOrDefaultAsync(
            x => x.WorkspaceId == workspaceId &&
                 x.WorkflowDefinitionId == workflowId &&
                 x.Status == WorkflowVersionStatus.Active, cancellationToken);
        if (active is not null)
        {
            active.Retire(now);
            db.WorkflowAuditEvents.Add(new WorkflowAuditEvent(
                Guid.NewGuid(), workspaceId, WorkflowAuditEventType.VersionRetired,
                now, workflowDefinitionId: workflowId, actorUserId: actorUserId,
                detailJson: $"{{\"versionId\":\"{active.Id}\"}}"));
        }

        version.Activate(now);
        definition.MarkActive(now);
        db.WorkflowAuditEvents.Add(new WorkflowAuditEvent(
            Guid.NewGuid(), workspaceId, WorkflowAuditEventType.VersionActivated,
            now, workflowDefinitionId: workflowId, actorUserId: actorUserId,
            detailJson: $"{{\"versionId\":\"{version.Id}\"}}"));

        return await CommitAsync(tx,
            new(WorkflowAdministrationPersistenceOutcome.Saved,
                Definition: definition, Version: version),
            cancellationToken);
    }

    public async Task<WorkflowAdministrationPersistenceResult> ArchiveDefinitionAsync(
        Guid actorUserId, Guid workspaceId, Guid workflowId, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var membership = await LockMembershipAsync(actorUserId, workspaceId, cancellationToken);
        if (membership is null)
            return await RollbackAsync(tx, WorkflowAdministrationPersistenceOutcome.WorkspaceNotFound);
        if (membership.Role < WorkspaceRole.Admin)
            return await RollbackAsync(tx, WorkflowAdministrationPersistenceOutcome.Forbidden);

        var definition = await LockDefinitionAsync(workspaceId, workflowId, cancellationToken);
        if (definition is null)
            return await RollbackAsync(tx, WorkflowAdministrationPersistenceOutcome.WorkflowNotFound);
        if (definition.Status == WorkflowDefinitionStatus.Archived)
        {
            await tx.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            return new(WorkflowAdministrationPersistenceOutcome.Saved, Definition: definition);
        }

        var active = await db.WorkflowVersions.SingleOrDefaultAsync(
            x => x.WorkspaceId == workspaceId &&
                 x.WorkflowDefinitionId == workflowId &&
                 x.Status == WorkflowVersionStatus.Active, cancellationToken);
        if (active is not null)
        {
            active.Retire(now);
            db.WorkflowAuditEvents.Add(new WorkflowAuditEvent(
                Guid.NewGuid(), workspaceId, WorkflowAuditEventType.VersionRetired,
                now, workflowDefinitionId: workflowId, actorUserId: actorUserId,
                detailJson: $"{{\"versionId\":\"{active.Id}\"}}"));
        }

        definition.Archive(now);
        db.WorkflowAuditEvents.Add(new WorkflowAuditEvent(
            Guid.NewGuid(), workspaceId, WorkflowAuditEventType.DefinitionArchived,
            now, workflowDefinitionId: workflowId, actorUserId: actorUserId));

        return await CommitAsync(tx,
            new(WorkflowAdministrationPersistenceOutcome.Saved, Definition: definition),
            cancellationToken);
    }

    public async Task<WorkflowAdministrationPersistenceResult> CreateRunAsync(
        Guid actorUserId, Guid workspaceId, Guid workflowId,
        string idempotencyKey, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var membership = await LockMembershipAsync(actorUserId, workspaceId, cancellationToken);
        if (membership is null)
            return await RollbackAsync(tx, WorkflowAdministrationPersistenceOutcome.WorkspaceNotFound);

        var definition = await LockDefinitionAsync(workspaceId, workflowId, cancellationToken);
        if (definition is null)
            return await RollbackAsync(tx, WorkflowAdministrationPersistenceOutcome.WorkflowNotFound);
        if (definition.Status != WorkflowDefinitionStatus.Active)
            return await RollbackAsync(tx, WorkflowAdministrationPersistenceOutcome.InvalidState);
        if (membership.Role < definition.MinimumRunRole)
            return await RollbackAsync(tx, WorkflowAdministrationPersistenceOutcome.RunRoleNotAuthorized);

        var existing = await db.WorkflowRuns.SingleOrDefaultAsync(
            x => x.WorkspaceId == workspaceId &&
                 x.WorkflowDefinitionId == workflowId &&
                 x.IdempotencyKey == idempotencyKey, cancellationToken);
        if (existing is not null)
        {
            await tx.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            return new(WorkflowAdministrationPersistenceOutcome.IdempotentReplay, Run: existing);
        }

        var version = await db.WorkflowVersions.SingleOrDefaultAsync(
            x => x.WorkspaceId == workspaceId &&
                 x.WorkflowDefinitionId == workflowId &&
                 x.Status == WorkflowVersionStatus.Active, cancellationToken);
        if (version is null)
            return await RollbackAsync(tx, WorkflowAdministrationPersistenceOutcome.InvalidState);

        var run = new WorkflowRun(
            Guid.NewGuid(), workspaceId, workflowId, version.Id,
            actorUserId, actorUserId, idempotencyKey, now);
        db.WorkflowRuns.Add(run);
        db.WorkflowAuditEvents.Add(new WorkflowAuditEvent(
            Guid.NewGuid(), workspaceId, WorkflowAuditEventType.RunRequested,
            now, workflowDefinitionId: workflowId, workflowRunId: run.Id,
            actorUserId: actorUserId));

        return await CommitAsync(tx,
            new(WorkflowAdministrationPersistenceOutcome.Saved,
                Definition: definition, Version: version, Run: run),
            cancellationToken);
    }

    public async Task<WorkflowAdministrationPersistenceResult> CancelRunAsync(
        Guid actorUserId, Guid workspaceId, Guid runId, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var membership = await LockMembershipAsync(actorUserId, workspaceId, cancellationToken);
        if (membership is null)
            return await RollbackAsync(tx, WorkflowAdministrationPersistenceOutcome.WorkspaceNotFound);

        var run = await LockRunAsync(workspaceId, runId, cancellationToken);
        if (run is null)
            return await RollbackAsync(tx, WorkflowAdministrationPersistenceOutcome.RunNotFound);
        if (actorUserId != run.RequestedByUserId && membership.Role < WorkspaceRole.Admin)
            return await RollbackAsync(tx, WorkflowAdministrationPersistenceOutcome.Forbidden);
        if (run.Status is WorkflowRunStatus.Succeeded or WorkflowRunStatus.Failed or
            WorkflowRunStatus.Cancelled or WorkflowRunStatus.OutcomeUnknown)
            return await RollbackAsync(tx, WorkflowAdministrationPersistenceOutcome.InvalidState);

        run.RequestCancellation(actorUserId, now);
        if (run.Status is WorkflowRunStatus.Queued or WorkflowRunStatus.Waiting)
        {
            var activeStep = await db.WorkflowStepRuns
                .Where(x => x.WorkspaceId == workspaceId &&
                            x.WorkflowRunId == runId &&
                            x.Status != WorkflowStepRunStatus.Succeeded &&
                            x.Status != WorkflowStepRunStatus.Failed &&
                            x.Status != WorkflowStepRunStatus.Cancelled &&
                            x.Status != WorkflowStepRunStatus.Skipped &&
                            x.Status != WorkflowStepRunStatus.OutcomeUnknown)
                .OrderByDescending(x => x.Attempt)
                .FirstOrDefaultAsync(cancellationToken);
            if (activeStep is not null)
            {
                activeStep.Cancel(now);
                var checkpoint = await db.WorkflowCheckpoints.SingleOrDefaultAsync(
                    x => x.WorkspaceId == workspaceId &&
                         x.StepRunId == activeStep.Id &&
                         x.Status == WorkflowCheckpointStatus.Pending,
                    cancellationToken);
                checkpoint?.Expire(now);
            }

            run.Cancel(now);
            db.WorkflowAuditEvents.Add(new WorkflowAuditEvent(
                Guid.NewGuid(), workspaceId, WorkflowAuditEventType.RunCancelled,
                now, workflowDefinitionId: run.WorkflowDefinitionId,
                workflowRunId: run.Id, actorUserId: actorUserId));
        }
        else
        {
            db.WorkflowAuditEvents.Add(new WorkflowAuditEvent(
                Guid.NewGuid(), workspaceId, WorkflowAuditEventType.CancellationRequested,
                now, workflowDefinitionId: run.WorkflowDefinitionId,
                workflowRunId: run.Id, actorUserId: actorUserId));
        }

        return await CommitAsync(tx,
            new(WorkflowAdministrationPersistenceOutcome.Saved, Run: run),
            cancellationToken);
    }

    public async Task<WorkflowAdministrationPersistenceResult> RetryRunAsync(
        Guid actorUserId, Guid workspaceId, Guid runId,
        CancellationToken cancellationToken = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var membership = await LockMembershipAsync(actorUserId, workspaceId, cancellationToken);
        if (membership is null)
            return await RollbackAsync(tx, WorkflowAdministrationPersistenceOutcome.WorkspaceNotFound);

        var run = await LockRunAsync(workspaceId, runId, cancellationToken);
        if (run is null)
            return await RollbackAsync(tx, WorkflowAdministrationPersistenceOutcome.RunNotFound);
        if (actorUserId != run.RequestedByUserId && membership.Role < WorkspaceRole.Admin)
            return await RollbackAsync(tx, WorkflowAdministrationPersistenceOutcome.Forbidden);

        return await RollbackAsync(
            tx, WorkflowAdministrationPersistenceOutcome.RetryNotSupported, run);
    }

    private async Task<WorkspaceMembership?> LockMembershipAsync(
        Guid userId, Guid workspaceId, CancellationToken cancellationToken)
    {
        if (db.Database.IsNpgsql())
            return await db.WorkspaceMemberships.FromSqlInterpolated(
                    $"SELECT * FROM workspace_memberships WHERE \"WorkspaceId\" = {workspaceId} AND \"UserId\" = {userId} FOR SHARE")
                .SingleOrDefaultAsync(cancellationToken);

        return await db.WorkspaceMemberships.SingleOrDefaultAsync(
            x => x.WorkspaceId == workspaceId && x.UserId == userId,
            cancellationToken);
    }

    private async Task<WorkflowDefinition?> LockDefinitionAsync(
        Guid workspaceId, Guid workflowId, CancellationToken cancellationToken)
    {
        if (db.Database.IsNpgsql())
            return await db.WorkflowDefinitions.FromSqlInterpolated(
                    $"SELECT * FROM workflow_definitions WHERE \"WorkspaceId\" = {workspaceId} AND \"Id\" = {workflowId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);

        return await db.WorkflowDefinitions.SingleOrDefaultAsync(
            x => x.WorkspaceId == workspaceId && x.Id == workflowId,
            cancellationToken);
    }

    private async Task<WorkflowRun?> LockRunAsync(
        Guid workspaceId, Guid runId, CancellationToken cancellationToken)
    {
        if (db.Database.IsNpgsql())
            return await db.WorkflowRuns.FromSqlInterpolated(
                    $"SELECT * FROM workflow_runs WHERE \"WorkspaceId\" = {workspaceId} AND \"Id\" = {runId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);

        return await db.WorkflowRuns.SingleOrDefaultAsync(
            x => x.WorkspaceId == workspaceId && x.Id == runId,
            cancellationToken);
    }

    private async Task<WorkflowAdministrationPersistenceResult> CommitAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        WorkflowAdministrationPersistenceResult success,
        CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return success;
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            return new(WorkflowAdministrationPersistenceOutcome.ConcurrencyConflict);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            return new(WorkflowAdministrationPersistenceOutcome.ConcurrencyConflict);
        }
    }

    private async Task<WorkflowAdministrationPersistenceResult> RollbackAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        WorkflowAdministrationPersistenceOutcome outcome,
        WorkflowRun? run = null)
    {
        await transaction.RollbackAsync(CancellationToken.None);
        db.ChangeTracker.Clear();
        return new(outcome, Run: run);
    }
}
