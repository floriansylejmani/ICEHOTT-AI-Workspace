using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Knowledge;

namespace ICEHOTT.Application.Knowledge;

public sealed class KnowledgeService(
    IWorkspaceRepository workspaces,
    IKnowledgeRepository knowledge,
    IKnowledgeJobQueue jobs,
    IKnowledgeRetriever retriever,
    IDocumentTextExtractor textExtractor,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
{
    public const int MaxContentLength = 200_000;
    public const long MaxUploadBytes = 10 * 1024 * 1024;

    private const int MaxTitleLength = 200;
    private const int MaxSourceNameLength = 260;

    public async Task<KnowledgeResult<IReadOnlyList<KnowledgeDocumentView>>> ListAsync(
        Guid userId,
        Guid workspaceId,
        CancellationToken cancellationToken = default)
    {
        if (await workspaces.FindMembershipAsync(userId, workspaceId, cancellationToken) is null)
            return new(null, "workspace_not_found");

        var documents = await knowledge.ListDocumentsAsync(workspaceId, cancellationToken);
        return new(documents.Select(MapDocument).ToArray(), null);
    }

    public async Task<KnowledgeResult<KnowledgeDocumentView>> IngestAsync(
        Guid userId,
        Guid workspaceId,
        string title,
        string? sourceName,
        string content,
        CancellationToken cancellationToken = default)
    {
        if (await workspaces.FindMembershipAsync(userId, workspaceId, cancellationToken) is null)
            return new(null, "workspace_not_found");

        return await QueueDocumentAsync(
            userId,
            workspaceId,
            title,
            NormalizeSourceName(sourceName),
            content,
            cancellationToken);
    }

    public async Task<KnowledgeResult<KnowledgeDocumentView>> IngestFileAsync(
        Guid userId,
        Guid workspaceId,
        string? title,
        string sourceName,
        string? contentType,
        long length,
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        if (await workspaces.FindMembershipAsync(userId, workspaceId, cancellationToken) is null)
            return new(null, "workspace_not_found");

        if (length <= 0) return new(null, "file_required");
        if (length > MaxUploadBytes) return new(null, "file_too_large");

        var safeSourceName = NormalizeSourceName(Path.GetFileName(sourceName));
        if (string.IsNullOrWhiteSpace(safeSourceName)) return new(null, "file_name_required");

        var resolvedTitle = string.IsNullOrWhiteSpace(title)
            ? Path.GetFileNameWithoutExtension(safeSourceName)
            : title.Trim();

        string extracted;
        try
        {
            extracted = await textExtractor.ExtractAsync(
                stream,
                safeSourceName,
                contentType,
                cancellationToken);
        }
        catch (UnsupportedKnowledgeFileException)
        {
            return new(null, "unsupported_file_type");
        }
        catch (InvalidKnowledgeFileException)
        {
            return new(null, "file_parse_failed");
        }

        return await QueueDocumentAsync(
            userId,
            workspaceId,
            resolvedTitle,
            safeSourceName,
            extracted,
            cancellationToken);
    }

    public async Task<KnowledgeResult<KnowledgeDocumentView>> ReindexAsync(
        Guid userId,
        Guid workspaceId,
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        if (await workspaces.FindMembershipAsync(userId, workspaceId, cancellationToken) is null)
            return new(null, "workspace_not_found");

        var document = await knowledge.FindDocumentAsync(workspaceId, documentId, cancellationToken);
        if (document is null) return new(null, "document_not_found");

        if (document.Status is KnowledgeDocumentStatus.Queued or KnowledgeDocumentStatus.Processing)
            return new(MapDocument(document), null);

        document.MarkQueued();
        await jobs.EnqueueAsync(document.Id, workspaceId, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new(MapDocument(document), null);
    }

    public async Task<KnowledgeResult<KnowledgeDocumentView>> DeleteAsync(
        Guid userId,
        Guid workspaceId,
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        if (await workspaces.FindMembershipAsync(userId, workspaceId, cancellationToken) is null)
            return new(null, "workspace_not_found");

        var document = await knowledge.FindDocumentAsync(workspaceId, documentId, cancellationToken);
        if (document is null) return new(null, "document_not_found");

        var deleted = MapDocument(document);
        knowledge.Remove(document);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new(deleted, null);
    }

    public async Task<KnowledgeResult<IReadOnlyList<KnowledgeSearchView>>> SearchAsync(
        Guid userId,
        Guid workspaceId,
        string query,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var normalizedQuery = query.Trim();
        if (string.IsNullOrWhiteSpace(normalizedQuery)) return new(null, "query_required");
        if (normalizedQuery.Length > 2000) return new(null, "query_too_long");

        if (await workspaces.FindMembershipAsync(userId, workspaceId, cancellationToken) is null)
            return new(null, "workspace_not_found");

        var retrieval = await retriever.RetrieveAsync(
            workspaceId,
            normalizedQuery,
            Math.Clamp(limit, 1, 10),
            cancellationToken);

        return new(retrieval.Matches.Select(match => new KnowledgeSearchView(
            match.ChunkId,
            match.DocumentId,
            match.Title,
            match.SourceName,
            match.Content,
            match.Score)).ToArray(), null);
    }

    private async Task<KnowledgeResult<KnowledgeDocumentView>> QueueDocumentAsync(
        Guid userId,
        Guid workspaceId,
        string title,
        string? sourceName,
        string content,
        CancellationToken cancellationToken)
    {
        var normalizedTitle = title.Trim();
        var normalizedContent = content.Trim();

        if (string.IsNullOrWhiteSpace(normalizedTitle)) return new(null, "title_required");
        if (normalizedTitle.Length > MaxTitleLength) return new(null, "title_too_long");
        if (sourceName?.Length > MaxSourceNameLength) return new(null, "source_name_too_long");
        if (string.IsNullOrWhiteSpace(normalizedContent)) return new(null, "content_required");
        if (normalizedContent.Length > MaxContentLength) return new(null, "content_too_long");

        var now = clock.GetUtcNow();
        var document = new KnowledgeDocument(
            Guid.NewGuid(),
            workspaceId,
            userId,
            normalizedTitle,
            sourceName,
            normalizedContent,
            now);

        await knowledge.AddDocumentAsync(document, cancellationToken);
        await jobs.EnqueueAsync(document.Id, workspaceId, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new(MapDocument(document), null);
    }

    private static string? NormalizeSourceName(string? sourceName)
    {
        if (string.IsNullOrWhiteSpace(sourceName)) return null;
        var safeName = Path.GetFileName(sourceName.Trim());
        return string.IsNullOrWhiteSpace(safeName) ? null : safeName;
    }

    private static KnowledgeDocumentView MapDocument(KnowledgeDocument document) =>
        new(
            document.Id,
            document.Title,
            document.SourceName,
            document.Status,
            document.ChunkCount,
            document.Content.Length,
            document.CreatedAtUtc,
            document.IndexedAtUtc);
}
