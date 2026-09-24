namespace ICEHOTT.Domain.Agents;

public sealed class Conversation
{
    private Conversation() { }

    public Conversation(Guid id, Guid workspaceId, Guid createdByUserId, string title, DateTimeOffset createdAtUtc)
    {
        Id = id;
        WorkspaceId = workspaceId;
        CreatedByUserId = createdByUserId;
        Title = title.Trim();
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public Guid CreatedByUserId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public void Touch(DateTimeOffset now) => UpdatedAtUtc = now;
}
