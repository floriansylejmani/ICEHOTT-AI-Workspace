namespace ICEHOTT.Domain.Agents;

public sealed class ConversationMessageCitation
{
    private ConversationMessageCitation() { }

    public ConversationMessageCitation(
        Guid id,
        Guid messageId,
        Guid workspaceId,
        Guid documentId,
        Guid chunkId,
        string title,
        string? sourceName,
        double score,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        MessageId = messageId;
        WorkspaceId = workspaceId;
        DocumentId = documentId;
        ChunkId = chunkId;
        Title = title.Trim();
        SourceName = string.IsNullOrWhiteSpace(sourceName) ? null : sourceName.Trim();
        Score = score;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }
    public Guid MessageId { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public Guid DocumentId { get; private set; }
    public Guid ChunkId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string? SourceName { get; private set; }
    public double Score { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
}
