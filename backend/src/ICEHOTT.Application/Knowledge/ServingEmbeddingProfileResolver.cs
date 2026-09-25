using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Knowledge;

namespace ICEHOTT.Application.Knowledge;

/// <summary>
/// Resolves the Active serving profile from the repository.
/// Every retrieval/query path must use this resolver exclusively.
/// Building and Retired profiles are never returned.
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
                "No Active embedding profile exists. " +
                "The deployment requires exactly one Active profile before serving requests.");

        var descriptor = ToDescriptor(profile);
        descriptor.Validate();
        return descriptor;
    }

    internal static EmbeddingProfileDescriptor ToDescriptor(EmbeddingProfile profile) =>
        new(profile.Id,
            profile.Key,
            profile.Provider,
            profile.Model,
            profile.Dimensions,
            profile.Version,
            profile.IndexVersion,
            profile.DistanceMetric,
            profile.Normalization);
}
