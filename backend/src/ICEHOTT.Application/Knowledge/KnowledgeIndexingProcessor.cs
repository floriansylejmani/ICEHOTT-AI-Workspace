using System.Diagnostics;
using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Knowledge;

namespace ICEHOTT.Application.Knowledge;

public sealed class KnowledgeIndexingProcessor(
    IKnowledgeRepository knowledge,
    IKnowledgeChunker chunker,
    IServingEmbeddingProfileResolver servingProfileResolver,
    IEmbeddingProviderRegistry providerRegistry,
    IVectorStore vectorStore,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
{
    // Application-level batch cap. Effective batch = min(AppBatchCap, provider.MaxBatchInputs).
    // This separates the application limit from the per-provider limit introduced in Phase 3.6B.
    private const int AppBatchCap = 512;

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

            // Ordinary ingestion always targets the Active serving profile.
            // Building-profile migrations have a separate stable-chunk build path so
            // ReplaceChunks cannot cascade-delete vectors that are still serving traffic.
            var configuredProfile =
                await servingProfileResolver.ResolveAsync(cancellationToken);

            configuredProfile.Validate();
            activity?.SetTag("rag.profile_kind", "serving");

            var provider = providerRegistry.Resolve(configuredProfile.Provider);
            var effectiveBatchSize = Math.Min(AppBatchCap, provider.Capabilities.MaxBatchInputs);

            document.MarkProcessing();
            await unitOfWork.SaveChangesAsync(cancellationToken);

            var chunkTexts = chunker.Chunk(document.Content);
            if (chunkTexts.Count == 0)
                throw new InvalidOperationException("Knowledge document produced no indexable chunks.");

            var embedded = await EmbedInBatchesAsync(
                configuredProfile,
                provider,
                effectiveBatchSize,
                chunkTexts,
                cancellationToken);

            if (embedded.Embeddings.Count != chunkTexts.Count)
                throw new InvalidOperationException("Embedding count did not match chunk count.");

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
                    new VectorEmbedding(chunk.Id, embedded.Embeddings[index]))
                .ToArray();

            await vectorStore.StoreManyAsync(
                workspaceId,
                embedded.Profile,
                vectorEmbeddings,
                cancellationToken);

            document.MarkReady(chunks.Length, clock.GetUtcNow());
            await unitOfWork.SaveChangesAsync(cancellationToken);

            stopwatch.Stop();
            RagTelemetry.IndexingDurationMs.Record(stopwatch.Elapsed.TotalMilliseconds);
            RagTelemetry.IndexedChunks.Record(chunks.LongLength);
            activity?.SetTag("rag.chunk_count", chunks.Length);
            activity?.SetTag("rag.effective_batch_size", effectiveBatchSize);
            SetProfileTags(activity, embedded.Profile);
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

    private static async Task<EmbeddingBatch> EmbedInBatchesAsync(
        EmbeddingProfileDescriptor expectedProfile,
        IEmbeddingProvider provider,
        int batchSize,
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        var output = new List<IReadOnlyList<float>>(texts.Count);

        for (var start = 0; start < texts.Count; start += batchSize)
        {
            var batch = texts.Skip(start).Take(batchSize).ToArray();
            var reply = await provider.EmbedAsync(
                expectedProfile,
                EmbeddingPurpose.Document,
                batch,
                cancellationToken);

            if (reply.Embeddings.Count != batch.Length)
                throw new EmbeddingProviderException(
                    "Embedding provider returned a different batch size.",
                    EmbeddingFailureKind.ProfileMismatch);

            reply.Profile.Validate();
            if (reply.Profile != expectedProfile)
                throw new EmbeddingProviderException(
                    "Embedding profile changed during document indexing.",
                    EmbeddingFailureKind.ProfileMismatch);

            if (reply.Embeddings.Any(vector => vector.Count != expectedProfile.Dimensions))
                throw new EmbeddingProviderException(
                    $"Embedding vector dimensions did not match profile {expectedProfile.Key}.",
                    EmbeddingFailureKind.ProfileMismatch);

            output.AddRange(reply.Embeddings);
        }

        return new EmbeddingBatch(expectedProfile, output);
    }

    private static void SetProfileTags(
        Activity? activity,
        EmbeddingProfileDescriptor profile)
    {
        activity?.SetTag("embedding.profile_id", profile.Id);
        activity?.SetTag("embedding.profile", profile.Key);
        activity?.SetTag("embedding.provider", profile.Provider);
        activity?.SetTag("embedding.model", profile.Model);
        activity?.SetTag("embedding.version", profile.Version);
        activity?.SetTag("embedding.index_version", profile.IndexVersion);
        activity?.SetTag("embedding.dimensions", profile.Dimensions);
    }
}
