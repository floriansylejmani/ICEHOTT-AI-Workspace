using ICEHOTT.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace ICEHOTT.Infrastructure.Ai;

public sealed class AiRuntimeEmbeddingProvider : IEmbeddingProvider
{
    private readonly IAiRuntimeClient _runtime;

    public AiRuntimeEmbeddingProvider(
        IAiRuntimeClient runtime,
        IOptions<AiRuntimeOptions> options)
    {
        _runtime = runtime;
        var configured = options.Value;

        Profile = new EmbeddingProfileDescriptor(
            configured.EmbeddingProfileId,
            configured.EmbeddingProfileKey,
            configured.EmbeddingProvider,
            configured.EmbeddingModel,
            configured.EmbeddingDimensions,
            configured.EmbeddingVersion,
            configured.EmbeddingIndexVersion,
            configured.EmbeddingDistanceMetric,
            configured.EmbeddingNormalization);

        Profile.Validate();
    }

    public EmbeddingProfileDescriptor Profile { get; }

    public async Task<EmbeddingBatch> EmbedAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        if (texts.Count == 0)
            return new EmbeddingBatch(Profile, []);

        try
        {
            var reply = await _runtime.EmbedAsync(texts, cancellationToken);

            if (reply.Dimensions != Profile.Dimensions)
                throw new EmbeddingProviderException(
                    $"Embedding runtime returned {reply.Dimensions} dimensions, but profile {Profile.Key} requires {Profile.Dimensions}.",
                    EmbeddingFailureKind.ProfileMismatch);

            if (reply.Embeddings.Any(vector => vector.Count != Profile.Dimensions))
                throw new EmbeddingProviderException(
                    $"Embedding runtime returned a vector incompatible with profile {Profile.Key}.",
                    EmbeddingFailureKind.ProfileMismatch);

            return new EmbeddingBatch(Profile, reply.Embeddings);
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
