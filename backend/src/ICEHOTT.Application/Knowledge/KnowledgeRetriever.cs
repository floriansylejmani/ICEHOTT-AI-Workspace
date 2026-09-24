using System.Diagnostics;
using ICEHOTT.Application.Abstractions;

namespace ICEHOTT.Application.Knowledge;

public sealed class KnowledgeRetriever(
    IEmbeddingProvider embeddings,
    IVectorStore vectorStore,
    IRagReranker reranker,
    IRetrievedContentPolicy contentPolicy) : IKnowledgeRetriever
{
    public async Task<KnowledgeRetrievalResult> RetrieveAsync(
        Guid workspaceId,
        string query,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var normalizedQuery = query.Trim();
        if (string.IsNullOrWhiteSpace(normalizedQuery))
            return new([], 0, 0);

        var finalLimit = Math.Clamp(limit, 1, 10);
        var candidateLimit = Math.Clamp(finalLimit * 4, 8, 30);
        var stopwatch = Stopwatch.StartNew();

        using var activity = RagTelemetry.ActivitySource.StartActivity("rag.retrieve");
        activity?.SetTag("workspace.id", workspaceId);
        activity?.SetTag("rag.limit", finalLimit);
        activity?.SetTag("rag.candidate_limit", candidateLimit);

        RagTelemetry.RetrievalRequests.Add(1);

        var embeddingBatch = await embeddings.EmbedAsync([normalizedQuery], cancellationToken);
        if (embeddingBatch.Embeddings.Count != 1)
            throw new InvalidOperationException("Embedding provider returned an invalid query batch.");

        var candidates = await vectorStore.SearchAsync(
            workspaceId,
            normalizedQuery,
            embeddingBatch.Embeddings[0],
            candidateLimit,
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
        activity?.SetTag("embedding.provider", embeddingBatch.Provider);
        activity?.SetTag("embedding.model", embeddingBatch.Model);

        return new(reranked, candidates.Count, filtered);
    }
}
