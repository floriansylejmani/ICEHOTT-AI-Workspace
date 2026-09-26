using System.Text.Json;
using ICEHOTT.Application.Abstractions;
using ICEHOTT.Application.Security;
using ICEHOTT.Domain.Workflows;

namespace ICEHOTT.Application.Workflows;

public sealed class WorkflowCheckpointService(
    IWorkspaceRepository workspaces,
    IWorkflowRepository workflows,
    IWorkflowAuditRepository audit,
    TimeProvider clock)
{
    public const int MaxReasonLength = 500;

    public async Task<WorkflowCheckpointResult<IReadOnlyList<WorkflowCheckpointView>>> ListAsync(
        Guid userId,
        Guid workspaceId,
        Guid runId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (await workspaces.FindMembershipAsync(
                userId,
                workspaceId,
                cancellationToken) is null)
            return new(null, "workspace_not_found");

        if (await workflows.FindRunAsync(
                workspaceId,
                runId,
                cancellationToken) is null)
            return new(null, "run_not_found");

        var checkpoints = await workflows.ListCheckpointsAsync(
            workspaceId,
            runId,
            Math.Clamp(limit, 1, 200),
            cancellationToken);

        return new(
            checkpoints.Select(Map).ToArray(),
            null);
    }

    public async Task<WorkflowCheckpointResult<WorkflowCheckpointView>> GetAsync(
        Guid userId,
        Guid workspaceId,
        Guid runId,
        Guid checkpointId,
        CancellationToken cancellationToken = default)
    {
        if (await workspaces.FindMembershipAsync(
                userId,
                workspaceId,
                cancellationToken) is null)
            return new(null, "workspace_not_found");

        if (await workflows.FindRunAsync(
                workspaceId,
                runId,
                cancellationToken) is null)
            return new(null, "run_not_found");

        var checkpoint = await workflows.FindCheckpointAsync(
            workspaceId,
            checkpointId,
            cancellationToken);

        if (checkpoint is null || checkpoint.WorkflowRunId != runId)
            return new(null, "checkpoint_not_found");

        return new(Map(checkpoint), null);
    }

    public Task<WorkflowCheckpointResult<WorkflowCheckpointView>> ApproveAsync(
        Guid userId,
        Guid workspaceId,
        Guid runId,
        Guid checkpointId,
        string? reason,
        CancellationToken cancellationToken = default) =>
        DecideAsync(
            userId,
            workspaceId,
            runId,
            checkpointId,
            approve: true,
            reason,
            cancellationToken);

    public Task<WorkflowCheckpointResult<WorkflowCheckpointView>> RejectAsync(
        Guid userId,
        Guid workspaceId,
        Guid runId,
        Guid checkpointId,
        string? reason,
        CancellationToken cancellationToken = default) =>
        DecideAsync(
            userId,
            workspaceId,
            runId,
            checkpointId,
            approve: false,
            reason,
            cancellationToken);

    private async Task<WorkflowCheckpointResult<WorkflowCheckpointView>> DecideAsync(
        Guid userId,
        Guid workspaceId,
        Guid runId,
        Guid checkpointId,
        bool approve,
        string? reason,
        CancellationToken cancellationToken)
    {
        var membership = await workspaces.FindMembershipAsync(
            userId,
            workspaceId,
            cancellationToken);

        if (membership is null)
            return new(null, "workspace_not_found");

        if (await workflows.FindRunAsync(
                workspaceId,
                runId,
                cancellationToken) is null)
            return new(null, "run_not_found");

        var checkpoint = await workflows.FindCheckpointAsync(
            workspaceId,
            checkpointId,
            cancellationToken);

        if (checkpoint is null || checkpoint.WorkflowRunId != runId)
            return new(null, "checkpoint_not_found");

        if (membership.Role < checkpoint.MinimumApproverRole)
            return new(null, "forbidden");

        if (checkpoint.RequiresDifferentApprover &&
            checkpoint.RequestedByUserId == userId)
            return new(null, "self_decision_forbidden");

        if (checkpoint.Status != WorkflowCheckpointStatus.Pending)
            return new(Map(checkpoint), "invalid_state");

        var normalizedReason = NormalizeReason(reason);
        if (normalizedReason is { Length: > MaxReasonLength })
            return new(null, "reason_too_long");

        var now = clock.GetUtcNow();

        if (approve)
            checkpoint.Approve(userId, normalizedReason, now);
        else
            checkpoint.Reject(userId, normalizedReason, now);

        await audit.AddAsync(
            new WorkflowAuditEvent(
                Guid.NewGuid(),
                workspaceId,
                approve
                    ? WorkflowAuditEventType.CheckpointApproved
                    : WorkflowAuditEventType.CheckpointRejected,
                now,
                workflowRunId: checkpoint.WorkflowRunId,
                workflowStepRunId: checkpoint.StepRunId,
                actorUserId: userId,
                detailJson: JsonSerializer.Serialize(new
                {
                    checkpointId = checkpoint.Id,
                    decision = approve ? "approved" : "rejected"
                })),
            cancellationToken);

        var outcome = await workflows.SaveCheckpointDecisionAsync(
            workspaceId,
            userId,
            checkpoint.MinimumApproverRole,
            cancellationToken);

        return outcome switch
        {
            WorkflowCheckpointDecisionPersistenceOutcome.Saved =>
                new(Map(checkpoint), null),

            WorkflowCheckpointDecisionPersistenceOutcome.ConcurrencyConflict =>
                new(null, "invalid_state"),

            WorkflowCheckpointDecisionPersistenceOutcome.ApproverAuthorizationConflict =>
                new(null, "forbidden"),

            _ => new(null, "checkpoint_decision_failed")
        };
    }

    private static string? NormalizeReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return null;

        return SecretClassifier.Redact(reason.Trim()).Trim();
    }

    private static WorkflowCheckpointView Map(WorkflowCheckpoint checkpoint) =>
        new(
            checkpoint.Id,
            checkpoint.WorkflowRunId,
            checkpoint.StepRunId,
            checkpoint.RequestedByUserId,
            checkpoint.MinimumApproverRole,
            checkpoint.RequiresDifferentApprover,
            checkpoint.Status,
            checkpoint.DecidedByUserId,
            checkpoint.CreatedAtUtc,
            checkpoint.DecidedAtUtc,
            checkpoint.Reason);
}
