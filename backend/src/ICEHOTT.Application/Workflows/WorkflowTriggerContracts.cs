using ICEHOTT.Domain.Workflows;

namespace ICEHOTT.Application.Workflows;

public sealed record WorkflowTriggerView(
    Guid Id,
    Guid WorkflowDefinitionId,
    Guid WorkflowVersionId,
    WorkflowTriggerType Type,
    string ScheduleExpression,
    string TimeZoneId,
    Guid RunAsUserId,
    bool Enabled,
    DateTimeOffset NextRunAtUtc,
    DateTimeOffset? LastRunAtUtc,
    Guid CreatedByUserId,
    DateTimeOffset CreatedAtUtc);

public sealed record WorkflowTriggerResult<T>(
    T? Value,
    string? ErrorCode)
{
    public bool Succeeded => ErrorCode is null;
}
