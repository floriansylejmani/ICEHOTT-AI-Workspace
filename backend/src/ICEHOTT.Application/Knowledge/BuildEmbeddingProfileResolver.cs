using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Knowledge;

namespace ICEHOTT.Application.Knowledge;

/// <summary>
/// Resolves the Building profile from the repository for index-build/reindex paths.
/// Active and Retired profiles are never returned.
/// Returns null when no Building profile exists (Phase 3.6A transitional state).
/// </summary>
public sealed class BuildEmbeddingProfileResolver(IEmbeddingProfileRepository repository)
    : IBuildEmbeddingProfileResolver
{
    public async Task<EmbeddingProfileDescriptor?> ResolveAsync(
        CancellationToken cancellationToken = default)
    {
        var profile = await repository.GetBuildingAsync(cancellationToken);

        if (profile is null)
            return null;

        var descriptor = ServingEmbeddingProfileResolver.ToDescriptor(profile);
        descriptor.Validate();
        return descriptor;
    }
}
