using System.Diagnostics;
using ICEHOTT.Application.Abstractions;

namespace ICEHOTT.Application.Knowledge;

public sealed class ProfileKnowledgeRetriever(
    IEmbeddingProviderRegistry providerRegistry,
    IVectorStore vectorStore,
    IRagReranker reranker,
    IRetrievedContentPolicy contentPolicy) : IProfileKnowledgeRetriever
{
    public async Task<KnowledgeRetrievalResult> RetrieveAsync(
        Guid workspaceId,
        EmbeddingProfileDescriptor profile,
        string query,
        int limit,
        bool allowBuildingProfile = false,
        CancellationToken cancellationToken = default)
    {
        var normalizedQuery = query.Trim();
        if (string.IsNullOrWhiteSpace(normalizedQuery))
            return new([], 0, 0);

        profile.Validate();

        var finalLimit = Math.Clamp(limit, 1, 10);
        var candidateLimit = Math.Clamp(finalLimit * 4, 8, 30);
        var stopwatch = Stopwatch.StartNew();

        using var activity = RagTelemetry.ActivitySource.StartActivity("rag.retrieve_profile");
        activity?.SetTag("workspace.id", workspaceId);
        activity?.SetTag("rag.limit", finalLimit);
        activity?.SetTag("rag.candidate_limit", candidateLimit);
        SetProfileTags(activity, profile);

        RagTelemetry.RetrievalRequests.Add(1);

        var provider = providerRegistry.Resolve(profile.Provider);
        ValidateProviderCompatibility(provider, profile);

        var embeddingStopwatch = Stopwatch.StartNew();
        var embeddingBatch = await provider.EmbedAsync(
            profile,
            EmbeddingPurpose.Query,
            [normalizedQuery],
            cancellationToken);
        embeddingStopwatch.Stop();

        if (embeddingBatch.Embeddings.Count != 1)
            throw new EmbeddingProviderException(
                "Embedding provider returned an invalid query batch.",
                EmbeddingFailureKind.ProfileMismatch);

        if (embeddingBatch.Profile != profile ||
            embeddingBatch.Embeddings[0].Count != profile.Dimensions)
            throw new EmbeddingProviderException(
                "Query embedding did not match the requested embedding profile.",
                EmbeddingFailureKind.ProfileMismatch);

        var candidates = await vectorStore.SearchAsync(
            workspaceId,
            profile,
            normalizedQuery,
            embeddingBatch.Embeddings[0],
            candidateLimit,
            allowBuildingProfile,
            cancellationToken);

        var safeCandidates = new List<KnowledgeMatch>(candidates.Count);
        var filtered = 0;

        foreach (var candidate in candidates)
        {
            if (candidate.Score < 0.10)
            {
                filtered++;
                continue;
            }

            var decision = contentPolicy.Evaluate(candidate.Content);
            if (!decision.Allowed)
            {
                filtered++;
                continue;
            }

            safeCandidates.Add(candidate);
        }

        var reranked = reranker.Rerank(normalizedQuery, safeCandidates, finalLimit);

        stopwatch.Stop();
        RagTelemetry.FilteredChunks.Add(filtered);
        RagTelemetry.RetrievalDurationMs.Record(stopwatch.Elapsed.TotalMilliseconds);
        RagTelemetry.RetrievalResults.Record(reranked.Count);

        activity?.SetTag("rag.candidates", candidates.Count);
        activity?.SetTag("rag.filtered", filtered);
        activity?.SetTag("rag.results", reranked.Count);
        activity?.SetTag("embedding.purpose", "query");

        return new(
            reranked,
            candidates.Count,
            filtered,
            embeddingBatch.Usage,
            embeddingStopwatch.Elapsed.TotalMilliseconds);
    }

    internal static void ValidateProviderCompatibility(
        IEmbeddingProvider provider,
        EmbeddingProfileDescriptor profile)
    {
        if (!string.Equals(
                provider.Provider,
                profile.Provider,
                StringComparison.OrdinalIgnoreCase))
            throw new EmbeddingProviderException(
                $"Provider '{provider.Provider}' does not match profile provider '{profile.Provider}'.",
                EmbeddingFailureKind.ProfileMismatch);

        if (!provider.Capabilities.SupportedDimensions.Contains(profile.Dimensions))
            throw new EmbeddingProviderException(
                $"Provider '{provider.Provider}' does not advertise support for {profile.Dimensions} dimensions.",
                EmbeddingFailureKind.ProfileMismatch);

        if (provider.Capabilities.MaxBatchInputs <= 0)
            throw new EmbeddingProviderException(
                $"Provider '{provider.Provider}' has an invalid MaxBatchInputs capability.",
                EmbeddingFailureKind.Configuration);
    }

    internal static void SetProfileTags(
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
