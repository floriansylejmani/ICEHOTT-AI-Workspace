namespace ICEHOTT.Application.Abstractions;

public interface IRagReranker
{
    IReadOnlyList<KnowledgeMatch> Rerank(
        string query,
        IReadOnlyList<KnowledgeMatch> candidates,
        int limit);
}
