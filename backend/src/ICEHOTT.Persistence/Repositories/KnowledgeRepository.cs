using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Knowledge;
using Microsoft.EntityFrameworkCore;

namespace ICEHOTT.Persistence.Repositories;

public sealed class KnowledgeRepository(ICEHOTTDbContext db) : IKnowledgeRepository
{
    public async Task AddDocumentAsync(
        KnowledgeDocument document,
        CancellationToken cancellationToken = default) =>
        await db.KnowledgeDocuments.AddAsync(document, cancellationToken);

    public async Task AddAsync(
        KnowledgeDocument document,
        IReadOnlyList<KnowledgeChunk> chunks,
        CancellationToken cancellationToken = default)
    {
        await db.KnowledgeDocuments.AddAsync(document, cancellationToken);
        if (chunks.Count > 0)
            await db.KnowledgeChunks.AddRangeAsync(chunks, cancellationToken);
    }

    public async Task ReplaceChunksAsync(
        Guid workspaceId,
        Guid documentId,
        IReadOnlyList<KnowledgeChunk> chunks,
        CancellationToken cancellationToken = default)
    {
        var existing = await db.KnowledgeChunks
            .Where(x => x.WorkspaceId == workspaceId && x.DocumentId == documentId)
            .ToListAsync(cancellationToken);

        if (existing.Count > 0) db.KnowledgeChunks.RemoveRange(existing);
        if (chunks.Count > 0) await db.KnowledgeChunks.AddRangeAsync(chunks, cancellationToken);
    }

    public async Task<IReadOnlyList<KnowledgeDocument>> ListDocumentsAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default)
    {
        var documents = await db.KnowledgeDocuments.AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId)
            .ToListAsync(cancellationToken);

        return documents.OrderByDescending(x => x.CreatedAtUtc).ToArray();
    }

    public Task<KnowledgeDocument?> FindDocumentAsync(
        Guid workspaceId,
        Guid documentId,
        CancellationToken cancellationToken = default) =>
        db.KnowledgeDocuments.SingleOrDefaultAsync(
            x => x.WorkspaceId == workspaceId && x.Id == documentId,
            cancellationToken);

    public void Remove(KnowledgeDocument document) => db.KnowledgeDocuments.Remove(document);
}
