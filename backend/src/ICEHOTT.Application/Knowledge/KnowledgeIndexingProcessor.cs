using System.Diagnostics;
using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Knowledge;

namespace ICEHOTT.Application.Knowledge;

public sealed class KnowledgeIndexingProcessor(
    IKnowledgeRepository knowledge,
    IKnowledgeChunker chunker,
    IServingEmbeddingProfileResolver servingProfiles,
    IEmbeddingProviderRegistry embeddingProviders,
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

            var configuredProfile = await servingProfiles.ResolveAsync(cancellationToken);
            configuredProfile.Validate();

            var provider = embeddingProviders.Resolve(configuredProfile.Provider);
            provider.Capabilities.ValidateProfile(configuredProfile);

            document.MarkProcessing();
            await unitOfWork.SaveChangesAsync(cancellationToken);

            var chunkTexts = chunker.Chunk(document.Content);
            if (chunkTexts.Count == 0)
                throw new InvalidOperationException("Knowledge document produced no indexable chunks.");

            var embedded = await EmbedInBatchesAsync(
                provider,
                configuredProfile,
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
        IEmbeddingProvider provider,
        EmbeddingProfileDescriptor expectedProfile,
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        var batchSize = Math.Clamp(provider.Capabilities.MaxBatchInputs, 1, 256);
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
