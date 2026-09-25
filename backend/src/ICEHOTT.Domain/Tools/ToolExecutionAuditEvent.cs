namespace ICEHOTT.Domain.Tools;

public sealed class ToolExecutionAuditEvent
{
    private ToolExecutionAuditEvent() { }

    public ToolExecutionAuditEvent(
        Guid id,
        Guid executionId,
        Guid workspaceId,
        ToolExecutionAuditEventType eventType,
        Guid? actorUserId,
        DateTimeOffset occurredAtUtc)
    {
        Id = id;
        ExecutionId = executionId;
        WorkspaceId = workspaceId;
        EventType = eventType;
        ActorUserId = actorUserId;
        OccurredAtUtc = occurredAtUtc;
    }

    public Guid Id { get; private set; }
    public Guid ExecutionId { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public ToolExecutionAuditEventType EventType { get; private set; }
    public Guid? ActorUserId { get; private set; }
    public DateTimeOffset OccurredAtUtc { get; private set; }
}
