using System.Text.Json;
using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Workflows;
using ICEHOTT.Domain.Workspaces;

namespace ICEHOTT.Application.Workflows;

public sealed class WorkflowTriggerService(
    IWorkspaceRepository workspaces,
    IWorkflowRepository workflows,
    IWorkflowAuditRepository audit,
    IWorkflowScheduleCalculator schedules,
    WorkflowSchedulerPolicy policy,
    TimeProvider clock)
{
    public async Task<WorkflowTriggerResult<WorkflowTriggerView>> CreateAsync(
        Guid userId,
        Guid workspaceId,
        Guid workflowId,
        Guid workflowVersionId,
        string scheduleExpression,
        string timeZoneId,
        CancellationToken cancellationToken = default)
    {
        var membership = await workspaces.FindMembershipAsync(
            userId,
            workspaceId,
            cancellationToken);

        if (membership is null)
            return new(null, "workspace_not_found");

        if (membership.Role < WorkspaceRole.Admin)
            return new(null, "forbidden");

        var definition = await workflows.FindDefinitionAsync(
            workspaceId,
            workflowId,
            cancellationToken);

        if (definition is null)
            return new(null, "workflow_not_found");

        if (definition.Status != WorkflowDefinitionStatus.Active)
            return new(null, "workflow_not_active");

        var version = await workflows.FindVersionAsync(
            workspaceId,
            workflowId,
            workflowVersionId,
            cancellationToken);

        if (version is null)
            return new(null, "workflow_version_not_found");

        if (version.Status != WorkflowVersionStatus.Active)
            return new(null, "workflow_version_not_active");

        var now = clock.GetUtcNow();
        var validation = schedules.Validate(
            scheduleExpression,
            timeZoneId,
            now,
            policy.MinimumInterval);

        if (!validation.IsValid || validation.NextRunAtUtc is null)
            return new(null, validation.ErrorCode ?? "invalid_schedule");

        var trigger = new WorkflowTrigger(
            Guid.NewGuid(),
            workspaceId,
            workflowId,
            workflowVersionId,
            scheduleExpression,
            timeZoneId,
            userId,
            userId,
            validation.NextRunAtUtc.Value,
            now);

        await workflows.AddTriggerAsync(
            trigger,
            cancellationToken);

        await audit.AddAsync(
            new WorkflowAuditEvent(
                Guid.NewGuid(),
                workspaceId,
                WorkflowAuditEventType.TriggerCreated,
                now,
                workflowDefinitionId: workflowId,
                workflowTriggerId: trigger.Id,
                actorUserId: userId,
                detailJson: JsonSerializer.Serialize(new
                {
                    triggerId = trigger.Id,
                    versionId = workflowVersionId
                })),
            cancellationToken);

        var outcome = await workflows.SaveTriggerAdministrationAsync(
            workspaceId,
            userId,
            userId,
            definition.MinimumRunRole,
            enforceActiveQuota: false,
            policy.MaxActiveTriggersPerWorkspace,
            cancellationToken);

        return MapAdministrationOutcome(
            outcome,
            trigger);
    }

    public async Task<WorkflowTriggerResult<IReadOnlyList<WorkflowTriggerView>>> ListAsync(
        Guid userId,
        Guid workspaceId,
        Guid workflowId,
        CancellationToken cancellationToken = default)
    {
        if (await workspaces.FindMembershipAsync(
                userId,
                workspaceId,
                cancellationToken) is null)
            return new(null, "workspace_not_found");

        if (await workflows.FindDefinitionAsync(
                workspaceId,
                workflowId,
                cancellationToken) is null)
            return new(null, "workflow_not_found");

        var triggers = await workflows.ListTriggersAsync(
            workspaceId,
            workflowId,
            cancellationToken);

        return new(
            triggers.Select(Map).ToArray(),
            null);
    }

    public Task<WorkflowTriggerResult<WorkflowTriggerView>> EnableAsync(
        Guid userId,
        Guid workspaceId,
        Guid workflowId,
        Guid triggerId,
        CancellationToken cancellationToken = default) =>
        SetEnabledAsync(
            userId,
            workspaceId,
            workflowId,
            triggerId,
            enabled: true,
            cancellationToken);

    public Task<WorkflowTriggerResult<WorkflowTriggerView>> DisableAsync(
        Guid userId,
        Guid workspaceId,
        Guid workflowId,
        Guid triggerId,
        CancellationToken cancellationToken = default) =>
        SetEnabledAsync(
            userId,
            workspaceId,
            workflowId,
            triggerId,
            enabled: false,
            cancellationToken);

    private async Task<WorkflowTriggerResult<WorkflowTriggerView>> SetEnabledAsync(
        Guid userId,
        Guid workspaceId,
        Guid workflowId,
        Guid triggerId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        var membership = await workspaces.FindMembershipAsync(
            userId,
            workspaceId,
            cancellationToken);

        if (membership is null)
            return new(null, "workspace_not_found");

        if (membership.Role < WorkspaceRole.Admin)
            return new(null, "forbidden");

        var definition = await workflows.FindDefinitionAsync(
            workspaceId,
            workflowId,
            cancellationToken);

        if (definition is null)
            return new(null, "workflow_not_found");

        var trigger = await workflows.FindTriggerAsync(
            workspaceId,
            triggerId,
            cancellationToken);

        if (trigger is null ||
            trigger.WorkflowDefinitionId != workflowId)
            return new(null, "trigger_not_found");

        if (trigger.Enabled == enabled)
            return new(Map(trigger), null);

        var now = clock.GetUtcNow();

        if (enabled)
        {
            if (definition.Status != WorkflowDefinitionStatus.Active)
                return new(null, "workflow_not_active");

            var version = await workflows.FindVersionAsync(
                workspaceId,
                workflowId,
                trigger.WorkflowVersionId,
                cancellationToken);

            if (version is null)
                return new(null, "workflow_version_not_found");

            if (version.Status == WorkflowVersionStatus.Draft)
                return new(null, "workflow_version_not_executable");

            var validation = schedules.Validate(
                trigger.ScheduleExpression,
                trigger.TimeZoneId,
                now,
                policy.MinimumInterval);

            if (!validation.IsValid ||
                validation.NextRunAtUtc is null)
                return new(
                    null,
                    validation.ErrorCode ?? "invalid_schedule");

            trigger.Enable(validation.NextRunAtUtc.Value);
        }
        else
        {
            trigger.Disable();
        }

        await audit.AddAsync(
            new WorkflowAuditEvent(
                Guid.NewGuid(),
                workspaceId,
                enabled
                    ? WorkflowAuditEventType.TriggerEnabled
                    : WorkflowAuditEventType.TriggerDisabled,
                now,
                workflowDefinitionId: workflowId,
                workflowTriggerId: trigger.Id,
                actorUserId: userId),
            cancellationToken);

        var outcome = await workflows.SaveTriggerAdministrationAsync(
            workspaceId,
            userId,
            enabled ? trigger.RunAsUserId : null,
            enabled ? definition.MinimumRunRole : null,
            enforceActiveQuota: enabled,
            policy.MaxActiveTriggersPerWorkspace,
            cancellationToken);

        return MapAdministrationOutcome(
            outcome,
            trigger);
    }

    private static WorkflowTriggerResult<WorkflowTriggerView>
        MapAdministrationOutcome(
            WorkflowTriggerAdministrationPersistenceOutcome outcome,
            WorkflowTrigger trigger) =>
        outcome switch
        {
            WorkflowTriggerAdministrationPersistenceOutcome.Saved =>
                new(Map(trigger), null),

            WorkflowTriggerAdministrationPersistenceOutcome.ActorAuthorizationConflict =>
                new(null, "forbidden"),

            WorkflowTriggerAdministrationPersistenceOutcome.RunAsAuthorizationConflict =>
                new(null, "run_as_not_authorized"),

            WorkflowTriggerAdministrationPersistenceOutcome.ActiveTriggerQuotaExceeded =>
                new(null, "trigger_quota_exceeded"),

            WorkflowTriggerAdministrationPersistenceOutcome.ConcurrencyConflict =>
                new(null, "invalid_state"),

            _ => new(null, "trigger_save_failed")
        };

    private static WorkflowTriggerView Map(
        WorkflowTrigger trigger) =>
        new(
            trigger.Id,
            trigger.WorkflowDefinitionId,
            trigger.WorkflowVersionId,
            trigger.Type,
            trigger.ScheduleExpression,
            trigger.TimeZoneId,
            trigger.RunAsUserId,
            trigger.Enabled,
            trigger.NextRunAtUtc,
            trigger.LastRunAtUtc,
            trigger.CreatedByUserId,
            trigger.CreatedAtUtc);
}
