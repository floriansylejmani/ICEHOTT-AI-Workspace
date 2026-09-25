using ICEHOTT.Application.Abstractions;

namespace ICEHOTT.Application.Knowledge;

public sealed class KnowledgeRetriever(
    IServingEmbeddingProfileResolver servingProfileResolver,
    IProfileKnowledgeRetriever profileRetriever) : IKnowledgeRetriever
{
    public async Task<KnowledgeRetrievalResult> RetrieveAsync(
        Guid workspaceId,
        string query,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var profile = await servingProfileResolver.ResolveAsync(cancellationToken);

        return await profileRetriever.RetrieveAsync(
            workspaceId,
            profile,
            query,
            limit,
            allowBuildingProfile: false,
            cancellationToken);
    }
}
