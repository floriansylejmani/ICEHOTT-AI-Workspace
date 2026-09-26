namespace ICEHOTT.Domain.Workflows;

public sealed class WorkflowTriggerFire
{
    private WorkflowTriggerFire() { }

    public WorkflowTriggerFire(
        Guid id,
        Guid workspaceId,
        Guid triggerId,
        Guid workflowVersionId,
        DateTimeOffset scheduledForUtc,
        string fireKey,
        DateTimeOffset createdAtUtc)
    {
        if (id == Guid.Empty) throw new ArgumentException("Trigger fire ID is required.", nameof(id));
        if (workspaceId == Guid.Empty) throw new ArgumentException("Workspace ID is required.", nameof(workspaceId));
        if (triggerId == Guid.Empty) throw new ArgumentException("Trigger ID is required.", nameof(triggerId));
        if (workflowVersionId == Guid.Empty) throw new ArgumentException("Workflow version ID is required.", nameof(workflowVersionId));
        if (string.IsNullOrWhiteSpace(fireKey)) throw new ArgumentException("Fire key is required.", nameof(fireKey));

        Id = id;
        WorkspaceId = workspaceId;
        TriggerId = triggerId;
        WorkflowVersionId = workflowVersionId;
        ScheduledForUtc = scheduledForUtc;
        FireKey = fireKey.Trim();
        CreatedAtUtc = createdAtUtc;
        Status = WorkflowTriggerFireStatus.Claimed;
    }

    public Guid Id { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public Guid TriggerId { get; private set; }
    public Guid WorkflowVersionId { get; private set; }
    public DateTimeOffset ScheduledForUtc { get; private set; }
    public string FireKey { get; private set; } = string.Empty;
    public WorkflowTriggerFireStatus Status { get; private set; }
    public Guid? WorkflowRunId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public void MarkRunCreated(Guid workflowRunId, DateTimeOffset completedAtUtc)
    {
        if (Status != WorkflowTriggerFireStatus.Claimed)
            throw new InvalidOperationException("Only claimed trigger fires can create a run.");
        if (workflowRunId == Guid.Empty)
            throw new ArgumentException("Workflow run ID is required.", nameof(workflowRunId));

        WorkflowRunId = workflowRunId;
        Status = WorkflowTriggerFireStatus.RunCreated;
        CompletedAtUtc = completedAtUtc;
    }

    public void Skip(DateTimeOffset completedAtUtc)
    {
        Complete(WorkflowTriggerFireStatus.Skipped, completedAtUtc);
    }

    public void Fail(DateTimeOffset completedAtUtc)
    {
        Complete(WorkflowTriggerFireStatus.Failed, completedAtUtc);
    }

    private void Complete(WorkflowTriggerFireStatus status, DateTimeOffset completedAtUtc)
    {
        if (Status != WorkflowTriggerFireStatus.Claimed)
            throw new InvalidOperationException("Only claimed trigger fires can complete.");

        Status = status;
        CompletedAtUtc = completedAtUtc;
    }
}
