namespace ICEHOTT.Domain.Workflows;

public sealed class WorkflowTrigger
{
    private WorkflowTrigger() { }

    public WorkflowTrigger(
        Guid id,
        Guid workspaceId,
        Guid workflowDefinitionId,
        Guid workflowVersionId,
        string scheduleExpression,
        string timeZoneId,
        Guid runAsUserId,
        Guid createdByUserId,
        DateTimeOffset nextRunAtUtc,
        DateTimeOffset createdAtUtc)
    {
        if (id == Guid.Empty) throw new ArgumentException("Workflow trigger ID is required.", nameof(id));
        if (workspaceId == Guid.Empty) throw new ArgumentException("Workspace ID is required.", nameof(workspaceId));
        if (workflowDefinitionId == Guid.Empty) throw new ArgumentException("Workflow definition ID is required.", nameof(workflowDefinitionId));
        if (workflowVersionId == Guid.Empty) throw new ArgumentException("Workflow version ID is required.", nameof(workflowVersionId));
        if (string.IsNullOrWhiteSpace(scheduleExpression)) throw new ArgumentException("Schedule expression is required.", nameof(scheduleExpression));
        if (string.IsNullOrWhiteSpace(timeZoneId)) throw new ArgumentException("Time zone ID is required.", nameof(timeZoneId));
        if (runAsUserId == Guid.Empty) throw new ArgumentException("Run-as user ID is required.", nameof(runAsUserId));
        if (createdByUserId == Guid.Empty) throw new ArgumentException("Creator ID is required.", nameof(createdByUserId));

        Id = id;
        WorkspaceId = workspaceId;
        WorkflowDefinitionId = workflowDefinitionId;
        WorkflowVersionId = workflowVersionId;
        Type = WorkflowTriggerType.Schedule;
        ScheduleExpression = scheduleExpression.Trim();
        TimeZoneId = timeZoneId.Trim();
        RunAsUserId = runAsUserId;
        CreatedByUserId = createdByUserId;
        NextRunAtUtc = nextRunAtUtc;
        CreatedAtUtc = createdAtUtc;
        Enabled = false;
    }

    public Guid Id { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public Guid WorkflowDefinitionId { get; private set; }
    public Guid WorkflowVersionId { get; private set; }
    public WorkflowTriggerType Type { get; private set; }
    public string ScheduleExpression { get; private set; } = string.Empty;
    public string TimeZoneId { get; private set; } = string.Empty;
    public Guid RunAsUserId { get; private set; }
    public bool Enabled { get; private set; }
    public DateTimeOffset NextRunAtUtc { get; private set; }
    public DateTimeOffset? LastRunAtUtc { get; private set; }
    public Guid CreatedByUserId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }

    public void Enable(DateTimeOffset nextRunAtUtc)
    {
        Enabled = true;
        NextRunAtUtc = nextRunAtUtc;
    }

    public void Disable()
    {
        Enabled = false;
    }

    public void RecordFire(DateTimeOffset scheduledForUtc, DateTimeOffset nextRunAtUtc)
    {
        if (!Enabled)
            throw new InvalidOperationException("Disabled workflow triggers cannot fire.");

        LastRunAtUtc = scheduledForUtc;
        NextRunAtUtc = nextRunAtUtc;
    }
}
