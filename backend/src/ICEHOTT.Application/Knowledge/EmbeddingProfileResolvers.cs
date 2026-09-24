using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Knowledge;

namespace ICEHOTT.Application.Knowledge;

/// <summary>
/// Strict serving resolver — returns only Active profiles.
/// Throws if no Active profile exists or if the repository
/// contract is violated and a non-Active profile is returned.
/// </summary>
public sealed class ServingEmbeddingProfileResolver(IEmbeddingProfileRepository repository)
    : IServingEmbeddingProfileResolver
{
    public async Task<EmbeddingProfileDescriptor> ResolveAsync(
        CancellationToken cancellationToken = default)
    {
        var profile = await repository.GetActiveAsync(cancellationToken);

        if (profile is null)
            throw new InvalidOperationException(
                "No Active embedding profile found. Serving requires exactly one Active profile.");

        if (profile.Status != EmbeddingProfileStatus.Active)
            throw new InvalidOperationException(
                $"Serving resolver received a profile with status {profile.Status}. " +
                "Only Active profiles may serve retrieval requests.");

        return ToDescriptor(profile);
    }

    private static EmbeddingProfileDescriptor ToDescriptor(EmbeddingProfile profile) =>
        new(
            profile.Id,
            profile.Key,
            profile.Provider,
            profile.Model,
            profile.Dimensions,
            profile.Version,
            profile.IndexVersion,
            profile.DistanceMetric,
            profile.Normalization);
}

/// <summary>
/// Strict build resolver — returns only Building profiles.
/// Throws if no Building profile exists or if the repository
/// contract is violated and a non-Building profile is returned.
/// </summary>
public sealed class BuildEmbeddingProfileResolver(IEmbeddingProfileRepository repository)
    : IBuildEmbeddingProfileResolver
{
    public async Task<EmbeddingProfileDescriptor> ResolveAsync(
        CancellationToken cancellationToken = default)
    {
        var profile = await repository.GetBuildingAsync(cancellationToken);

        if (profile is null)
            throw new InvalidOperationException(
                "No Building embedding profile found. Build operations require a Building profile.");

        if (profile.Status != EmbeddingProfileStatus.Building)
            throw new InvalidOperationException(
                $"Build resolver received a profile with status {profile.Status}. " +
                "Only Building profiles may be used for build operations.");

        return ToDescriptor(profile);
    }

    private static EmbeddingProfileDescriptor ToDescriptor(EmbeddingProfile profile) =>
        new(
            profile.Id,
            profile.Key,
            profile.Provider,
            profile.Model,
            profile.Dimensions,
            profile.Version,
            profile.IndexVersion,
            profile.DistanceMetric,
            profile.Normalization);
}
