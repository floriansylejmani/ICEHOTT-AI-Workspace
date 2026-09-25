using ICEHOTT.Domain.Workspaces;

namespace ICEHOTT.Domain.Tools;

public sealed class ToolExecution
{
    private ToolExecution() { }

    /// <summary>Admission under built-in defaults (policy version 0).</summary>
    public ToolExecution(
        Guid id,
        Guid workspaceId,
        Guid requestedByUserId,
        string toolName,
        ToolRiskLevel riskLevel,
        string argumentsJson,
        string argumentsHash,
        string idempotencyKey,
        bool requiresApproval,
        DateTimeOffset requestedAtUtc)
        : this(
            id, workspaceId, requestedByUserId, toolName, riskLevel,
            argumentsJson, argumentsHash, idempotencyKey, requestedAtUtc,
            new EffectiveToolPolicy(
                0,
                true,
                WorkspaceRole.Member,
                requiresApproval ? WorkspaceRole.Admin : null,
                requiresApproval,
                null))
    {
    }

    /// <summary>
    /// Admission under an effective policy, which is snapshotted on the row.
    /// Whether the execution needs approval is decided by the policy alone.
    /// </summary>
    public ToolExecution(
        Guid id,
        Guid workspaceId,
        Guid requestedByUserId,
        string toolName,
        ToolRiskLevel riskLevel,
        string argumentsJson,
        string argumentsHash,
        string idempotencyKey,
        DateTimeOffset requestedAtUtc,
        EffectiveToolPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!policy.Enabled)
            throw new InvalidOperationException("A disabled tool cannot be admitted.");
        if (policy.RequiresApproval && policy.MinimumApproverRole is null)
            throw new ArgumentException("An approval policy needs an approver role.", nameof(policy));
        var requiresApproval = policy.RequiresApproval;

        if (id == Guid.Empty) throw new ArgumentException("Execution ID is required.", nameof(id));
        if (workspaceId == Guid.Empty) throw new ArgumentException("Workspace ID is required.", nameof(workspaceId));
        if (requestedByUserId == Guid.Empty) throw new ArgumentException("Requester ID is required.", nameof(requestedByUserId));
        if (string.IsNullOrWhiteSpace(toolName)) throw new ArgumentException("Tool name is required.", nameof(toolName));
        if (string.IsNullOrWhiteSpace(argumentsJson)) throw new ArgumentException("Arguments JSON is required.", nameof(argumentsJson));
        if (string.IsNullOrWhiteSpace(argumentsHash)) throw new ArgumentException("Arguments hash is required.", nameof(argumentsHash));
        if (string.IsNullOrWhiteSpace(idempotencyKey)) throw new ArgumentException("Idempotency key is required.", nameof(idempotencyKey));

