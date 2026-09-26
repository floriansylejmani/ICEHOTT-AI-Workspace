using ICEHOTT.Domain.Workflows;
using ICEHOTT.Domain.Workspaces;

namespace ICEHOTT.Application.Workflows;

public sealed record WorkflowDefinitionSummaryView(
    Guid Id,
    string Name,
    string? Description,
    WorkflowDefinitionStatus Status,
    WorkspaceRole MinimumRunRole,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record WorkflowVersionSummaryView(
    Guid Id,
    int VersionNumber,
    string DefinitionHash,
    WorkflowVersionStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ActivatedAtUtc,
    DateTimeOffset? RetiredAtUtc);

public sealed record WorkflowDefinitionDetailView(
    Guid Id,
    string Name,
    string? Description,
    WorkflowDefinitionStatus Status,
    WorkspaceRole MinimumRunRole,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<WorkflowVersionSummaryView> Versions);

public sealed record WorkflowRunSummaryView(
    Guid Id,
    Guid WorkflowDefinitionId,
    Guid WorkflowVersionId,
    WorkflowRunStatus Status,
    string? CurrentStepKey,
    WorkflowWaitReason? WaitReason,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    DateTimeOffset? ResumeAtUtc,
    string? ErrorCode,
    DateTimeOffset? CancellationRequestedAtUtc);

public sealed record WorkflowStepTimelineView(
    Guid Id,
    string StepKey,
    int Attempt,
    WorkflowStepType StepType,
    WorkflowStepRunStatus Status,
    Guid? ToolExecutionId,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    DateTimeOffset? NextAttemptAtUtc,
    string? ErrorCode);

public sealed record WorkflowRunDetailView(
    Guid Id,
    Guid WorkflowDefinitionId,
    Guid WorkflowVersionId,
    WorkflowRunStatus Status,
    string? CurrentStepKey,
    WorkflowWaitReason? WaitReason,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    DateTimeOffset? ResumeAtUtc,
    string? ErrorCode,
    DateTimeOffset? CancellationRequestedAtUtc,
    IReadOnlyList<WorkflowStepTimelineView> Steps);

public sealed record WorkflowExperienceResult<T>(
    T? Value,
    string? ErrorCode)
{
    public bool Succeeded => ErrorCode is null;
}
