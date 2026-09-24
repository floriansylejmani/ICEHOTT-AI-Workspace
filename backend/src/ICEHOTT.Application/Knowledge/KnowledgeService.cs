using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Knowledge;

namespace ICEHOTT.Application.Knowledge;

public sealed class KnowledgeService(
    IWorkspaceRepository workspaces,
    IKnowledgeRepository knowledge,
    IVectorStore vectorStore,
    IAiRuntimeClient aiRuntime,
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

        return await IngestCoreAsync(
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

        return await IngestCoreAsync(
            userId,
            workspaceId,
            resolvedTitle,
            safeSourceName,
            extracted,
            cancellationToken);
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

        var embeddingReply = await aiRuntime.EmbedAsync([normalizedQuery], cancellationToken);
        if (embeddingReply.Embeddings.Count != 1)
            throw new AiRuntimeUnavailableException("AI runtime returned an invalid query embedding.");

        var matches = await vectorStore.SearchAsync(
            workspaceId,
            normalizedQuery,
            embeddingReply.Embeddings[0],
            Math.Clamp(limit, 1, 10),
            cancellationToken);

        return new(matches.Select(match => new KnowledgeSearchView(
            match.ChunkId,
            match.DocumentId,
            match.Title,
            match.SourceName,
            match.Content,
            match.Score)).ToArray(), null);
    }

    private async Task<KnowledgeResult<KnowledgeDocumentView>> IngestCoreAsync(
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

        var chunkTexts = ChunkText(normalizedContent);
        var chunks = chunkTexts.Select((text, index) =>
            new KnowledgeChunk(Guid.NewGuid(), document.Id, workspaceId, index, text, now)).ToArray();

        await knowledge.AddAsync(document, chunks, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        try
        {
            var embeddedChunks = await EmbedInBatchesAsync(chunkTexts, cancellationToken);
            if (embeddedChunks.Count != chunks.Length)
                throw new AiRuntimeUnavailableException("Embedding count did not match chunk count.");

            var embeddings = chunks.Select((chunk, index) =>
                new VectorEmbedding(chunk.Id, embeddedChunks[index])).ToArray();

            await vectorStore.StoreManyAsync(workspaceId, embeddings, cancellationToken);
            document.MarkReady(chunks.Length, clock.GetUtcNow());
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            document.MarkFailed();
            await unitOfWork.SaveChangesAsync(cancellationToken);
            throw;
        }

        return new(MapDocument(document), null);
    }

    private async Task<IReadOnlyList<IReadOnlyList<float>>> EmbedInBatchesAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        const int batchSize = 64;
        var embeddings = new List<IReadOnlyList<float>>(texts.Count);
        int? dimensions = null;

        for (var start = 0; start < texts.Count; start += batchSize)
        {
            var batch = texts.Skip(start).Take(batchSize).ToArray();
            var reply = await aiRuntime.EmbedAsync(batch, cancellationToken);

            if (reply.Embeddings.Count != batch.Length)
                throw new AiRuntimeUnavailableException("Embedding batch size did not match request size.");

            dimensions ??= reply.Dimensions;
            if (reply.Dimensions != dimensions)
                throw new AiRuntimeUnavailableException("Embedding dimensions changed between batches.");

            embeddings.AddRange(reply.Embeddings);
        }

        return embeddings;
    }

    private static string[] ChunkText(string content)
    {
        const int wordsPerChunk = 140;
        const int overlapWords = 25;
        var words = content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return [];

        var chunks = new List<string>();
        var start = 0;

        while (start < words.Length)
        {
            var count = Math.Min(wordsPerChunk, words.Length - start);
            chunks.Add(string.Join(' ', words, start, count));
            if (start + count >= words.Length) break;
            start += wordsPerChunk - overlapWords;
        }

        return chunks.ToArray();
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
