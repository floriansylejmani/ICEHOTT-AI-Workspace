using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Workflows;

namespace ICEHOTT.Application.Workflows;

public sealed class WorkflowExperienceService(
    IWorkspaceRepository workspaces,
    IWorkflowRepository workflows)
{
    public async Task<WorkflowExperienceResult<IReadOnlyList<WorkflowDefinitionSummaryView>>> ListDefinitionsAsync(
        Guid userId,
        Guid workspaceId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (await workspaces.FindMembershipAsync(userId, workspaceId, cancellationToken) is null)
            return new(null, "workspace_not_found");

        var items = await workflows.ListDefinitionsAsync(
            workspaceId,
            Math.Clamp(limit, 1, 200),
            cancellationToken);

        return new(items.Select(MapDefinition).ToArray(), null);
    }

    public async Task<WorkflowExperienceResult<WorkflowDefinitionDetailView>> GetDefinitionAsync(
        Guid userId,
        Guid workspaceId,
        Guid workflowId,
        CancellationToken cancellationToken = default)
    {
        if (await workspaces.FindMembershipAsync(userId, workspaceId, cancellationToken) is null)
            return new(null, "workspace_not_found");

        var definition = await workflows.FindDefinitionAsync(
            workspaceId,
            workflowId,
            cancellationToken);

        if (definition is null)
            return new(null, "workflow_not_found");

        var versions = await workflows.ListVersionsAsync(
            workspaceId,
            workflowId,
            cancellationToken);

        return new(
            new WorkflowDefinitionDetailView(
                definition.Id,
                definition.Name,
                definition.Description,
                definition.Status,
                definition.MinimumRunRole,
                definition.CreatedAtUtc,
                definition.UpdatedAtUtc,
                versions.Select(MapVersion).ToArray()),
            null);
    }

    public async Task<WorkflowExperienceResult<IReadOnlyList<WorkflowRunSummaryView>>> ListRunsAsync(
        Guid userId,
        Guid workspaceId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (await workspaces.FindMembershipAsync(userId, workspaceId, cancellationToken) is null)
            return new(null, "workspace_not_found");

        var items = await workflows.ListRunsAsync(
            workspaceId,
            Math.Clamp(limit, 1, 200),
            cancellationToken);

        return new(items.Select(MapRun).ToArray(), null);
    }

    public async Task<WorkflowExperienceResult<WorkflowRunDetailView>> GetRunAsync(
        Guid userId,
        Guid workspaceId,
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        if (await workspaces.FindMembershipAsync(userId, workspaceId, cancellationToken) is null)
            return new(null, "workspace_not_found");

        var run = await workflows.FindRunAsync(
            workspaceId,
            runId,
            cancellationToken);

        if (run is null)
            return new(null, "run_not_found");

        var steps = await workflows.ListStepRunsAsync(
            workspaceId,
            runId,
            cancellationToken);

        var orderedSteps = steps
            .OrderBy(step => step.StartedAtUtc ?? step.CompletedAtUtc ?? run.CreatedAtUtc)
            .ThenBy(step => step.StepKey, StringComparer.Ordinal)
            .ThenBy(step => step.Attempt)
            .Select(MapStep)
            .ToArray();

        return new(
            new WorkflowRunDetailView(
                run.Id,
                run.WorkflowDefinitionId,
                run.WorkflowVersionId,
                run.Status,
                run.CurrentStepKey,
                run.WaitReason,
                run.CreatedAtUtc,
                run.StartedAtUtc,
                run.CompletedAtUtc,
                run.ResumeAtUtc,
                run.ErrorCode,
                run.CancellationRequestedAtUtc,
                orderedSteps),
            null);
    }

    private static WorkflowDefinitionSummaryView MapDefinition(WorkflowDefinition definition) =>
        new(
            definition.Id,
            definition.Name,
            definition.Description,
            definition.Status,
            definition.MinimumRunRole,
            definition.CreatedAtUtc,
            definition.UpdatedAtUtc);

    private static WorkflowVersionSummaryView MapVersion(WorkflowVersion version) =>
        new(
            version.Id,
            version.VersionNumber,
            version.DefinitionHash,
            version.Status,
            version.CreatedAtUtc,
            version.ActivatedAtUtc,
            version.RetiredAtUtc);

    private static WorkflowRunSummaryView MapRun(WorkflowRun run) =>
        new(
            run.Id,
            run.WorkflowDefinitionId,
            run.WorkflowVersionId,
            run.Status,
            run.CurrentStepKey,
            run.WaitReason,
            run.CreatedAtUtc,
            run.StartedAtUtc,
            run.CompletedAtUtc,
            run.ResumeAtUtc,
            run.ErrorCode,
            run.CancellationRequestedAtUtc);

    private static WorkflowStepTimelineView MapStep(WorkflowStepRun step) =>
        new(
            step.Id,
            step.StepKey,
            step.Attempt,
            step.StepType,
            step.Status,
            step.ToolExecutionId,
            step.StartedAtUtc,
            step.CompletedAtUtc,
            step.NextAttemptAtUtc,
            step.ErrorCode);
}
