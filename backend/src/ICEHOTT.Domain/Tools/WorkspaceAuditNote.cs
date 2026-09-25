namespace ICEHOTT.Domain.Tools;

public sealed class WorkspaceAuditNote
{
    private WorkspaceAuditNote() { }

    public WorkspaceAuditNote(
        Guid id,
        Guid workspaceId,
        Guid toolExecutionId,
        Guid createdByUserId,
        string message,
        DateTimeOffset createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("Message is required.", nameof(message));

        Id = id;
        WorkspaceId = workspaceId;
        ToolExecutionId = toolExecutionId;
        CreatedByUserId = createdByUserId;
        Message = message.Trim();
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public Guid ToolExecutionId { get; private set; }
    public Guid CreatedByUserId { get; private set; }
    public string Message { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; private set; }
}
