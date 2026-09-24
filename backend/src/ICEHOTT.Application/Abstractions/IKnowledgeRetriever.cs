namespace ICEHOTT.Application.Abstractions;

public sealed record KnowledgeRetrievalResult(
    IReadOnlyList<KnowledgeMatch> Matches,
    int CandidateCount,
    int FilteredCount);

public interface IKnowledgeRetriever
{
    Task<KnowledgeRetrievalResult> RetrieveAsync(
        Guid workspaceId,
        string query,
        int limit,
        CancellationToken cancellationToken = default);
}
