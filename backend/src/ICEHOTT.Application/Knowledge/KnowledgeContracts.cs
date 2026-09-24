using ICEHOTT.Domain.Knowledge;

namespace ICEHOTT.Application.Knowledge;

public sealed record KnowledgeDocumentView(
    Guid Id,
    string Title,
    string? SourceName,
    KnowledgeDocumentStatus Status,
    int ChunkCount,
    int CharacterCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? IndexedAtUtc);

public sealed record KnowledgeSearchView(
    Guid ChunkId,
    Guid DocumentId,
    string Title,
    string? SourceName,
    string Content,
    double Score);

public sealed record KnowledgeResult<T>(T? Value, string? ErrorCode)
{
    public bool Succeeded => ErrorCode is null;
}
