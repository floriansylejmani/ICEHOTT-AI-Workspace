namespace ICEHOTT.Application.Abstractions;

public sealed record KnowledgeRetrievalResult(
    IReadOnlyList<KnowledgeMatch> Matches,
    int CandidateCount,
    int FilteredCount,
    EmbeddingUsage? Usage = null,
    double? EmbeddingLatencyMs = null);

public interface IKnowledgeRetriever
{
    Task<KnowledgeRetrievalResult> RetrieveAsync(
        Guid workspaceId,
        string query,
        int limit,
        CancellationToken cancellationToken = default);
}
