using ICEHOTT.Domain.Workflows;
using ICEHOTT.Domain.Workspaces;

namespace ICEHOTT.Application.Workflows;

public sealed record WorkflowCheckpointView(
    Guid Id,
    Guid WorkflowRunId,
    Guid StepRunId,
    Guid RequestedByUserId,
    WorkspaceRole MinimumApproverRole,
    bool RequiresDifferentApprover,
    WorkflowCheckpointStatus Status,
    Guid? DecidedByUserId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? DecidedAtUtc,
    string? Reason);

public sealed record WorkflowCheckpointResult<T>(
    T? Value,
    string? ErrorCode)
{
    public bool Succeeded => ErrorCode is null;
}
