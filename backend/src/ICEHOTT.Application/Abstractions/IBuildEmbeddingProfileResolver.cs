namespace ICEHOTT.Application.Abstractions;

/// <summary>
/// Resolves the current Building embedding profile for reindex/build operations.
/// Only returns a profile with status Building; throws if none exists.
/// Must never be used for retrieval/serving requests.
/// </summary>
public interface IBuildEmbeddingProfileResolver
{
    Task<EmbeddingProfileDescriptor> ResolveAsync(CancellationToken cancellationToken = default);
}
