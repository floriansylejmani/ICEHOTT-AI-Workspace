namespace ICEHOTT.Domain.Knowledge;

public sealed class KnowledgeChunk
{
    private KnowledgeChunk() { }

    public KnowledgeChunk(
        Guid id,
        Guid documentId,
        Guid workspaceId,
        int ordinal,
        string content,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        DocumentId = documentId;
        WorkspaceId = workspaceId;
        Ordinal = ordinal;
        Content = content.Trim();
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }
    public Guid DocumentId { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public int Ordinal { get; private set; }
    public string Content { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; private set; }
}
