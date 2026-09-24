namespace ICEHOTT.Domain.Knowledge;

public sealed class KnowledgeDocument
{
    private KnowledgeDocument() { }

    public KnowledgeDocument(
        Guid id,
        Guid workspaceId,
        Guid createdByUserId,
        string title,
        string? sourceName,
        string content,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        WorkspaceId = workspaceId;
        CreatedByUserId = createdByUserId;
        Title = title.Trim();
        SourceName = string.IsNullOrWhiteSpace(sourceName) ? null : sourceName.Trim();
        Content = content.Trim();
        Status = KnowledgeDocumentStatus.Queued;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public Guid CreatedByUserId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string? SourceName { get; private set; }
    public string Content { get; private set; } = string.Empty;
    public KnowledgeDocumentStatus Status { get; private set; }
    public int ChunkCount { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? IndexedAtUtc { get; private set; }

    public void MarkQueued()
    {
        Status = KnowledgeDocumentStatus.Queued;
        IndexedAtUtc = null;
    }

    public void MarkProcessing()
    {
        Status = KnowledgeDocumentStatus.Processing;
        IndexedAtUtc = null;
    }

    public void MarkReady(int chunkCount, DateTimeOffset indexedAtUtc)
    {
        Status = KnowledgeDocumentStatus.Ready;
        ChunkCount = chunkCount;
        IndexedAtUtc = indexedAtUtc;
    }

    public void MarkFailed()
    {
        Status = KnowledgeDocumentStatus.Failed;
        ChunkCount = 0;
        IndexedAtUtc = null;
    }
}
