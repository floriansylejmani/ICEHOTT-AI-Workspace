using System.Text.Json;
using ICEHOTT.Application.Abstractions;
using ICEHOTT.Application.Tools;
using ICEHOTT.Domain.Tools;
using ICEHOTT.Domain.Workflows;
using ICEHOTT.Domain.Workspaces;

namespace ICEHOTT.Application.Workflows;

public enum WorkflowRunProcessDisposition
{
    Waiting = 1,
    Completed = 2,
    LeaseLost = 3
}

public sealed record WorkflowRunProcessResult(
    WorkflowRunProcessDisposition Disposition,
    WorkflowRunStatus Status,
    string? ErrorCode = null);

public sealed class WorkflowRunProcessor(
    IWorkflowRepository workflows,
    IWorkflowRunQueue queue,
    IWorkflowAuditRepository audit,
    IWorkspaceRepository workspaces,
    IWorkflowToolInvoker toolInvoker,
    TimeProvider clock)
{
    private const int MaxTransitionsPerLease = 100;

    public async Task<WorkflowRunProcessResult> ProcessAsync(
        WorkflowRunLease lease,
        CancellationToken cancellationToken = default)
    {
        var run = await workflows.FindRunAsync(
            lease.WorkspaceId,
            lease.RunId,
            cancellationToken);

        if (run is null ||
            run.Status != WorkflowRunStatus.Running ||
            run.LeaseOwnerId != lease.WorkerId ||
            run.LeaseGeneration != lease.LeaseGeneration)
        {
            return new(
                WorkflowRunProcessDisposition.LeaseLost,
                run?.Status ?? WorkflowRunStatus.Failed,
                "workflow_lease_lost");
        }

        var definition = await workflows.FindDefinitionAsync(
            lease.WorkspaceId,
            lease.WorkflowDefinitionId,
            cancellationToken);
        var version = await workflows.FindVersionAsync(
            lease.WorkspaceId,
            lease.WorkflowDefinitionId,
            lease.WorkflowVersionId,
            cancellationToken);

        if (definition is null || version is null)
            return await FailRunAsync(
                run,
                lease,
                "workflow_definition_missing",
                "Workflow definition or pinned version no longer exists.",
                cancellationToken);

        WorkflowPlan runtime;
        try
        {
            runtime = WorkflowPlanParser.Parse(version.DefinitionJson);
        }
        catch (WorkflowPlanException)
        {
            return await FailRunAsync(
                run,
                lease,
                "workflow_definition_invalid",
                "Pinned workflow definition failed runtime validation.",
                cancellationToken);
        }

        for (var transition = 0;
             transition < MaxTransitionsPerLease;
             transition++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (run.CancellationRequestedAtUtc is not null)
                return await HandleCancellationAsync(
                    run,
                    lease,
                    cancellationToken);

            var membership = await workspaces.FindMembershipAsync(
                run.RunAsUserId,
                run.WorkspaceId,
                cancellationToken);

            if (membership is null ||
                membership.Role < definition.MinimumRunRole)
            {
                return await FailRunAsync(
                    run,
                    lease,
                    "workflow_run_as_not_authorized",
                    "Workflow run-as user is no longer authorized.",
                    cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(run.CurrentStepKey))
            {
                var currentDefinition = runtime.Steps.SingleOrDefault(
                    x => string.Equals(
                        x.Key,
                        run.CurrentStepKey,
                        StringComparison.Ordinal));

                if (currentDefinition is null)
                    return await FailRunAsync(
                        run,
                        lease,
                        "workflow_current_step_missing",
                        "Current workflow step is not present in the pinned version.",
                        cancellationToken);

                var currentStep = await workflows.FindLatestStepRunAsync(
                    run.WorkspaceId,
                    run.Id,
                    currentDefinition.Key,
                    cancellationToken);

                if (currentStep is null)
                    return await FailRunAsync(
                        run,
                        lease,
                        "workflow_step_state_missing",
                        "Persisted workflow step state is missing.",
                        cancellationToken);

                var existing = await HandleExistingStepAsync(
                    run,
                    currentStep,
                    currentDefinition,
                    lease,
                    cancellationToken);

                if (existing is not null)
                    return existing;

                continue;
            }

            var stepRuns = await workflows.ListStepRunsAsync(
                run.WorkspaceId,
                run.Id,
                cancellationToken);

            var latestByKey = stepRuns
                .GroupBy(x => x.StepKey, StringComparer.Ordinal)
                .ToDictionary(
                    x => x.Key,
                    x => x.OrderByDescending(y => y.Attempt).First(),
                    StringComparer.Ordinal);

            var next = runtime.Steps.FirstOrDefault(step =>
                !latestByKey.ContainsKey(step.Key) &&
                step.DependsOn.All(dependency =>
                    latestByKey.TryGetValue(dependency, out var dependencyRun) &&
                    dependencyRun.Status is
                        WorkflowStepRunStatus.Succeeded or
                        WorkflowStepRunStatus.Skipped));

            if (next is null)
            {
                if (runtime.Steps.All(step =>
                        latestByKey.TryGetValue(step.Key, out var stepRun) &&
                        stepRun.Status is
                            WorkflowStepRunStatus.Succeeded or
                            WorkflowStepRunStatus.Skipped))
                {
                    return await SucceedRunAsync(
                        run,
                        lease,
                        cancellationToken);
                }

                return await FailRunAsync(
                    run,
                    lease,
                    "workflow_graph_stalled",
                    "Workflow graph has no runnable step.",
                    cancellationToken);
            }

            var started = await StartNewAttemptAsync(
                run,
                next,
                attempt: 1,
                lease,
                stepRuns.Count == 0,
                cancellationToken);

            if (started.Result is not null)
                return started.Result;

            var result = await HandleRunningStepAsync(
                run,
                started.Step!,
                next,
                lease,
                cancellationToken);

            if (result is not null)
                return result;
        }

        return await FailRunAsync(
            run,
            lease,
            "workflow_transition_limit_exceeded",
            "Workflow exceeded the bounded transition limit for one lease.",
            cancellationToken);
    }

    private static Guid GetLeaseOwner(WorkflowRun run) =>
        run.LeaseOwnerId ?? Guid.Empty;
    private async Task<WorkflowRunProcessResult?> HandleExistingStepAsync(
        WorkflowRun run,
        WorkflowStepRun stepRun,
        WorkflowPlanStep definition,
        WorkflowRunLease lease,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        switch (stepRun.Status)
        {
            case WorkflowStepRunStatus.Succeeded:
            case WorkflowStepRunStatus.Skipped:
                run.SetCurrentStep(null);
                return await SaveAndContinueAsync(
                    run,
                    lease,
                    cancellationToken);

            case WorkflowStepRunStatus.WaitingForDelay:
                if (stepRun.NextAttemptAtUtc is { } resumeAt &&
                    resumeAt > now)
                {
                    run.Wait(WorkflowWaitReason.Delay, resumeAt);
                    return await SaveWaitingAsync(
                        run,
                        lease,
                        cancellationToken);
                }

                stepRun.Succeed(null, now);
                run.SetCurrentStep(null);
                await AddAuditAsync(
                    run,
                    stepRun,
                    WorkflowAuditEventType.StepSucceeded,
                    cancellationToken);
                return await SaveAndContinueAsync(
                    run,
                    lease,
                    cancellationToken);

            case WorkflowStepRunStatus.WaitingForRetry:
                if (stepRun.NextAttemptAtUtc is { } retryAt &&
                    retryAt > now)
                {
                    run.Wait(WorkflowWaitReason.RetryBackoff, retryAt);
                    return await SaveWaitingAsync(
                        run,
                        lease,
                        cancellationToken);
                }

                stepRun.CloseForRetry(now);
                var restarted = await StartNewAttemptAsync(
                    run,
                    definition,
                    checked(stepRun.Attempt + 1),
                    lease,
                    isFirstRunStep: false,
                    cancellationToken);

                if (restarted.Result is not null)
                    return restarted.Result;

                return await HandleRunningStepAsync(
                    run,
                    restarted.Step!,
                    definition,
                    lease,
                    cancellationToken);

            case WorkflowStepRunStatus.WaitingForTool:
                return await HandleToolStateAsync(
                    run,
                    stepRun,
                    definition,
                    lease,
                    cancellationToken);

            case WorkflowStepRunStatus.WaitingForCheckpoint:
                return await HandleCheckpointStateAsync(
                    run,
                    stepRun,
                    lease,
                    cancellationToken);

            case WorkflowStepRunStatus.Running:
                return await HandleRunningStepAsync(
                    run,
                    stepRun,
                    definition,
                    lease,
                    cancellationToken);

            case WorkflowStepRunStatus.Failed:
                return await FailRunAsync(
                    run,
                    lease,
                    stepRun.ErrorCode ?? "workflow_step_failed",
                    stepRun.ErrorMessage ?? "Workflow step failed.",
                    cancellationToken);

            case WorkflowStepRunStatus.Cancelled:
                run.Cancel(now);
                await AddAuditAsync(
                    run,
                    stepRun,
                    WorkflowAuditEventType.RunCancelled,
                    cancellationToken);
                return await SaveTerminalAsync(
                    run,
                    lease,
                    cancellationToken);

            case WorkflowStepRunStatus.OutcomeUnknown:
                run.MarkOutcomeUnknown(
                    stepRun.ErrorMessage,
                    now);
                await AddAuditAsync(
                    run,
                    stepRun,
                    WorkflowAuditEventType.OutcomeUnknown,
                    cancellationToken);
                return await SaveTerminalAsync(
                    run,
                    lease,
                    cancellationToken);

            default:
                return await FailRunAsync(
                    run,
                    lease,
                    "workflow_step_invalid_state",
                    "Workflow step is in an invalid runtime state.",
                    cancellationToken);
        }
    }

    private async Task<(WorkflowStepRun? Step, WorkflowRunProcessResult? Result)>
        StartNewAttemptAsync(
            WorkflowRun run,
            WorkflowPlanStep definition,
            int attempt,
            WorkflowRunLease lease,
            bool isFirstRunStep,
            CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var stepRun = new WorkflowStepRun(
            Guid.NewGuid(),
            run.Id,
            run.WorkspaceId,
            definition.Key,
            attempt,
            definition.Type,
            BuildInputJson(definition));

        stepRun.MarkReady();
        stepRun.Start(now);
        run.SetCurrentStep(definition.Key);

        await workflows.AddStepRunAsync(
            stepRun,
            cancellationToken);

        if (isFirstRunStep)
        {
            await audit.AddAsync(
                NewAudit(
                    run,
                    null,
                    WorkflowAuditEventType.RunStarted,
                    run.RunAsUserId,
                    now),
                cancellationToken);
        }

        await AddAuditAsync(
            run,
            stepRun,
            WorkflowAuditEventType.StepStarted,
            cancellationToken);

        var outcome = await queue.SaveFencedAsync(
            run.Id,
            lease.WorkerId,
            lease.LeaseGeneration,
            cancellationToken);

        return outcome == WorkflowRunPersistenceOutcome.Saved
            ? (stepRun, null)
            : (
                null,
                new WorkflowRunProcessResult(
                    WorkflowRunProcessDisposition.LeaseLost,
                    run.Status,
                    "workflow_lease_lost"));
    }
    private async Task<WorkflowRunProcessResult?> HandleRunningStepAsync(
        WorkflowRun run,
        WorkflowStepRun stepRun,
        WorkflowPlanStep definition,
        WorkflowRunLease lease,
        CancellationToken cancellationToken)
    {
        return definition.Type switch
        {
            WorkflowStepType.Tool => await HandleToolStateAsync(
                run,
                stepRun,
                definition,
                lease,
                cancellationToken),

            WorkflowStepType.Delay => await HandleDelayStartAsync(
                run,
                stepRun,
                definition,
                lease,
                cancellationToken),

            WorkflowStepType.Checkpoint => await HandleCheckpointStartAsync(
                run,
                stepRun,
                definition,
                lease,
                cancellationToken),

            WorkflowStepType.Artifact => await FailStepAndRunAsync(
                run,
                stepRun,
                lease,
                "workflow_artifact_handler_unavailable",
                "Artifact execution is introduced in Phase 5D.",
                cancellationToken),

            WorkflowStepType.Condition => await FailStepAndRunAsync(
                run,
                stepRun,
                lease,
                "workflow_condition_handler_unavailable",
                "Condition execution is not enabled in this runner packet.",
                cancellationToken),

            _ => await FailStepAndRunAsync(
                run,
                stepRun,
                lease,
                "workflow_step_type_unsupported",
                "Workflow step type is unsupported.",
                cancellationToken)
        };
    }

    private async Task<WorkflowRunProcessResult?> HandleDelayStartAsync(
        WorkflowRun run,
        WorkflowStepRun stepRun,
        WorkflowPlanStep definition,
        WorkflowRunLease lease,
        CancellationToken cancellationToken)
    {
        if (definition.DelaySeconds is not { } delaySeconds)
            return await FailStepAndRunAsync(
                run,
                stepRun,
                lease,
                "workflow_delay_invalid",
                "Delay step has no valid duration.",
                cancellationToken);

        var startedAt = stepRun.StartedAtUtc ?? clock.GetUtcNow();
        var resumeAt = startedAt.AddSeconds(delaySeconds);
        var now = clock.GetUtcNow();

        if (resumeAt <= now)
        {
            stepRun.Succeed(null, now);
            run.SetCurrentStep(null);
            await AddAuditAsync(
                run,
                stepRun,
                WorkflowAuditEventType.StepSucceeded,
                cancellationToken);
            return await SaveAndContinueAsync(
                run,
                lease,
                cancellationToken);
        }

        stepRun.WaitForDelay(resumeAt);
        run.Wait(WorkflowWaitReason.Delay, resumeAt);

        return await SaveWaitingAsync(
            run,
            lease,
            cancellationToken);
    }

    private async Task<WorkflowRunProcessResult?> HandleCheckpointStartAsync(
        WorkflowRun run,
        WorkflowStepRun stepRun,
        WorkflowPlanStep definition,
        WorkflowRunLease lease,
        CancellationToken cancellationToken)
    {
        var checkpoint = await workflows.FindCheckpointByStepRunAsync(
            run.WorkspaceId,
            stepRun.Id,
            cancellationToken);

        if (checkpoint is null)
        {
            checkpoint = new WorkflowCheckpoint(
                Guid.NewGuid(),
                run.WorkspaceId,
                run.Id,
                stepRun.Id,
                run.RunAsUserId,
                definition.MinimumApproverRole ?? WorkspaceRole.Admin,
                definition.RequiresDifferentApprover,
                clock.GetUtcNow());

            await workflows.AddCheckpointAsync(
                checkpoint,
                cancellationToken);

            await AddAuditAsync(
                run,
                stepRun,
                WorkflowAuditEventType.CheckpointRequested,
                cancellationToken);
        }

        stepRun.WaitForCheckpoint();
        run.Wait(WorkflowWaitReason.Checkpoint);

        return await SaveWaitingAsync(
            run,
            lease,
            cancellationToken);
    }

    private async Task<WorkflowRunProcessResult?> HandleCheckpointStateAsync(
        WorkflowRun run,
        WorkflowStepRun stepRun,
        WorkflowRunLease lease,
        CancellationToken cancellationToken)
    {
        var checkpoint = await workflows.FindCheckpointByStepRunAsync(
            run.WorkspaceId,
            stepRun.Id,
            cancellationToken);

        if (checkpoint is null)
            return await FailStepAndRunAsync(
                run,
                stepRun,
                lease,
                "workflow_checkpoint_missing",
                "Workflow checkpoint state is missing.",
                cancellationToken);

        var now = clock.GetUtcNow();
        switch (checkpoint.Status)
        {
            case WorkflowCheckpointStatus.Pending:
                run.Wait(WorkflowWaitReason.Checkpoint);
                return await SaveWaitingAsync(
                    run,
                    lease,
                    cancellationToken);

            case WorkflowCheckpointStatus.Approved:
                stepRun.Succeed(
                    JsonSerializer.Serialize(
                        new
                        {
                            decision = "approved",
                            checkpointId = checkpoint.Id
                        }),
                    now);
                run.SetCurrentStep(null);
                await AddAuditAsync(
                    run,
                    stepRun,
                    WorkflowAuditEventType.StepSucceeded,
                    cancellationToken);
                return await SaveAndContinueAsync(
                    run,
                    lease,
                    cancellationToken);

            case WorkflowCheckpointStatus.Rejected:
            case WorkflowCheckpointStatus.Expired:
                return await FailStepAndRunAsync(
                    run,
                    stepRun,
                    lease,
                    checkpoint.Status == WorkflowCheckpointStatus.Rejected
                        ? "workflow_checkpoint_rejected"
                        : "workflow_checkpoint_expired",
                    "Workflow checkpoint did not approve execution.",
                    cancellationToken);

            default:
                return await FailStepAndRunAsync(
                    run,
                    stepRun,
                    lease,
                    "workflow_checkpoint_invalid_state",
                    "Workflow checkpoint is in an invalid state.",
                    cancellationToken);
        }
    }
    private async Task<WorkflowRunProcessResult?> HandleToolStateAsync(
        WorkflowRun run,
        WorkflowStepRun stepRun,
        WorkflowPlanStep definition,
        WorkflowRunLease lease,
        CancellationToken cancellationToken)
    {
        ToolOperationResult<ToolExecutionView> operation;

        if (stepRun.ToolExecutionId is { } existingExecutionId)
        {
            operation = await toolInvoker.GetAsync(
                run.RunAsUserId,
                run.WorkspaceId,
                existingExecutionId,
                cancellationToken);
        }
        else
        {
            if (definition.ToolName is null ||
                string.IsNullOrWhiteSpace(definition.ArgumentsJson))
            {
                return await FailStepAndRunAsync(
                    run,
                    stepRun,
                    lease,
                    "workflow_tool_definition_invalid",
                    "Tool step is missing a tool name or arguments.",
                    cancellationToken);
            }

            using var argumentsDocument = JsonDocument.Parse(
                definition.ArgumentsJson);
            operation = await toolInvoker.RequestAsync(
                run.RunAsUserId,
                run.WorkspaceId,
                definition.ToolName,
                argumentsDocument.RootElement.Clone(),
                BuildToolIdempotencyKey(run.Id, stepRun.Id),
                cancellationToken);
        }

        if (operation.Value is null)
        {
            if (CanRetryAdmission(operation.ErrorCode) &&
                stepRun.Attempt < definition.Retry.MaxAttempts)
            {
                return await ScheduleRetryAsync(
                    run,
                    stepRun,
                    definition,
                    lease,
                    operation.ErrorCode ?? "workflow_tool_admission_failed",
                    "Tool admission can be retried safely.",
                    operation.RetryAfterSeconds,
                    cancellationToken);
            }

            return await FailStepAndRunAsync(
                run,
                stepRun,
                lease,
                operation.ErrorCode ?? "workflow_tool_request_failed",
                "Tool request failed before a durable execution was available.",
                cancellationToken);
        }

        var execution = operation.Value;

        if (stepRun.ToolExecutionId is null)
        {
            stepRun.AttachToolExecution(execution.Id);
        }

        switch (execution.Status)
        {
            case ToolExecutionStatus.PendingApproval:
            case ToolExecutionStatus.Ready:
            case ToolExecutionStatus.Running:
                stepRun.WaitForTool(execution.Id);
                run.Wait(WorkflowWaitReason.ToolExecution);
                return await SaveWaitingAsync(
                    run,
                    lease,
                    cancellationToken);

            case ToolExecutionStatus.Succeeded:
                stepRun.Succeed(
                    execution.Result?.GetRawText(),
                    execution.CompletedAtUtc ?? clock.GetUtcNow());
                run.SetCurrentStep(null);
                await AddAuditAsync(
                    run,
                    stepRun,
                    WorkflowAuditEventType.StepSucceeded,
                    cancellationToken);
                return await SaveAndContinueAsync(
                    run,
                    lease,
                    cancellationToken);

            case ToolExecutionStatus.Failed:
            case ToolExecutionStatus.TimedOut:
                if (execution.RiskLevel == ToolRiskLevel.ReadOnly &&
                    stepRun.Attempt < definition.Retry.MaxAttempts)
                {
                    return await ScheduleRetryAsync(
                        run,
                        stepRun,
                        definition,
                        lease,
                        execution.ErrorCode ?? "workflow_tool_failed",
                        execution.ErrorMessage ?? "Read-only tool execution failed.",
                        retryAfterSeconds: null,
                        cancellationToken);
                }

                return await FailStepAndRunAsync(
                    run,
                    stepRun,
                    lease,
                    execution.ErrorCode ?? "workflow_tool_failed",
                    execution.ErrorMessage ?? "Tool execution failed.",
                    cancellationToken);

            case ToolExecutionStatus.OutcomeUnknown:
                stepRun.MarkOutcomeUnknown(
                    execution.ErrorMessage,
                    execution.CompletedAtUtc ?? clock.GetUtcNow());
                run.MarkOutcomeUnknown(
                    execution.ErrorMessage,
                    execution.CompletedAtUtc ?? clock.GetUtcNow());
                await AddAuditAsync(
                    run,
                    stepRun,
                    WorkflowAuditEventType.OutcomeUnknown,
                    cancellationToken);
                return await SaveTerminalAsync(
                    run,
                    lease,
                    cancellationToken);

            case ToolExecutionStatus.Rejected:
                return await FailStepAndRunAsync(
                    run,
                    stepRun,
                    lease,
                    "workflow_tool_rejected",
                    "Tool execution was rejected.",
                    cancellationToken);

            case ToolExecutionStatus.Cancelled:
                return await FailStepAndRunAsync(
                    run,
                    stepRun,
                    lease,
                    "workflow_tool_cancelled",
                    "Tool execution was cancelled.",
                    cancellationToken);

            default:
                return await FailStepAndRunAsync(
                    run,
                    stepRun,
                    lease,
                    "workflow_tool_invalid_state",
                    "Tool execution is in an invalid state.",
                    cancellationToken);
        }
    }
    private async Task<WorkflowRunProcessResult?> ScheduleRetryAsync(
        WorkflowRun run,
        WorkflowStepRun stepRun,
        WorkflowPlanStep definition,
        WorkflowRunLease lease,
        string errorCode,
        string errorMessage,
        int? retryAfterSeconds,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var policyDelay = RetryDelaySeconds(
            definition.Retry,
            stepRun.Attempt);
        var delaySeconds = Math.Max(
            policyDelay,
            Math.Clamp(retryAfterSeconds ?? 0, 0, 3600));
        var retryAt = now.AddSeconds(delaySeconds);

        stepRun.WaitForRetry(
            retryAt,
            errorCode,
            errorMessage);
        run.Wait(
            WorkflowWaitReason.RetryBackoff,
            retryAt);

        await AddAuditAsync(
            run,
            stepRun,
            WorkflowAuditEventType.RetryScheduled,
            cancellationToken);

        return await SaveWaitingAsync(
            run,
            lease,
            cancellationToken);
    }

    private async Task<WorkflowRunProcessResult> HandleCancellationAsync(
        WorkflowRun run,
        WorkflowRunLease lease,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        WorkflowStepRun? currentStep = null;

        if (!string.IsNullOrWhiteSpace(run.CurrentStepKey))
        {
            currentStep = await workflows.FindLatestStepRunAsync(
                run.WorkspaceId,
                run.Id,
                run.CurrentStepKey,
                cancellationToken);
        }

        if (currentStep?.ToolExecutionId is { } executionId)
        {
            var tool = await toolInvoker.GetAsync(
                run.RunAsUserId,
                run.WorkspaceId,
                executionId,
                cancellationToken);

            if (tool.Value?.Status == ToolExecutionStatus.OutcomeUnknown ||
                tool.Value is
                {
                    Status: ToolExecutionStatus.Running,
                    RiskLevel: ToolRiskLevel.SensitiveWrite
                })
            {
                if (currentStep.Status is
                    WorkflowStepRunStatus.Running or
                    WorkflowStepRunStatus.WaitingForTool)
                {
                    currentStep.MarkOutcomeUnknown(
                        "Cancellation intersected an uncertain sensitive tool execution.",
                        now);
                }

                run.MarkOutcomeUnknown(
                    "Cancellation intersected an uncertain sensitive tool execution.",
                    now);
                await AddAuditAsync(
                    run,
                    currentStep,
                    WorkflowAuditEventType.OutcomeUnknown,
                    cancellationToken);
                return await SaveTerminalAsync(
                    run,
                    lease,
                    cancellationToken);
            }

            if (tool.Value?.Status is
                ToolExecutionStatus.PendingApproval or
                ToolExecutionStatus.Ready)
            {
                var actor = run.CancellationRequestedByUserId ??
                            run.RunAsUserId;
                var cancelled = await toolInvoker.CancelAsync(
                    actor,
                    run.WorkspaceId,
                    executionId,
                    cancellationToken);

                if (!cancelled.Succeeded &&
                    actor != run.RunAsUserId)
                {
                    cancelled = await toolInvoker.CancelAsync(
                        run.RunAsUserId,
                        run.WorkspaceId,
                        executionId,
                        cancellationToken);
                }

                if (!cancelled.Succeeded)
                {
                    currentStep.MarkOutcomeUnknown(
                        "Workflow could not prove cancellation of the pending tool execution.",
                        now);
                    run.MarkOutcomeUnknown(
                        "Workflow could not prove cancellation of the pending tool execution.",
                        now);
                    await AddAuditAsync(
                        run,
                        currentStep,
                        WorkflowAuditEventType.OutcomeUnknown,
                        cancellationToken);
                    return await SaveTerminalAsync(
                        run,
                        lease,
                        cancellationToken);
                }
            }
        }

        if (currentStep is not null &&
            currentStep.Status is not (
                WorkflowStepRunStatus.Succeeded or
                WorkflowStepRunStatus.Failed or
                WorkflowStepRunStatus.Cancelled or
                WorkflowStepRunStatus.Skipped or
                WorkflowStepRunStatus.OutcomeUnknown))
        {
            currentStep.Cancel(now);
        }

        run.Cancel(now);
        await AddAuditAsync(
            run,
            currentStep,
            WorkflowAuditEventType.RunCancelled,
            cancellationToken);
        return await SaveTerminalAsync(
            run,
            lease,
            cancellationToken);
    }
    private async Task<WorkflowRunProcessResult> FailStepAndRunAsync(
        WorkflowRun run,
        WorkflowStepRun stepRun,
        WorkflowRunLease lease,
        string errorCode,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        if (stepRun.Status is not (
            WorkflowStepRunStatus.Succeeded or
            WorkflowStepRunStatus.Failed or
            WorkflowStepRunStatus.Cancelled or
            WorkflowStepRunStatus.Skipped or
            WorkflowStepRunStatus.OutcomeUnknown))
        {
            stepRun.Fail(
                errorCode,
                errorMessage,
                now);
        }

        await AddAuditAsync(
            run,
            stepRun,
            WorkflowAuditEventType.StepFailed,
            cancellationToken);

        run.Fail(
            errorCode,
            errorMessage,
            now);

        await audit.AddAsync(
            NewAudit(
                run,
                stepRun,
                WorkflowAuditEventType.RunFailed,
                null,
                now),
            cancellationToken);

        return await SaveTerminalAsync(
            run,
            lease,
            cancellationToken);
    }

    private async Task<WorkflowRunProcessResult> FailRunAsync(
        WorkflowRun run,
        WorkflowRunLease lease,
        string errorCode,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        run.Fail(
            errorCode,
            errorMessage,
            now);

        await audit.AddAsync(
            NewAudit(
                run,
                null,
                WorkflowAuditEventType.RunFailed,
                null,
                now),
            cancellationToken);

        return await SaveTerminalAsync(
            run,
            lease,
            cancellationToken);
    }

    private async Task<WorkflowRunProcessResult> SucceedRunAsync(
        WorkflowRun run,
        WorkflowRunLease lease,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        run.Succeed(now);

        await audit.AddAsync(
            NewAudit(
                run,
                null,
                WorkflowAuditEventType.RunSucceeded,
                run.RunAsUserId,
                now),
            cancellationToken);

        return await SaveTerminalAsync(
            run,
            lease,
            cancellationToken);
    }

    private async Task<WorkflowRunProcessResult?> SaveAndContinueAsync(
        WorkflowRun run,
        WorkflowRunLease lease,
        CancellationToken cancellationToken)
    {
        var outcome = await queue.SaveFencedAsync(
            run.Id,
            GetLeaseOwner(run),
            lease.LeaseGeneration,
            cancellationToken);

        return outcome == WorkflowRunPersistenceOutcome.Saved
            ? null
            : new WorkflowRunProcessResult(
                WorkflowRunProcessDisposition.LeaseLost,
                run.Status,
                "workflow_lease_lost");
    }

    private async Task<WorkflowRunProcessResult> SaveWaitingAsync(
        WorkflowRun run,
        WorkflowRunLease lease,
        CancellationToken cancellationToken)
    {
        var outcome = await queue.SaveFencedAsync(
            run.Id,
            lease.WorkerId,
            lease.LeaseGeneration,
            cancellationToken);

        return outcome == WorkflowRunPersistenceOutcome.Saved
            ? new(
                WorkflowRunProcessDisposition.Waiting,
                run.Status)
            : new(
                WorkflowRunProcessDisposition.LeaseLost,
                run.Status,
                "workflow_lease_lost");
    }

    private async Task<WorkflowRunProcessResult> SaveTerminalAsync(
        WorkflowRun run,
        WorkflowRunLease lease,
        CancellationToken cancellationToken)
    {
        var outcome = await queue.SaveFencedAsync(
            run.Id,
            lease.WorkerId,
            lease.LeaseGeneration,
            cancellationToken);

        return outcome == WorkflowRunPersistenceOutcome.Saved
            ? new(
                WorkflowRunProcessDisposition.Completed,
                run.Status,
                run.ErrorCode)
            : new(
                WorkflowRunProcessDisposition.LeaseLost,
                run.Status,
                "workflow_lease_lost");
    }
    private Task AddAuditAsync(
        WorkflowRun run,
        WorkflowStepRun? stepRun,
        WorkflowAuditEventType eventType,
        CancellationToken cancellationToken) =>
        audit.AddAsync(
            NewAudit(
                run,
                stepRun,
                eventType,
                run.RunAsUserId,
                clock.GetUtcNow()),
            cancellationToken);

    private static WorkflowAuditEvent NewAudit(
        WorkflowRun run,
        WorkflowStepRun? stepRun,
        WorkflowAuditEventType eventType,
        Guid? actorUserId,
        DateTimeOffset now) =>
        new(
            Guid.NewGuid(),
            run.WorkspaceId,
            eventType,
            now,
            workflowDefinitionId: run.WorkflowDefinitionId,
            workflowRunId: run.Id,
            workflowStepRunId: stepRun?.Id,
            actorUserId: actorUserId);

    private static string BuildInputJson(
        WorkflowPlanStep step)
    {
        if (!string.IsNullOrWhiteSpace(step.ArgumentsJson))
            return step.ArgumentsJson;

        if (step.DelaySeconds is { } delaySeconds)
            return JsonSerializer.Serialize(
                new
                {
                    delaySeconds
                });

        return "{}";
    }

    private static string BuildToolIdempotencyKey(
        Guid runId,
        Guid stepRunId) =>
        $"wf-{runId:N}-{stepRunId:N}";

    private static bool CanRetryAdmission(string? errorCode) =>
        errorCode is "tool_quota_exceeded" or "policy_changed";

    private static int RetryDelaySeconds(
        WorkflowRetryPolicy policy,
        int attempt)
    {
        var exponent = Math.Max(0, attempt - 1);
        var calculated =
            policy.InitialDelaySeconds *
            Math.Pow(policy.BackoffMultiplier, exponent);

        return Math.Clamp(
            (int)Math.Ceiling(calculated),
            1,
            policy.MaxDelaySeconds);
    }
}
