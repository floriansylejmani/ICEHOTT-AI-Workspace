using ICEHOTT.Domain.Knowledge;

namespace ICEHOTT.Application.Abstractions;

public interface IKnowledgeRepository
{
    Task AddAsync(
        KnowledgeDocument document,
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
