using System.Diagnostics;
using ICEHOTT.Application.Abstractions;

namespace ICEHOTT.Application.Knowledge;

public sealed record EmbeddingBuildProgress(
    Guid? ProfileId,
    int EmbeddedThisPass,
    long ReadyChunkCount,
    long EmbeddedChunkCount,
    long MissingChunkCount,
    bool IsComplete)
{
    public static EmbeddingBuildProgress NoBuildingProfile { get; } =
        new(null, 0, 0, 0, 0, true);
}

public sealed class EmbeddingProfileBuildService(
    IBuildEmbeddingProfileResolver buildProfileResolver,
    IEmbeddingProviderRegistry providerRegistry,
    IVectorIndexProvisioner indexProvisioner,
    IEmbeddingBuildStore buildStore,
    IVectorStore vectorStore)
{
    private const int AppBatchCap = 512;

    public async Task<EmbeddingBuildProgress> BuildNextBatchAsync(
        int requestedBatchSize = AppBatchCap,
        CancellationToken cancellationToken = default)
    {
        var profile = await buildProfileResolver.ResolveAsync(cancellationToken);
        if (profile is null)
            return EmbeddingBuildProgress.NoBuildingProfile;

        profile.Validate();
        if (profile.Dimensions > 2000)
            throw new VectorStoreUnavailableException(
                "The current float32 pgvector HNSW serving path supports at most 2000 dimensions.",
                retryable: false);

        var provider = providerRegistry.Resolve(profile.Provider);
        ProfileKnowledgeRetriever.ValidateProviderCompatibility(provider, profile);

        var batchSize = Math.Clamp(
            Math.Min(requestedBatchSize, provider.Capabilities.MaxBatchInputs),
            1,
            AppBatchCap);

        await indexProvisioner.EnsureBuildIndexAsync(profile, cancellationToken);

        var missing = await buildStore.GetMissingReadyChunksAsync(
            profile.Id,
            batchSize,
            cancellationToken);

        if (missing.Count > 0)
        {
            using var activity = RagTelemetry.ActivitySource.StartActivity("rag.build_profile_batch");
            ProfileKnowledgeRetriever.SetProfileTags(activity, profile);
            activity?.SetTag("embedding.purpose", "document");
            activity?.SetTag("rag.build_batch_size", missing.Count);

            var stopwatch = Stopwatch.StartNew();

            foreach (var workspaceBatch in missing
                         .GroupBy(x => x.WorkspaceId)
                         .OrderBy(x => x.Key))
            {
                var workspaceChunks = workspaceBatch.ToArray();

                var reply = await provider.EmbedAsync(
                    profile,
                    EmbeddingPurpose.Document,
                    workspaceChunks.Select(x => x.Content).ToArray(),
                    cancellationToken);

                ValidateReply(profile, workspaceChunks.Length, reply);

                var workspaceItems = workspaceChunks
                    .Select((chunk, index) =>
                        new VectorEmbedding(
                            chunk.ChunkId,
                            reply.Embeddings[index]))
                    .ToArray();

                await vectorStore.StoreManyAsync(
                    workspaceBatch.Key,
                    profile,
                    workspaceItems,
                    cancellationToken);
            }

            stopwatch.Stop();
            RagTelemetry.IndexedChunks.Record(missing.Count);
            RagTelemetry.IndexingDurationMs.Record(stopwatch.Elapsed.TotalMilliseconds);
            activity?.SetStatus(ActivityStatusCode.Ok);
        }

        var coverage = await buildStore.GetCoverageAsync(
            profile.Id,
            cancellationToken);

        return new EmbeddingBuildProgress(
            profile.Id,
            missing.Count,
            coverage.ReadyChunkCount,
            coverage.EmbeddedChunkCount,
            coverage.MissingChunkCount,
            coverage.IsComplete);
    }

    public async Task<EmbeddingBuildProgress> BuildUntilCompleteAsync(
        int requestedBatchSize = AppBatchCap,
        int maxBatches = 10000,
        CancellationToken cancellationToken = default)
    {
        if (maxBatches <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBatches));

        EmbeddingBuildProgress progress = EmbeddingBuildProgress.NoBuildingProfile;

        for (var batch = 0; batch < maxBatches; batch++)
        {
            progress = await BuildNextBatchAsync(
                requestedBatchSize,
                cancellationToken);

            if (progress.IsComplete || progress.ProfileId is null)
                return progress;

            if (progress.EmbeddedThisPass == 0)
                throw new InvalidOperationException(
                    "Building profile still has missing chunks but no progress was made.");
        }

        throw new InvalidOperationException(
            $"Building profile did not complete within {maxBatches} batches.");
    }

    private static void ValidateReply(
        EmbeddingProfileDescriptor expectedProfile,
        int expectedCount,
        EmbeddingBatch reply)
    {
        if (reply.Profile != expectedProfile)
            throw new EmbeddingProviderException(
                "Embedding profile changed during build.",
                EmbeddingFailureKind.ProfileMismatch);

        if (reply.Embeddings.Count != expectedCount)
            throw new EmbeddingProviderException(
                "Embedding provider returned a different batch size during build.",
                EmbeddingFailureKind.ProfileMismatch);

        if (reply.Embeddings.Any(x => x.Count != expectedProfile.Dimensions))
            throw new EmbeddingProviderException(
                "Embedding provider returned incompatible dimensions during build.",
                EmbeddingFailureKind.ProfileMismatch);
    }
}
