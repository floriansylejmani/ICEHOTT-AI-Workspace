using System.Text.Json;
using ICEHOTT.Domain.Tools;
using ICEHOTT.Domain.Workspaces;

namespace ICEHOTT.Application.Tools;

public enum ToolArgumentType
{
    String = 1,
    Integer = 2,
    Boolean = 3
}

public sealed record ToolArgumentDefinition(
    string Name,
    ToolArgumentType Type,
    bool Required,
    int? MaxLength = null);

public sealed record ToolDefinition(
    string Name,
    string Description,
    ToolRiskLevel RiskLevel,
    WorkspaceRole MinimumRequesterRole,
    bool RequiresApproval,
    WorkspaceRole? MinimumApproverRole,
    IReadOnlyList<ToolArgumentDefinition> Arguments);

public sealed record ToolArgumentValidationResult(
    bool IsValid,
    IReadOnlyList<string> Errors)
{
    public static ToolArgumentValidationResult Valid { get; } =
        new(true, []);

    public static ToolArgumentValidationResult Invalid(params string[] errors) =>
        new(false, errors);
}

public sealed record ToolExecutionContext(
    Guid WorkspaceId,
    Guid RequestedByUserId,
    Guid ExecutionId,
    DateTimeOffset StartedAtUtc);

public sealed record ToolExecutionOutput(string ResultJson);

public sealed record ToolDefinitionView(
    string Name,
    string Description,
    ToolRiskLevel RiskLevel,
    WorkspaceRole MinimumRequesterRole,
    bool RequiresApproval,
    WorkspaceRole? MinimumApproverRole,
    IReadOnlyList<ToolArgumentDefinition> Arguments,
    int PolicyVersion = 0);

public sealed record ToolAuditEventView(
    ToolExecutionAuditEventType EventType,
    Guid? ActorUserId,
    DateTimeOffset OccurredAtUtc);

public sealed record ToolExecutionView(
    Guid Id,
    Guid WorkspaceId,
    Guid RequestedByUserId,
    string ToolName,
    ToolRiskLevel RiskLevel,
    ToolExecutionStatus Status,
    string IdempotencyKey,
    JsonElement Arguments,
    Guid? ApprovedByUserId,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? ApprovedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    JsonElement? Result,
    string? ErrorCode,
    string? ErrorMessage,
    IReadOnlyList<ToolAuditEventView> AuditEvents,
    int PolicyVersion = 0);

public sealed record ToolOperationResult<T>(
    T? Value,
    string? ErrorCode,
    IReadOnlyList<string>? ValidationErrors = null,
    int? RetryAfterSeconds = null)
{
    public bool Succeeded => ErrorCode is null;
}
