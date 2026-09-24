using ICEHOTT.Domain.Knowledge;

namespace ICEHOTT.Application.Abstractions;

public interface IKnowledgeRepository
{
    Task AddDocumentAsync(
        KnowledgeDocument document,
        CancellationToken cancellationToken = default);

    Task AddAsync(
        KnowledgeDocument document,
        IReadOnlyList<KnowledgeChunk> chunks,
        CancellationToken cancellationToken = default);

    Task ReplaceChunksAsync(
        Guid workspaceId,
        Guid documentId,
        IReadOnlyList<KnowledgeChunk> chunks,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<KnowledgeDocument>> ListDocumentsAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default);

    Task<KnowledgeDocument?> FindDocumentAsync(
        Guid workspaceId,
        Guid documentId,
        CancellationToken cancellationToken = default);

    void Remove(KnowledgeDocument document);
}