        Id = id;
        WorkspaceId = workspaceId;
        RequestedByUserId = requestedByUserId;
        ToolName = toolName.Trim();
        RiskLevel = riskLevel;
        ArgumentsJson = argumentsJson;
        ArgumentsHash = argumentsHash;
        IdempotencyKey = idempotencyKey.Trim();
        Status = requiresApproval ? ToolExecutionStatus.PendingApproval : ToolExecutionStatus.Ready;
        RequestedAtUtc = requestedAtUtc;
        PolicyVersion = policy.Version;
        PolicyMinimumRequesterRole = policy.MinimumRequesterRole;
        PolicyMinimumApproverRole = policy.MinimumApproverRole;
        PolicyRequiresApproval = policy.RequiresApproval;
        PolicyMaxArgumentLength = policy.MaxArgumentLength;
    }

    // Effective policy snapshot at admission (Phase 4.5 packet B). Immutable.
    public int PolicyVersion { get; private set; }
    public WorkspaceRole PolicyMinimumRequesterRole { get; private set; } = WorkspaceRole.Member;
    public WorkspaceRole? PolicyMinimumApproverRole { get; private set; }
    public bool PolicyRequiresApproval { get; private set; }
    public int? PolicyMaxArgumentLength { get; private set; }

    public EffectiveToolPolicy PolicySnapshot =>
        new(
            PolicyVersion,
            true,
            PolicyMinimumRequesterRole,
            PolicyMinimumApproverRole,
            PolicyRequiresApproval,
            PolicyMaxArgumentLength);

    public Guid Id { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public Guid RequestedByUserId { get; private set; }
    public string ToolName { get; private set; } = string.Empty;
    public ToolRiskLevel RiskLevel { get; private set; }
    public string ArgumentsJson { get; private set; } = "{}";
    public string ArgumentsHash { get; private set; } = string.Empty;
    public string IdempotencyKey { get; private set; } = string.Empty;
    public ToolExecutionStatus Status { get; private set; }
    public Guid? ApprovedByUserId { get; private set; }
    public DateTimeOffset RequestedAtUtc { get; private set; }
    public DateTimeOffset? ApprovedAtUtc { get; private set; }
    public DateTimeOffset? StartedAtUtc { get; private set; }
    public Guid? LeaseOwnerId { get; private set; }
    public DateTimeOffset? DeadlineAtUtc { get; private set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public string? ResultJson { get; private set; }
    public string? ErrorCode { get; private set; }
    public string? ErrorMessage { get; private set; }

    public void Approve(Guid approverUserId, DateTimeOffset approvedAtUtc)
    {
        if (Status != ToolExecutionStatus.PendingApproval)
            throw new InvalidOperationException("Only pending executions can be approved.");
        if (approverUserId == Guid.Empty)
            throw new ArgumentException("Approver ID is required.", nameof(approverUserId));
        if (approverUserId == RequestedByUserId)
            throw new InvalidOperationException("Sensitive tool executions cannot be self-approved.");

        ApprovedByUserId = approverUserId;
        ApprovedAtUtc = approvedAtUtc;
        Status = ToolExecutionStatus.Ready;
    }

    public void Reject(Guid approverUserId, DateTimeOffset rejectedAtUtc)
    {
        if (Status != ToolExecutionStatus.PendingApproval)
            throw new InvalidOperationException("Only pending executions can be rejected.");
        if (approverUserId == Guid.Empty)
            throw new ArgumentException("Approver ID is required.", nameof(approverUserId));
        if (approverUserId == RequestedByUserId)
            throw new InvalidOperationException("Sensitive tool executions cannot be self-rejected.");

        ApprovedByUserId = approverUserId;
        ApprovedAtUtc = rejectedAtUtc;
        CompletedAtUtc = rejectedAtUtc;
        Status = ToolExecutionStatus.Rejected;
    }

    public void Start(DateTimeOffset startedAtUtc)
    {
        if (Status != ToolExecutionStatus.Ready)
            throw new InvalidOperationException("Only ready executions can start.");

        StartedAtUtc = startedAtUtc;
        Status = ToolExecutionStatus.Running;
    }

    public void Start(
        DateTimeOffset startedAtUtc,
        Guid leaseOwnerId,
        DateTimeOffset deadlineAtUtc,
        DateTimeOffset leaseExpiresAtUtc)
    {
        if (Status != ToolExecutionStatus.Ready)
            throw new InvalidOperationException("Only ready executions can start.");
        if (leaseOwnerId == Guid.Empty)
            throw new ArgumentException("Lease owner is required.", nameof(leaseOwnerId));
        if (deadlineAtUtc <= startedAtUtc)
            throw new ArgumentOutOfRangeException(nameof(deadlineAtUtc));
        if (leaseExpiresAtUtc <= deadlineAtUtc)
            throw new ArgumentOutOfRangeException(nameof(leaseExpiresAtUtc));

        LeaseOwnerId = leaseOwnerId;
        DeadlineAtUtc = deadlineAtUtc;
        LeaseExpiresAtUtc = leaseExpiresAtUtc;
        Start(startedAtUtc);
    }

    public void Cancel(DateTimeOffset cancelledAtUtc)
    {
        if (Status is not (ToolExecutionStatus.PendingApproval or ToolExecutionStatus.Ready))
            throw new InvalidOperationException("Only executions that have not started can be cancelled.");

        CompletedAtUtc = cancelledAtUtc;
        Status = ToolExecutionStatus.Cancelled;
    }

    public void TimeOut(DateTimeOffset timedOutAtUtc)
    {
        if (Status != ToolExecutionStatus.Running)
            throw new InvalidOperationException("Only running executions can time out.");

        ErrorCode = "tool_timeout";
        ErrorMessage = "Tool execution timed out.";
        CompletedAtUtc = timedOutAtUtc;
        Status = ToolExecutionStatus.TimedOut;
    }

    public void MarkOutcomeUnknown(DateTimeOffset detectedAtUtc)
    {
        if (Status != ToolExecutionStatus.Running)
            throw new InvalidOperationException("Only running executions can have an unknown outcome.");

        ErrorCode = "tool_outcome_unknown";
        ErrorMessage = "Tool execution outcome requires operator review.";
        CompletedAtUtc = detectedAtUtc;
        Status = ToolExecutionStatus.OutcomeUnknown;
    }

    public void Succeed(string resultJson, DateTimeOffset completedAtUtc)
    {
        if (Status != ToolExecutionStatus.Running)
            throw new InvalidOperationException("Only running executions can succeed.");
        if (string.IsNullOrWhiteSpace(resultJson))
            throw new ArgumentException("Result JSON is required.", nameof(resultJson));

        ResultJson = resultJson;
        ErrorCode = null;
        ErrorMessage = null;
        CompletedAtUtc = completedAtUtc;
        Status = ToolExecutionStatus.Succeeded;
    }

    public void Fail(string errorCode, string errorMessage, DateTimeOffset completedAtUtc)
    {
        if (Status != ToolExecutionStatus.Running)
            throw new InvalidOperationException("Only running executions can fail.");
        if (string.IsNullOrWhiteSpace(errorCode))
            throw new ArgumentException("Error code is required.", nameof(errorCode));

        ErrorCode = errorCode.Trim();
        ErrorMessage = string.IsNullOrWhiteSpace(errorMessage)
            ? "Tool execution failed."
            : errorMessage.Trim();
        CompletedAtUtc = completedAtUtc;
        Status = ToolExecutionStatus.Failed;
    }
}
