using System.Data;
using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Workflows;
using ICEHOTT.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace ICEHOTT.Persistence.Repositories;

public sealed class ArtifactRepository(ICEHOTTDbContext db) : IArtifactRepository
{
    public Task AddAsync(
        Artifact artifact,
        CancellationToken cancellationToken = default) =>
        db.Artifacts.AddAsync(artifact, cancellationToken).AsTask();

    public async Task<ArtifactAddOutcome> TryAddWithinQuotaAsync(
        Artifact artifact,
        int maxArtifactCount,
        long maxWorkspaceBytes,
        CancellationToken cancellationToken = default)
    {
        if (maxArtifactCount < 1)
            throw new ArgumentOutOfRangeException(nameof(maxArtifactCount));
        if (maxWorkspaceBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maxWorkspaceBytes));

        if (artifact.SizeBytes > maxWorkspaceBytes)
            return ArtifactAddOutcome.QuotaExceeded;

        if (IsSqlite())
        {
            if (!await db.Workspaces.AnyAsync(
                    x => x.Id == artifact.WorkspaceId,
                    cancellationToken))
                return ArtifactAddOutcome.WorkspaceMissing;

            if (artifact.IdempotencyKey is not null &&
                await db.Artifacts.AnyAsync(
                    x => x.WorkspaceId == artifact.WorkspaceId &&
                         x.IdempotencyKey == artifact.IdempotencyKey,
                    cancellationToken))
                return ArtifactAddOutcome.IdempotencyExists;

            if (await ValidateWorkflowBindingAsync(artifact, cancellationToken)
                is { } sqliteBinding)
                return sqliteBinding;

            var usage = await GetUsageAsync(
                artifact.WorkspaceId,
                cancellationToken);

            if (usage.ActiveArtifactCount >= maxArtifactCount ||
                usage.ActiveBytes > maxWorkspaceBytes - artifact.SizeBytes)
                return ArtifactAddOutcome.QuotaExceeded;

            db.Artifacts.Add(artifact);
            await db.SaveChangesAsync(cancellationToken);
            return ArtifactAddOutcome.Added;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        var connection = db.Database.GetDbConnection();
        await using (var lockCommand = connection.CreateCommand())
        {
            lockCommand.Transaction = transaction.GetDbTransaction();
            lockCommand.CommandText = """
                SELECT 1
                FROM workspaces
                WHERE "Id" = @workspaceId
                FOR UPDATE
                """;
            lockCommand.Parameters.Add(
                new NpgsqlParameter("workspaceId", artifact.WorkspaceId));

            var locked = await lockCommand.ExecuteScalarAsync(cancellationToken);
            if (locked is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return ArtifactAddOutcome.WorkspaceMissing;
            }
        }

        if (artifact.IdempotencyKey is not null &&
            await db.Artifacts.AnyAsync(
                x => x.WorkspaceId == artifact.WorkspaceId &&
                     x.IdempotencyKey == artifact.IdempotencyKey,
                cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return ArtifactAddOutcome.IdempotencyExists;
        }

        if (await ValidateWorkflowBindingAsync(artifact, cancellationToken)
            is { } bindingFailure)
        {
            await transaction.RollbackAsync(cancellationToken);
            return bindingFailure;
        }

        var usageSnapshot = await GetUsageAsync(
            artifact.WorkspaceId,
            cancellationToken);

        if (usageSnapshot.ActiveArtifactCount >= maxArtifactCount ||
            usageSnapshot.ActiveBytes > maxWorkspaceBytes - artifact.SizeBytes)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ArtifactAddOutcome.QuotaExceeded;
        }

        db.Artifacts.Add(artifact);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ArtifactAddOutcome.Added;
    }
    /// <summary>
    /// Revalidates a workflow-bound artifact inside the insert transaction so a
    /// concurrent demotion, cancellation, or step completion cannot slip between
    /// the service pre-check and the insert. Membership and run rows are share-locked
    /// (PostgreSQL) so writers of those rows serialize against this insert.
    /// </summary>
    private async Task<ArtifactAddOutcome?> ValidateWorkflowBindingAsync(
        Artifact artifact,
        CancellationToken cancellationToken)
    {
        if (artifact.WorkflowRunId is not { } runId ||
            artifact.StepRunId is not { } stepRunId)
            return null;

        var workspaceId = artifact.WorkspaceId;
        var userId = artifact.CreatedByUserId;

        var membership = IsSqlite()
            ? await db.WorkspaceMemberships.AsNoTracking().SingleOrDefaultAsync(
                x => x.WorkspaceId == workspaceId && x.UserId == userId,
                cancellationToken)
            : await db.WorkspaceMemberships.FromSqlInterpolated(
                    $"SELECT * FROM workspace_memberships WHERE \"WorkspaceId\" = {workspaceId} AND \"UserId\" = {userId} FOR SHARE")
                .AsNoTracking()
                .SingleOrDefaultAsync(cancellationToken);
        if (membership is null)
            return ArtifactAddOutcome.WorkspaceMissing;

        var run = IsSqlite()
            ? await db.WorkflowRuns.AsNoTracking().SingleOrDefaultAsync(
                x => x.WorkspaceId == workspaceId && x.Id == runId,
                cancellationToken)
            : await db.WorkflowRuns.FromSqlInterpolated(
                    $"SELECT * FROM workflow_runs WHERE \"WorkspaceId\" = {workspaceId} AND \"Id\" = {runId} FOR SHARE")
                .AsNoTracking()
                .SingleOrDefaultAsync(cancellationToken);
        if (run is null)
            return ArtifactAddOutcome.BindingNotFound;

        var step = await db.WorkflowStepRuns.AsNoTracking().SingleOrDefaultAsync(
            x => x.WorkspaceId == workspaceId &&
                 x.WorkflowRunId == runId &&
                 x.Id == stepRunId,
            cancellationToken);
        if (step is null || step.StepType != WorkflowStepType.Artifact)
            return ArtifactAddOutcome.BindingNotFound;

        if (run.RunAsUserId != userId && membership.Role < WorkspaceRole.Admin)
            return ArtifactAddOutcome.BindingNotAuthorized;

        if (run.Status != WorkflowRunStatus.Waiting ||
            run.WaitReason != WorkflowWaitReason.Artifact ||
            run.CancellationRequestedAtUtc is not null ||
            step.Status != WorkflowStepRunStatus.WaitingForArtifact ||
            !string.Equals(run.CurrentStepKey, step.StepKey, StringComparison.Ordinal))
            return ArtifactAddOutcome.BindingInvalidState;

        // A step that already holds a Ready artifact is satisfied; a second upload
        // could never be consumed. Pending rows are ignored so an abandoned upload
        // cannot wedge the step.
        if (await db.Artifacts.AnyAsync(
                x => x.WorkspaceId == workspaceId &&
                     x.WorkflowRunId == runId &&
                     x.StepRunId == stepRunId &&
                     x.Status == ArtifactStatus.Ready,
                cancellationToken))
            return ArtifactAddOutcome.BindingInvalidState;

        return null;
    }

