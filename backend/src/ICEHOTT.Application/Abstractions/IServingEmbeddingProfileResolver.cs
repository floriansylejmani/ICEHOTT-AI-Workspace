namespace ICEHOTT.Application.Abstractions;

/// <summary>
/// Resolves the deployment-wide Active embedding profile for retrieval/serving requests.
/// Only returns a profile with status Active; throws if none exists.
/// Must never be used by build/reindex operations.
/// </summary>
public interface IServingEmbeddingProfileResolver
{
    Task<EmbeddingProfileDescriptor> ResolveAsync(CancellationToken cancellationToken = default);
}
