namespace ICEHOTT.Domain.Agents;

public sealed class ConversationMessage
{
    private ConversationMessage() { }

    public ConversationMessage(
        Guid id,
        Guid conversationId,
        Guid workspaceId,
        MessageRole role,
        string content,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        ConversationId = conversationId;
        WorkspaceId = workspaceId;
        Role = role;
        Content = content.Trim();
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }
    public Guid ConversationId { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public MessageRole Role { get; private set; }
    public string Content { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; private set; }
}
