namespace ICEHOTT.Domain.Workflows;

public sealed class WorkflowAuditEvent
{
    private WorkflowAuditEvent() { }

    public WorkflowAuditEvent(
        Guid id,
        Guid workspaceId,
        WorkflowAuditEventType eventType,
        DateTimeOffset createdAtUtc,
        Guid? workflowDefinitionId = null,
        Guid? workflowRunId = null,
        Guid? workflowStepRunId = null,
        Guid? workflowTriggerId = null,
        Guid? actorUserId = null,
        string? detailJson = null)
    {
        if (id == Guid.Empty) throw new ArgumentException("Workflow audit event ID is required.", nameof(id));
        if (workspaceId == Guid.Empty) throw new ArgumentException("Workspace ID is required.", nameof(workspaceId));

        Id = id;
        WorkspaceId = workspaceId;
        WorkflowDefinitionId = workflowDefinitionId;
        WorkflowRunId = workflowRunId;
        WorkflowStepRunId = workflowStepRunId;
        WorkflowTriggerId = workflowTriggerId;
        ActorUserId = actorUserId;
        EventType = eventType;
        DetailJson = string.IsNullOrWhiteSpace(detailJson) ? null : detailJson;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public Guid? WorkflowDefinitionId { get; private set; }
    public Guid? WorkflowRunId { get; private set; }
    public Guid? WorkflowStepRunId { get; private set; }
    public Guid? WorkflowTriggerId { get; private set; }
    public Guid? ActorUserId { get; private set; }
    public WorkflowAuditEventType EventType { get; private set; }
    public string? DetailJson { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
}
