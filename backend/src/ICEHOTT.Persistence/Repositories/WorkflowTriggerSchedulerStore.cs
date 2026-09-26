using System.Text.Json;
using ICEHOTT.Application.Abstractions;
using ICEHOTT.Application.Workflows;
using ICEHOTT.Domain.Workflows;
using Microsoft.EntityFrameworkCore;

namespace ICEHOTT.Persistence.Repositories;

public sealed class WorkflowTriggerSchedulerStore(
    ICEHOTTDbContext db,
    IWorkflowScheduleCalculator schedules)
    : IWorkflowTriggerSchedulerStore
{
    public async Task<WorkflowTriggerSchedulerResult> ClaimNextDueAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (!db.Database.IsNpgsql())
            return await ClaimSqliteAsync(now, cancellationToken);

        await using var transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);

        var trigger = await db.WorkflowTriggers
            .FromSqlInterpolated($"""
                SELECT *
                FROM workflow_triggers
                WHERE "Enabled" = TRUE
                  AND "NextRunAtUtc" <= {now}
                ORDER BY "NextRunAtUtc", "Id"
                FOR UPDATE SKIP LOCKED
                LIMIT 1
                """)
            .SingleOrDefaultAsync(cancellationToken);

        if (trigger is null)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return new(WorkflowTriggerSchedulerDisposition.NoWork);
        }

        var scheduledForUtc = trigger.NextRunAtUtc;
        var nextRunAtUtc = schedules.GetNextOccurrence(
            trigger.ScheduleExpression,
            trigger.TimeZoneId,
            now);

        if (nextRunAtUtc is null)
        {
            var failed = new WorkflowTriggerFire(
                Guid.NewGuid(),
                trigger.WorkspaceId,
                trigger.Id,
                trigger.WorkflowVersionId,
                scheduledForUtc,
                BuildFireKey(trigger.Id, scheduledForUtc),
                now);
            failed.Fail(now);
            trigger.Disable();

            db.WorkflowTriggerFires.Add(failed);
            db.WorkflowAuditEvents.Add(
                NewAudit(
                    trigger,
                    WorkflowAuditEventType.TriggerFireFailed,
                    now,
                    detailJson: JsonSerializer.Serialize(new
                    {
                        reason = "invalid_schedule"
                    })));
            db.WorkflowAuditEvents.Add(
                NewAudit(
                    trigger,
                    WorkflowAuditEventType.TriggerDisabled,
                    now,
                    detailJson: JsonSerializer.Serialize(new
                    {
                        reason = "invalid_schedule"
                    })));

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new(
                WorkflowTriggerSchedulerDisposition.Failed,
                trigger.Id,
                failed.Id,
                Reason: "invalid_schedule");
        }

        var fire = new WorkflowTriggerFire(
            Guid.NewGuid(),
            trigger.WorkspaceId,
            trigger.Id,
            trigger.WorkflowVersionId,
            scheduledForUtc,
            BuildFireKey(trigger.Id, scheduledForUtc),
            now);

        db.WorkflowTriggerFires.Add(fire);
        trigger.RecordFire(
            scheduledForUtc,
            nextRunAtUtc.Value);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new(
            WorkflowTriggerSchedulerDisposition.Claimed,
            trigger.Id,
            fire.Id);
    }

    public async Task<WorkflowTriggerSchedulerResult> ProcessNextClaimedAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (!db.Database.IsNpgsql())
            return await ProcessSqliteAsync(now, cancellationToken);

        await using var transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);

        var fire = await db.WorkflowTriggerFires
            .FromSqlInterpolated($"""
                SELECT *
                FROM workflow_trigger_fires
                WHERE "Status" = 'Claimed'
                ORDER BY "CreatedAtUtc", "Id"
                FOR UPDATE SKIP LOCKED
                LIMIT 1
                """)
            .SingleOrDefaultAsync(cancellationToken);

        if (fire is null)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return new(WorkflowTriggerSchedulerDisposition.NoWork);
        }

        var trigger = await db.WorkflowTriggers
            .FromSqlInterpolated($"""
                SELECT *
                FROM workflow_triggers
                WHERE "Id" = {fire.TriggerId}
                  AND "WorkspaceId" = {fire.WorkspaceId}
                FOR UPDATE
                """)
            .SingleAsync(cancellationToken);

        if (!trigger.Enabled)
        {
            fire.Skip(now);
            db.WorkflowAuditEvents.Add(
                NewAudit(
                    trigger,
                    WorkflowAuditEventType.TriggerSkipped,
                    now,
                    detailJson: JsonSerializer.Serialize(new
                    {
                        reason = "trigger_disabled"
                    })));

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new(
                WorkflowTriggerSchedulerDisposition.Skipped,
                trigger.Id,
                fire.Id,
                Reason: "trigger_disabled");
        }

        var definition = await db.WorkflowDefinitions
            .SingleAsync(
                x => x.Id == trigger.WorkflowDefinitionId &&
                     x.WorkspaceId == trigger.WorkspaceId,
                cancellationToken);

        var version = await db.WorkflowVersions
            .SingleAsync(
                x => x.Id == trigger.WorkflowVersionId &&
                     x.WorkflowDefinitionId == trigger.WorkflowDefinitionId &&
                     x.WorkspaceId == trigger.WorkspaceId,
                cancellationToken);

        if (definition.Status != WorkflowDefinitionStatus.Active)
        {
            fire.Skip(now);
            trigger.Disable();
            AddDisabledAudits(
                trigger,
                fire,
                now,
                "workflow_not_active",
                skipped: true);

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new(
                WorkflowTriggerSchedulerDisposition.Skipped,
                trigger.Id,
                fire.Id,
                Reason: "workflow_not_active");
        }

        if (version.Status == WorkflowVersionStatus.Draft)
        {
            fire.Fail(now);
            trigger.Disable();
            AddDisabledAudits(
                trigger,
                fire,
                now,
                "workflow_version_not_executable",
                skipped: false);

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new(
                WorkflowTriggerSchedulerDisposition.Failed,
                trigger.Id,
                fire.Id,
                Reason: "workflow_version_not_executable");
        }

        var membership = (await db.WorkspaceMemberships
                .FromSqlInterpolated($"""
                    SELECT *
                    FROM workspace_memberships
                    WHERE "WorkspaceId" = {trigger.WorkspaceId}
                      AND "UserId" = {trigger.RunAsUserId}
                    FOR SHARE
                    """)
                .AsNoTracking()
                .ToListAsync(cancellationToken))
            .SingleOrDefault();

        if (membership is null ||
            membership.Role < definition.MinimumRunRole)
        {
            fire.Fail(now);
            trigger.Disable();
            AddDisabledAudits(
                trigger,
                fire,
                now,
                "run_as_not_authorized",
                skipped: false);

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new(
                WorkflowTriggerSchedulerDisposition.Failed,
                trigger.Id,
                fire.Id,
                Reason: "run_as_not_authorized");
        }

        var run = new WorkflowRun(
            Guid.NewGuid(),
            trigger.WorkspaceId,
            trigger.WorkflowDefinitionId,
            trigger.WorkflowVersionId,
            trigger.CreatedByUserId,
            trigger.RunAsUserId,
            BuildRunIdempotencyKey(fire.FireKey),
            now);

        db.WorkflowRuns.Add(run);
        fire.MarkRunCreated(run.Id, now);
        db.WorkflowAuditEvents.Add(
            new WorkflowAuditEvent(
                Guid.NewGuid(),
                trigger.WorkspaceId,
                WorkflowAuditEventType.TriggerFired,
                now,
                workflowDefinitionId: trigger.WorkflowDefinitionId,
                workflowRunId: run.Id,
                workflowTriggerId: trigger.Id,
                actorUserId: null,
                detailJson: JsonSerializer.Serialize(new
                {
                    fireId = fire.Id,
                    scheduledForUtc = fire.ScheduledForUtc
                })));

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new(
            WorkflowTriggerSchedulerDisposition.RunCreated,
            trigger.Id,
            fire.Id,
            run.Id);
    }

    private async Task<WorkflowTriggerSchedulerResult> ClaimSqliteAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);

        var trigger = await db.WorkflowTriggers
            .Where(x => x.Enabled && x.NextRunAtUtc <= now)
            .OrderBy(x => x.NextRunAtUtc)
            .ThenBy(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (trigger is null)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return new(WorkflowTriggerSchedulerDisposition.NoWork);
        }

        var next = schedules.GetNextOccurrence(
            trigger.ScheduleExpression,
            trigger.TimeZoneId,
            now);

        if (next is null)
        {
            trigger.Disable();
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(
                WorkflowTriggerSchedulerDisposition.Failed,
                trigger.Id,
                Reason: "invalid_schedule");
        }

        var scheduled = trigger.NextRunAtUtc;
        var fire = new WorkflowTriggerFire(
            Guid.NewGuid(),
            trigger.WorkspaceId,
            trigger.Id,
            trigger.WorkflowVersionId,
            scheduled,
            BuildFireKey(trigger.Id, scheduled),
            now);

        db.WorkflowTriggerFires.Add(fire);
        trigger.RecordFire(scheduled, next.Value);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new(
            WorkflowTriggerSchedulerDisposition.Claimed,
            trigger.Id,
            fire.Id);
    }

    private async Task<WorkflowTriggerSchedulerResult> ProcessSqliteAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);

        var fire = await db.WorkflowTriggerFires
            .Where(x => x.Status == WorkflowTriggerFireStatus.Claimed)
            .OrderBy(x => x.CreatedAtUtc)
            .ThenBy(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (fire is null)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return new(WorkflowTriggerSchedulerDisposition.NoWork);
        }

        var trigger = await db.WorkflowTriggers.SingleAsync(
            x => x.Id == fire.TriggerId &&
                 x.WorkspaceId == fire.WorkspaceId,
            cancellationToken);
        var definition = await db.WorkflowDefinitions.SingleAsync(
            x => x.Id == trigger.WorkflowDefinitionId &&
                 x.WorkspaceId == trigger.WorkspaceId,
            cancellationToken);
        var membership = await db.WorkspaceMemberships
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.WorkspaceId == trigger.WorkspaceId &&
                     x.UserId == trigger.RunAsUserId,
                cancellationToken);

        if (!trigger.Enabled ||
            definition.Status != WorkflowDefinitionStatus.Active ||
            membership is null ||
            membership.Role < definition.MinimumRunRole)
        {
            fire.Fail(now);
            trigger.Disable();
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new(
                WorkflowTriggerSchedulerDisposition.Failed,
                trigger.Id,
                fire.Id,
                Reason: "trigger_not_runnable");
        }

        var run = new WorkflowRun(
            Guid.NewGuid(),
            trigger.WorkspaceId,
            trigger.WorkflowDefinitionId,
            trigger.WorkflowVersionId,
            trigger.CreatedByUserId,
            trigger.RunAsUserId,
            BuildRunIdempotencyKey(fire.FireKey),
            now);
        db.WorkflowRuns.Add(run);
        fire.MarkRunCreated(run.Id, now);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new(
            WorkflowTriggerSchedulerDisposition.RunCreated,
            trigger.Id,
            fire.Id,
            run.Id);
    }

    private void AddDisabledAudits(
        WorkflowTrigger trigger,
        WorkflowTriggerFire fire,
        DateTimeOffset now,
        string reason,
        bool skipped)
    {
        db.WorkflowAuditEvents.Add(
            NewAudit(
                trigger,
                skipped
                    ? WorkflowAuditEventType.TriggerSkipped
                    : WorkflowAuditEventType.TriggerFireFailed,
                now,
                detailJson: JsonSerializer.Serialize(new
                {
                    fireId = fire.Id,
                    reason
                })));

        db.WorkflowAuditEvents.Add(
            NewAudit(
                trigger,
                WorkflowAuditEventType.TriggerDisabled,
                now,
                detailJson: JsonSerializer.Serialize(new
                {
                    reason
                })));
    }

    private static WorkflowAuditEvent NewAudit(
        WorkflowTrigger trigger,
        WorkflowAuditEventType eventType,
        DateTimeOffset now,
        string? detailJson = null) =>
        new(
            Guid.NewGuid(),
            trigger.WorkspaceId,
            eventType,
            now,
            workflowDefinitionId: trigger.WorkflowDefinitionId,
            workflowTriggerId: trigger.Id,
            actorUserId: null,
            detailJson: detailJson);

    private static string BuildFireKey(
        Guid triggerId,
        DateTimeOffset scheduledForUtc) =>
        $"wf-fire:{triggerId:N}:{scheduledForUtc.ToUnixTimeSeconds()}";

    private static string BuildRunIdempotencyKey(
        string fireKey) =>
        $"schedule:{fireKey}";
}
