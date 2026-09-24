using System.Diagnostics;
using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Knowledge;

namespace ICEHOTT.Application.Knowledge;

public sealed class KnowledgeIndexingProcessor(
    IKnowledgeRepository knowledge,
    IKnowledgeChunker chunker,
    IEmbeddingProvider embeddings,
    IVectorStore vectorStore,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
{
    public async Task<int> ProcessAsync(
        Guid workspaceId,
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        using var activity = RagTelemetry.ActivitySource.StartActivity("rag.index_document");
        activity?.SetTag("workspace.id", workspaceId);
        activity?.SetTag("document.id", documentId);

        try
        {
            var document = await knowledge.FindDocumentAsync(
                workspaceId,
                documentId,
                cancellationToken);

            if (document is null)
            {
                activity?.SetTag("rag.document_missing", true);
                return 0;
            }

            document.MarkProcessing();
            await unitOfWork.SaveChangesAsync(cancellationToken);

            var chunkTexts = chunker.Chunk(document.Content);
            if (chunkTexts.Count == 0)
                throw new InvalidOperationException("Knowledge document produced no indexable chunks.");

            var embedded = await EmbedInBatchesAsync(chunkTexts, cancellationToken);
            if (embedded.Vectors.Count != chunkTexts.Count)
                throw new InvalidOperationException("Embedding count did not match chunk count.");
            if (embedded.Dimensions != 64)
                throw new InvalidOperationException(
                    $"Embedding provider returned {embedded.Dimensions} dimensions; ICEHOTT expects 64.");

            var now = clock.GetUtcNow();
            var chunks = chunkTexts
                .Select((text, index) =>
                    new KnowledgeChunk(
                        Guid.NewGuid(),
                        document.Id,
                        workspaceId,
                        index,
                        text,
                        now))
                .ToArray();

            await knowledge.ReplaceChunksAsync(
                workspaceId,
                document.Id,
                chunks,
                cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);

            var vectorEmbeddings = chunks
                .Select((chunk, index) =>
                    new VectorEmbedding(chunk.Id, embedded.Vectors[index]))
                .ToArray();

            await vectorStore.StoreManyAsync(
                workspaceId,
                vectorEmbeddings,
                cancellationToken);

            document.MarkReady(chunks.Length, clock.GetUtcNow());
            await unitOfWork.SaveChangesAsync(cancellationToken);

            stopwatch.Stop();
            RagTelemetry.IndexingDurationMs.Record(stopwatch.Elapsed.TotalMilliseconds);
            RagTelemetry.IndexedChunks.Record(chunks.LongLength);
            activity?.SetTag("rag.chunk_count", chunks.Length);
            activity?.SetTag("embedding.provider", embedded.Provider);
            activity?.SetTag("embedding.model", embedded.Model);
            activity?.SetTag("embedding.dimensions", embedded.Dimensions);
            activity?.SetStatus(ActivityStatusCode.Ok);

            return chunks.Length;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            stopwatch.Stop();
            RagTelemetry.IndexingFailures.Add(1);
            RagTelemetry.IndexingDurationMs.Record(stopwatch.Elapsed.TotalMilliseconds);
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            throw;
        }
    }

    private async Task<EmbeddedContent> EmbedInBatchesAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        const int batchSize = 64;
        var output = new List<IReadOnlyList<float>>(texts.Count);
        int? dimensions = null;
        string? provider = null;
        string? model = null;

        for (var start = 0; start < texts.Count; start += batchSize)
        {
            var batch = texts.Skip(start).Take(batchSize).ToArray();
            var reply = await embeddings.EmbedAsync(batch, cancellationToken);

            if (reply.Embeddings.Count != batch.Length)
                throw new InvalidOperationException(
                    "Embedding provider returned a different batch size.");

            dimensions ??= reply.Dimensions;
            provider ??= reply.Provider;
            model ??= reply.Model;

            if (reply.Dimensions != dimensions)
                throw new InvalidOperationException(
                    "Embedding dimensions changed between batches.");
            if (!string.Equals(reply.Provider, provider, StringComparison.Ordinal) ||
                !string.Equals(reply.Model, model, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Embedding provider/model changed between batches.");

            output.AddRange(reply.Embeddings);
        }

        return new EmbeddedContent(
            output,
            dimensions ?? 0,
            provider ?? "unknown",
            model ?? "unknown");
    }

    private sealed record EmbeddedContent(
        IReadOnlyList<IReadOnlyList<float>> Vectors,
        int Dimensions,
        string Provider,
        string Model);
}
