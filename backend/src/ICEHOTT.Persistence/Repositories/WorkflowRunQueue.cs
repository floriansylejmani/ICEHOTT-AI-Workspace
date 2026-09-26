using System.Data;
using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Tools;
using ICEHOTT.Domain.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace ICEHOTT.Persistence.Repositories;

public sealed class WorkflowRunQueue(
    ICEHOTTDbContext db,
    TimeProvider clock) : IWorkflowRunQueue
{
    public async Task<WorkflowRunLease?> LeaseNextAsync(
        Guid workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        if (workerId == Guid.Empty)
            throw new ArgumentException("Worker ID is required.", nameof(workerId));

        var safeLeaseDuration = TimeSpan.FromSeconds(
            Math.Clamp(leaseDuration.TotalSeconds, 5, 3600));

        if (IsSqlite())
            return await LeaseSqliteAsync(
                workerId,
                safeLeaseDuration,
                cancellationToken);

        return await LeasePostgresAsync(
            workerId,
            safeLeaseDuration,
            cancellationToken);
    }
    public async Task<bool> RenewLeaseAsync(
        Guid runId,
        Guid workerId,
        int leaseGeneration,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        if (runId == Guid.Empty || workerId == Guid.Empty || leaseGeneration < 1)
            return false;

        var now = clock.GetUtcNow();
        var safeLeaseDuration = TimeSpan.FromSeconds(
            Math.Clamp(leaseDuration.TotalSeconds, 5, 3600));
        var leaseExpiresAtUtc = now.Add(safeLeaseDuration);

        var affected = await db.WorkflowRuns
            .Where(x =>
                x.Id == runId &&
                x.Status == WorkflowRunStatus.Running &&
                x.LeaseOwnerId == workerId &&
                x.LeaseGeneration == leaseGeneration)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(
                    x => x.LeaseExpiresAtUtc,
                    leaseExpiresAtUtc),
                cancellationToken);

        return affected == 1;
    }

    public async Task<bool> RequestCancellationAsync(
        Guid workspaceId,
        Guid runId,
        Guid actorUserId,
        DateTimeOffset requestedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (workspaceId == Guid.Empty ||
            runId == Guid.Empty ||
            actorUserId == Guid.Empty)
            return false;
        var terminal = new[]
        {
            WorkflowRunStatus.Succeeded,
            WorkflowRunStatus.Failed,
            WorkflowRunStatus.Cancelled,
            WorkflowRunStatus.OutcomeUnknown
        };

        var affected = await db.WorkflowRuns
            .Where(x =>
                x.WorkspaceId == workspaceId &&
                x.Id == runId &&
                !terminal.Contains(x.Status))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(
                        x => x.CancellationRequestedByUserId,
                        x => x.CancellationRequestedByUserId ?? actorUserId)
                    .SetProperty(
                        x => x.CancellationRequestedAtUtc,
                        x => x.CancellationRequestedAtUtc ?? requestedAtUtc),
                cancellationToken);

        return affected == 1;
    }

    public Task<bool> IsCancellationRequestedAsync(
        Guid runId,
        Guid workerId,
        int leaseGeneration,
        CancellationToken cancellationToken = default)
    {
        if (runId == Guid.Empty || workerId == Guid.Empty || leaseGeneration < 1)
            return Task.FromResult(false);

        return db.WorkflowRuns.AsNoTracking().AnyAsync(
            x =>
                x.Id == runId &&
                x.Status == WorkflowRunStatus.Running &&
                x.LeaseOwnerId == workerId &&
                x.LeaseGeneration == leaseGeneration &&
                x.CancellationRequestedAtUtc != null,
            cancellationToken);
    }
    public async Task<WorkflowRunPersistenceOutcome> SaveFencedAsync(
        Guid runId,
        Guid workerId,
        int leaseGeneration,
        CancellationToken cancellationToken = default)
    {
        if (runId == Guid.Empty || workerId == Guid.Empty || leaseGeneration < 1)
            return WorkflowRunPersistenceOutcome.LeaseLost;

        if (IsSqlite())
        {
            var current = await db.WorkflowRuns.AsNoTracking().SingleOrDefaultAsync(
                x => x.Id == runId,
                cancellationToken);

            if (current is null ||
                current.LeaseOwnerId != workerId ||
                current.LeaseGeneration != leaseGeneration)
            {
                db.ChangeTracker.Clear();
                return WorkflowRunPersistenceOutcome.LeaseLost;
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return WorkflowRunPersistenceOutcome.Saved;
            }
            catch (DbUpdateConcurrencyException)
            {
                db.ChangeTracker.Clear();
                return WorkflowRunPersistenceOutcome.ConcurrencyConflict;
            }
        }

        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = """
            SELECT 1
            FROM workflow_runs
            WHERE "Id" = @runId
              AND "LeaseOwnerId" = @workerId
              AND "LeaseGeneration" = @leaseGeneration
            FOR UPDATE
            """;
        command.Parameters.Add(new NpgsqlParameter("runId", runId));
        command.Parameters.Add(new NpgsqlParameter("workerId", workerId));
        command.Parameters.Add(new NpgsqlParameter("leaseGeneration", leaseGeneration));

        var guard = await command.ExecuteScalarAsync(cancellationToken);
        if (guard is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            return WorkflowRunPersistenceOutcome.LeaseLost;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return WorkflowRunPersistenceOutcome.Saved;
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            return WorkflowRunPersistenceOutcome.ConcurrencyConflict;
        }
    }

    private async Task<WorkflowRunLease?> LeasePostgresAsync(
        Guid workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var leaseExpiresAtUtc = now.Add(leaseDuration);
        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;

        try
        {
            if (closeWhenDone)
                await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                WITH next_run AS (
                    SELECT r."Id"
                    FROM workflow_runs AS r
                    WHERE r."Status" IN ('Queued', 'Waiting', 'Running')
                      AND (
                        (
                            r."CancellationRequestedAtUtc" IS NOT NULL
                            AND (
                                r."Status" IN ('Queued', 'Waiting')
                                OR (
                                    r."Status" = 'Running'
                                    AND r."LeaseExpiresAtUtc" IS NOT NULL
                                    AND r."LeaseExpiresAtUtc" <= @now
                                )
                            )
                        )
                        OR r."Status" = 'Queued'
                        OR (
                            r."Status" = 'Waiting'
                            AND (
                                (
                                    r."WaitReason" IN ('Delay', 'RetryBackoff')
                                    AND r."ResumeAtUtc" IS NOT NULL
                                    AND r."ResumeAtUtc" <= @now
                                )
                                OR (
                                    r."WaitReason" = 'Checkpoint'
                                    AND EXISTS (
                                        SELECT 1
                                        FROM workflow_step_runs AS s
                                        JOIN workflow_checkpoints AS c
                                          ON c."StepRunId" = s."Id"
                                         AND c."WorkflowRunId" = s."WorkflowRunId"
                                         AND c."WorkspaceId" = s."WorkspaceId"
                                        WHERE s."WorkflowRunId" = r."Id"
                                          AND s."WorkspaceId" = r."WorkspaceId"
                                          AND s."StepKey" = r."CurrentStepKey"
                                          AND s."Status" = 'WaitingForCheckpoint'
                                          AND c."Status" IN ('Approved', 'Rejected', 'Expired')
                                    )
                                )
                                OR (
                                    r."WaitReason" = 'ToolExecution'
                                    AND EXISTS (
                                        SELECT 1
                                        FROM workflow_step_runs AS s
                                        JOIN tool_executions AS t
                                          ON t."Id" = s."ToolExecutionId"
                                         AND t."WorkspaceId" = s."WorkspaceId"
                                        WHERE s."WorkflowRunId" = r."Id"
                                          AND s."WorkspaceId" = r."WorkspaceId"
                                          AND s."StepKey" = r."CurrentStepKey"
                                          AND s."ToolExecutionId" IS NOT NULL
                                          AND t."Status" IN (4, 5, 6, 7, 8, 9)
                                    )
                                )
                            )
                        )
                        OR (
                            r."Status" = 'Running'
                            AND r."LeaseExpiresAtUtc" IS NOT NULL
                            AND r."LeaseExpiresAtUtc" <= @now
                            AND NOT EXISTS (
                                SELECT 1
                                FROM workflow_step_runs AS s
                                JOIN tool_executions AS t
                                  ON t."Id" = s."ToolExecutionId"
                                 AND t."WorkspaceId" = s."WorkspaceId"
                                WHERE s."WorkflowRunId" = r."Id"
                                  AND s."WorkspaceId" = r."WorkspaceId"
                                  AND s."StepKey" = r."CurrentStepKey"
                                  AND s."StepType" = 'Tool'
                                  AND s."ToolExecutionId" IS NOT NULL
                                  AND t."Status" = 3
                            )
                        )
                      )
                    ORDER BY
                        (r."CancellationRequestedAtUtc" IS NULL),
                        COALESCE(r."ResumeAtUtc", r."CreatedAtUtc"),
                        r."CreatedAtUtc",
                        r."Id"
                    FOR UPDATE SKIP LOCKED
                    LIMIT 1
                )
                UPDATE workflow_runs AS r
                SET
                    "Status" = 'Running',
                    "StartedAtUtc" = COALESCE(r."StartedAtUtc", @now),
                    "LeaseOwnerId" = @workerId,
                    "LeaseExpiresAtUtc" = @leaseExpiresAtUtc,
                    "LeaseGeneration" = r."LeaseGeneration" + 1,
                    "WaitReason" = NULL,
                    "ResumeAtUtc" = NULL
                FROM next_run
                WHERE r."Id" = next_run."Id"
                RETURNING
                    r."Id",
                    r."WorkspaceId",
                    r."WorkflowDefinitionId",
                    r."WorkflowVersionId",
                    r."RunAsUserId",
                    r."CurrentStepKey",
                    r."LeaseGeneration",
                    r."LeaseExpiresAtUtc"
                """;
            command.Parameters.Add(new NpgsqlParameter("now", now));
            command.Parameters.Add(new NpgsqlParameter("workerId", workerId));
            command.Parameters.Add(new NpgsqlParameter("leaseExpiresAtUtc", leaseExpiresAtUtc));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;

            return new WorkflowRunLease(
                reader.GetGuid(0),
                workerId,
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetGuid(3),
                reader.GetGuid(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetInt32(6),
                reader.GetFieldValue<DateTimeOffset>(7));
        }
        finally
        {
            if (closeWhenDone && connection.State == ConnectionState.Open)
                await connection.CloseAsync();
        }
    }

    private async Task<WorkflowRunLease?> LeaseSqliteAsync(
        Guid workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var runs = await db.WorkflowRuns
            .Where(x => x.Status == WorkflowRunStatus.Queued ||
                        x.Status == WorkflowRunStatus.Waiting ||
                        x.Status == WorkflowRunStatus.Running)
            .OrderBy(x => x.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        foreach (var run in runs)
        {
            var cancellationDue =
                run.CancellationRequestedAtUtc is not null &&
                (run.Status is WorkflowRunStatus.Queued or WorkflowRunStatus.Waiting ||
                 run.Status == WorkflowRunStatus.Running &&
                 run.LeaseExpiresAtUtc is { } expiredCancellation &&
                 expiredCancellation <= now);

            var timerDue =
                run.Status == WorkflowRunStatus.Waiting &&
                run.WaitReason is WorkflowWaitReason.Delay or WorkflowWaitReason.RetryBackoff &&
                run.ResumeAtUtc is { } resumeAt &&
                resumeAt <= now;

            var checkpointDue = false;
            if (run.Status == WorkflowRunStatus.Waiting &&
                run.WaitReason == WorkflowWaitReason.Checkpoint &&
                !string.IsNullOrWhiteSpace(run.CurrentStepKey))
            {
                checkpointDue = await (
                    from step in db.WorkflowStepRuns.AsNoTracking()
                    join checkpoint in db.WorkflowCheckpoints.AsNoTracking()
                        on new
                        {
                            StepRunId = step.Id,
                            step.WorkflowRunId,
                            step.WorkspaceId
                        }
                        equals new
                        {
                            checkpoint.StepRunId,
                            checkpoint.WorkflowRunId,
                            checkpoint.WorkspaceId
                        }
                    where step.WorkflowRunId == run.Id &&
                          step.WorkspaceId == run.WorkspaceId &&
                          step.StepKey == run.CurrentStepKey &&
                          step.Status == WorkflowStepRunStatus.WaitingForCheckpoint &&
                          checkpoint.Status != WorkflowCheckpointStatus.Pending
                    select checkpoint.Id)
                    .AnyAsync(cancellationToken);
            }

            var expiredRunning =
                run.Status == WorkflowRunStatus.Running &&
                run.LeaseExpiresAtUtc is { } expired &&
                expired <= now;

            if (!cancellationDue &&
                run.Status != WorkflowRunStatus.Queued &&
                !timerDue &&
                !checkpointDue &&
                !expiredRunning)
                continue;

            var leaseExpiresAtUtc = now.Add(leaseDuration);
            if (run.Status == WorkflowRunStatus.Queued)
                run.Start(run.CurrentStepKey, now);
            else if (run.Status == WorkflowRunStatus.Waiting)
                run.Resume();

            var generation = run.ClaimLease(workerId, leaseExpiresAtUtc);
            await db.SaveChangesAsync(cancellationToken);

            return new WorkflowRunLease(
                run.Id,
                workerId,
                run.WorkspaceId,
                run.WorkflowDefinitionId,
                run.WorkflowVersionId,
                run.RunAsUserId,
                run.CurrentStepKey,
                generation,
                leaseExpiresAtUtc);
        }

        return null;
    }

    private bool IsSqlite() =>
        db.Database.ProviderName?.Contains(
            "Sqlite",
            StringComparison.OrdinalIgnoreCase) == true;
}