    public Task<Artifact?> FindAsync(
        Guid workspaceId,
        Guid artifactId,
        CancellationToken cancellationToken = default) =>
        db.Artifacts.SingleOrDefaultAsync(
            x => x.WorkspaceId == workspaceId && x.Id == artifactId,
            cancellationToken);

    public Task<Artifact?> FindReadyByStepAsync(
        Guid workspaceId,
        Guid workflowRunId,
        Guid stepRunId,
        CancellationToken cancellationToken = default) =>
        db.Artifacts
            .Where(x => x.WorkspaceId == workspaceId &&
                        x.WorkflowRunId == workflowRunId &&
                        x.StepRunId == stepRunId &&
                        x.Status == ArtifactStatus.Ready)
            .OrderBy(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<Artifact?> FindByIdempotencyKeyAsync(
        Guid workspaceId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        var normalized = idempotencyKey.Trim();
        return db.Artifacts.SingleOrDefaultAsync(
            x => x.WorkspaceId == workspaceId &&
                 x.IdempotencyKey == normalized,
            cancellationToken);
    }

    public async Task<IReadOnlyList<Artifact>> ListAsync(
        Guid workspaceId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var capped = Math.Clamp(limit, 1, 200);
        var query = db.Artifacts.AsNoTracking()
            .Where(x =>
                x.WorkspaceId == workspaceId &&
                x.Status != ArtifactStatus.Deleted);

        if (IsSqlite())
        {
            var items = await query.ToListAsync(cancellationToken);
            return items.OrderByDescending(x => x.CreatedAtUtc)
                .Take(capped)
                .ToArray();
        }

        return await query.OrderByDescending(x => x.CreatedAtUtc)
            .Take(capped)
            .ToListAsync(cancellationToken);
    }

    public async Task<ArtifactWorkspaceUsage> GetUsageAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default)
    {
        var active = db.Artifacts
            .Where(x =>
                x.WorkspaceId == workspaceId &&
                (x.Status == ArtifactStatus.Pending ||
                 x.Status == ArtifactStatus.Ready));

        var count = await active.CountAsync(cancellationToken);
        var bytes = await active
            .Select(x => (long?)x.SizeBytes)
            .SumAsync(cancellationToken) ?? 0;

        return new(count, bytes);
    }

    public async Task<IReadOnlyList<Artifact>> ListMaintenanceCandidatesAsync(
        DateTimeOffset pendingOlderThanUtc,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var capped = Math.Clamp(limit, 1, 200);

        var query = db.Artifacts.Where(x =>
            (x.Status == ArtifactStatus.Pending &&
             x.CreatedAtUtc <= pendingOlderThanUtc) ||
            (x.Status == ArtifactStatus.Failed &&
             x.StagingKey != null) ||
            (x.Status == ArtifactStatus.Deleted &&
             x.StorageDeletedAtUtc == null));

        if (IsSqlite())
        {
            var items = await query.ToListAsync(cancellationToken);
            return items.OrderBy(x => x.CreatedAtUtc)
                .Take(capped)
                .ToArray();
        }

        return await query.OrderBy(x => x.CreatedAtUtc)
            .Take(capped)
            .ToListAsync(cancellationToken);
    }

    private bool IsSqlite() =>
        db.Database.ProviderName?.Contains(
            "Sqlite",
            StringComparison.OrdinalIgnoreCase) == true;
}
