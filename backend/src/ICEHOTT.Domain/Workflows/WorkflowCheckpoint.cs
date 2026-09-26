using ICEHOTT.Domain.Workspaces;

namespace ICEHOTT.Domain.Workflows;

public sealed class WorkflowCheckpoint
{
    private WorkflowCheckpoint() { }

    public WorkflowCheckpoint(
        Guid id,
        Guid workspaceId,
        Guid workflowRunId,
        Guid stepRunId,
        Guid requestedByUserId,
        WorkspaceRole minimumApproverRole,
        bool requiresDifferentApprover,
        DateTimeOffset createdAtUtc)
    {
        if (id == Guid.Empty) throw new ArgumentException("Checkpoint ID is required.", nameof(id));
        if (workspaceId == Guid.Empty) throw new ArgumentException("Workspace ID is required.", nameof(workspaceId));
        if (workflowRunId == Guid.Empty) throw new ArgumentException("Workflow run ID is required.", nameof(workflowRunId));
        if (stepRunId == Guid.Empty) throw new ArgumentException("Workflow step run ID is required.", nameof(stepRunId));
        if (requestedByUserId == Guid.Empty) throw new ArgumentException("Requester ID is required.", nameof(requestedByUserId));
        if (minimumApproverRole is < WorkspaceRole.Member or > WorkspaceRole.Owner)
            throw new ArgumentOutOfRangeException(nameof(minimumApproverRole));

        Id = id;
        WorkspaceId = workspaceId;
        WorkflowRunId = workflowRunId;
        StepRunId = stepRunId;
        RequestedByUserId = requestedByUserId;
        MinimumApproverRole = minimumApproverRole;
        RequiresDifferentApprover = requiresDifferentApprover;
        CreatedAtUtc = createdAtUtc;
        Status = WorkflowCheckpointStatus.Pending;
    }

    public Guid Id { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public Guid WorkflowRunId { get; private set; }
    public Guid StepRunId { get; private set; }
    public Guid RequestedByUserId { get; private set; }
    public WorkspaceRole MinimumApproverRole { get; private set; }
    public bool RequiresDifferentApprover { get; private set; }
    public WorkflowCheckpointStatus Status { get; private set; }
    public Guid? DecidedByUserId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? DecidedAtUtc { get; private set; }
    public string? Reason { get; private set; }

    public void Approve(Guid approverUserId, string? reason, DateTimeOffset decidedAtUtc)
    {
        Decide(approverUserId, WorkflowCheckpointStatus.Approved, reason, decidedAtUtc);
    }

    public void Reject(Guid approverUserId, string? reason, DateTimeOffset decidedAtUtc)
    {
        Decide(approverUserId, WorkflowCheckpointStatus.Rejected, reason, decidedAtUtc);
    }

    public void Expire(DateTimeOffset decidedAtUtc)
    {
        if (Status != WorkflowCheckpointStatus.Pending)
            throw new InvalidOperationException("Only pending checkpoints can expire.");

        Status = WorkflowCheckpointStatus.Expired;
        DecidedAtUtc = decidedAtUtc;
    }

    private void Decide(
        Guid approverUserId,
        WorkflowCheckpointStatus decision,
        string? reason,
        DateTimeOffset decidedAtUtc)
    {
        if (Status != WorkflowCheckpointStatus.Pending)
            throw new InvalidOperationException("Only pending checkpoints can be decided.");
        if (approverUserId == Guid.Empty)
            throw new ArgumentException("Approver ID is required.", nameof(approverUserId));
        if (RequiresDifferentApprover && approverUserId == RequestedByUserId)
            throw new InvalidOperationException("Checkpoint cannot be self-approved or self-rejected.");

        Status = decision;
        DecidedByUserId = approverUserId;
        DecidedAtUtc = decidedAtUtc;
        Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
    }
}
