namespace ICEHOTT.Domain.Workflows;

public sealed class WorkflowRun
{
    private WorkflowRun() { }

    public WorkflowRun(
        Guid id,
        Guid workspaceId,
        Guid workflowDefinitionId,
        Guid workflowVersionId,
        Guid requestedByUserId,
        Guid runAsUserId,
        string idempotencyKey,
        DateTimeOffset createdAtUtc)
    {
        if (id == Guid.Empty) throw new ArgumentException("Workflow run ID is required.", nameof(id));
        if (workspaceId == Guid.Empty) throw new ArgumentException("Workspace ID is required.", nameof(workspaceId));
        if (workflowDefinitionId == Guid.Empty) throw new ArgumentException("Workflow definition ID is required.", nameof(workflowDefinitionId));
        if (workflowVersionId == Guid.Empty) throw new ArgumentException("Workflow version ID is required.", nameof(workflowVersionId));
        if (requestedByUserId == Guid.Empty) throw new ArgumentException("Requester ID is required.", nameof(requestedByUserId));
        if (runAsUserId == Guid.Empty) throw new ArgumentException("Run-as user ID is required.", nameof(runAsUserId));
        if (string.IsNullOrWhiteSpace(idempotencyKey)) throw new ArgumentException("Idempotency key is required.", nameof(idempotencyKey));

        Id = id;
        WorkspaceId = workspaceId;
        WorkflowDefinitionId = workflowDefinitionId;
        WorkflowVersionId = workflowVersionId;
        RequestedByUserId = requestedByUserId;
        RunAsUserId = runAsUserId;
        IdempotencyKey = idempotencyKey.Trim();
        CreatedAtUtc = createdAtUtc;
        Status = WorkflowRunStatus.Queued;
    }

    public Guid Id { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public Guid WorkflowDefinitionId { get; private set; }
    public Guid WorkflowVersionId { get; private set; }
    public Guid RequestedByUserId { get; private set; }
    public Guid RunAsUserId { get; private set; }
    public string IdempotencyKey { get; private set; } = string.Empty;
    public WorkflowRunStatus Status { get; private set; }
    public string? CurrentStepKey { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? StartedAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public Guid? LeaseOwnerId { get; private set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; private set; }
    public int LeaseGeneration { get; private set; }
    public WorkflowWaitReason? WaitReason { get; private set; }
    public DateTimeOffset? ResumeAtUtc { get; private set; }
    public string? ErrorCode { get; private set; }
    public string? ErrorMessage { get; private set; }

    public void Start(string? currentStepKey, DateTimeOffset startedAtUtc)
    {
        if (Status != WorkflowRunStatus.Queued)
            throw new InvalidOperationException("Only queued workflow runs can start.");

        Status = WorkflowRunStatus.Running;
        CurrentStepKey = string.IsNullOrWhiteSpace(currentStepKey) ? null : currentStepKey.Trim();
        StartedAtUtc = startedAtUtc;
    }

    public int ClaimLease(Guid leaseOwnerId, DateTimeOffset leaseExpiresAtUtc)
    {
        if (leaseOwnerId == Guid.Empty)
            throw new ArgumentException("Lease owner ID is required.", nameof(leaseOwnerId));
        if (Status is WorkflowRunStatus.Succeeded or WorkflowRunStatus.Failed or WorkflowRunStatus.Cancelled or WorkflowRunStatus.OutcomeUnknown)
            throw new InvalidOperationException("Terminal workflow runs cannot be leased.");

        LeaseGeneration = checked(LeaseGeneration + 1);
        LeaseOwnerId = leaseOwnerId;
        LeaseExpiresAtUtc = leaseExpiresAtUtc;
        return LeaseGeneration;
    }

    public void Wait(WorkflowWaitReason reason, DateTimeOffset? resumeAtUtc = null)
    {
        if (Status != WorkflowRunStatus.Running)
            throw new InvalidOperationException("Only running workflow runs can wait.");
        if (reason != WorkflowWaitReason.Checkpoint && resumeAtUtc is null)
            throw new ArgumentException("Delay and retry waits require a resume time.", nameof(resumeAtUtc));

        Status = WorkflowRunStatus.Waiting;
        WaitReason = reason;
        ResumeAtUtc = resumeAtUtc;
        ClearLease();
    }

    public void Resume(string? currentStepKey = null)
    {
        if (Status != WorkflowRunStatus.Waiting)
            throw new InvalidOperationException("Only waiting workflow runs can resume.");

        Status = WorkflowRunStatus.Running;
        WaitReason = null;
        ResumeAtUtc = null;
        if (!string.IsNullOrWhiteSpace(currentStepKey))
            CurrentStepKey = currentStepKey.Trim();
    }

    public void Succeed(DateTimeOffset completedAtUtc)
    {
        if (Status != WorkflowRunStatus.Running)
            throw new InvalidOperationException("Only running workflow runs can succeed.");

        Status = WorkflowRunStatus.Succeeded;
        CompletedAtUtc = completedAtUtc;
        ErrorCode = null;
        ErrorMessage = null;
        ClearLease();
    }

    public void Fail(string errorCode, string? errorMessage, DateTimeOffset completedAtUtc)
    {
        if (Status is WorkflowRunStatus.Succeeded or WorkflowRunStatus.Failed or WorkflowRunStatus.Cancelled or WorkflowRunStatus.OutcomeUnknown)
            throw new InvalidOperationException("Terminal workflow run cannot fail.");
        if (string.IsNullOrWhiteSpace(errorCode))
            throw new ArgumentException("Error code is required.", nameof(errorCode));

        Status = WorkflowRunStatus.Failed;
        CompletedAtUtc = completedAtUtc;
        ErrorCode = errorCode.Trim();
        ErrorMessage = string.IsNullOrWhiteSpace(errorMessage) ? null : errorMessage.Trim();
        WaitReason = null;
        ResumeAtUtc = null;
        ClearLease();
    }

    public void Cancel(DateTimeOffset completedAtUtc)
    {
        if (Status is WorkflowRunStatus.Succeeded or WorkflowRunStatus.Failed or WorkflowRunStatus.Cancelled or WorkflowRunStatus.OutcomeUnknown)
            throw new InvalidOperationException("Terminal workflow run cannot be cancelled.");

        Status = WorkflowRunStatus.Cancelled;
        CompletedAtUtc = completedAtUtc;
        WaitReason = null;
        ResumeAtUtc = null;
        ClearLease();
    }

    public void MarkOutcomeUnknown(string? errorMessage, DateTimeOffset completedAtUtc)
    {
        if (Status is not (WorkflowRunStatus.Running or WorkflowRunStatus.Waiting))
            throw new InvalidOperationException("Only active workflow runs can have an unknown outcome.");

        Status = WorkflowRunStatus.OutcomeUnknown;
        CompletedAtUtc = completedAtUtc;
        ErrorCode = "workflow_outcome_unknown";
        ErrorMessage = string.IsNullOrWhiteSpace(errorMessage)
            ? "Workflow outcome requires operator review."
            : errorMessage.Trim();
        WaitReason = null;
        ResumeAtUtc = null;
        ClearLease();
    }

    private void ClearLease()
    {
        LeaseOwnerId = null;
        LeaseExpiresAtUtc = null;
    }
}
