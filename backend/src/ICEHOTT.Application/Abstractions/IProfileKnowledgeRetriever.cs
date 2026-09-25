namespace ICEHOTT.Application.Abstractions;

public interface IProfileKnowledgeRetriever
{
    Task<KnowledgeRetrievalResult> RetrieveAsync(
        Guid workspaceId,
        EmbeddingProfileDescriptor profile,
        string query,
        int limit,
        bool allowBuildingProfile = false,
        CancellationToken cancellationToken = default);
}
