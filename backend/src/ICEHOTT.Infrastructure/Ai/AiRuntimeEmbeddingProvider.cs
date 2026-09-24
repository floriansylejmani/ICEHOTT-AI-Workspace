using ICEHOTT.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace ICEHOTT.Infrastructure.Ai;

public sealed class AiRuntimeEmbeddingProvider : IEmbeddingProvider
{
    private readonly IAiRuntimeClient _runtime;
    private readonly EmbeddingProfileDescriptor _configuredProfile;

    public AiRuntimeEmbeddingProvider(
        IAiRuntimeClient runtime,
        IOptions<AiRuntimeOptions> options)
    {
        _runtime = runtime;
        var configured = options.Value;

        _configuredProfile = new EmbeddingProfileDescriptor(
            configured.EmbeddingProfileId,
            configured.EmbeddingProfileKey,
            configured.EmbeddingProvider,
            configured.EmbeddingModel,
            configured.EmbeddingDimensions,
            configured.EmbeddingVersion,
            configured.EmbeddingIndexVersion,
            configured.EmbeddingDistanceMetric,
            configured.EmbeddingNormalization);

        _configuredProfile.Validate();

        Provider = _configuredProfile.Provider;
        Capabilities = new EmbeddingProviderCapabilities(
            Provider,
            new HashSet<int> { _configuredProfile.Dimensions },
            64,
            null,
            false);
    }

    public string Provider { get; }
    public EmbeddingProviderCapabilities Capabilities { get; }

    public async Task<EmbeddingBatch> EmbedAsync(
        EmbeddingProfileDescriptor profile,
        EmbeddingPurpose purpose,
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        Capabilities.ValidateProfile(profile);

        if (profile != _configuredProfile)
            throw new EmbeddingProviderException(
                $"Embedding runtime is configured for profile {_configuredProfile.Key}, not {profile.Key}.",
                EmbeddingFailureKind.ProfileMismatch);

        if (texts.Count == 0)
            return new EmbeddingBatch(profile, []);

        try
        {
            var reply = await _runtime.EmbedAsync(texts, cancellationToken);

            if (reply.Dimensions != profile.Dimensions)
                throw new EmbeddingProviderException(
                    $"Embedding runtime returned {reply.Dimensions} dimensions, but profile {profile.Key} requires {profile.Dimensions}.",
                    EmbeddingFailureKind.ProfileMismatch);

            if (reply.Embeddings.Any(vector => vector.Count != profile.Dimensions))
                throw new EmbeddingProviderException(
                    $"Embedding runtime returned a vector incompatible with profile {profile.Key}.",
                    EmbeddingFailureKind.ProfileMismatch);

            return new EmbeddingBatch(profile, reply.Embeddings);
        }
        catch (EmbeddingProviderException)
        {
            throw;
        }
        catch (AiRuntimeUnavailableException exception)
        {
            throw new EmbeddingProviderException(
                "Embedding runtime is unavailable.",
                EmbeddingFailureKind.Transient,
                exception);
        }
    }
}
