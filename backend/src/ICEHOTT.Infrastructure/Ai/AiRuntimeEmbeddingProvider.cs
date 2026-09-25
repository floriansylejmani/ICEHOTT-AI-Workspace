using ICEHOTT.Application.Abstractions;

namespace ICEHOTT.Infrastructure.Ai;

/// <summary>
/// Embedding provider backed by the local ICEHOTT AI runtime service.
/// Produces 64-dimensional deterministic vectors. Provider name "icehott-ai-runtime".
/// This is the Phase 3.6A baseline provider; it does not support purpose routing.
/// </summary>
public sealed class AiRuntimeEmbeddingProvider :
    IEmbeddingProvider,
    IEmbeddingProviderConfigurationProbe
{
    private const string ProviderName = "icehott-ai-runtime";
    private const string SupportedModel = "deterministic-64d";
    private static readonly IReadOnlySet<int> SupportedDims = new HashSet<int> { 64 };

    private readonly IAiRuntimeClient _runtime;

    public AiRuntimeEmbeddingProvider(IAiRuntimeClient runtime)
    {
        _runtime = runtime;
    }

    public string Provider => ProviderName;

    public EmbeddingProviderCapabilities Capabilities { get; } = new(
        ProviderName,
        SupportedDims,
        MaxBatchInputs: 64,
        MaxInputTokens: null,
        SupportsPurposeRouting: false);

    public bool IsConfigured(EmbeddingProfileDescriptor profile)
    {
        try
        {
            profile.Validate();
            return
                string.Equals(
                    profile.Provider,
                    ProviderName,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    profile.Model,
                    SupportedModel,
                    StringComparison.Ordinal) &&
                SupportedDims.Contains(profile.Dimensions);
        }
        catch
        {
            return false;
        }
    }

    public async Task<EmbeddingBatch> EmbedAsync(
        EmbeddingProfileDescriptor profile,
        EmbeddingPurpose purpose,
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        // Pre-flight: provider name must match profile
        if (!string.Equals(profile.Provider, ProviderName, StringComparison.OrdinalIgnoreCase))
            throw new EmbeddingProviderException(
                $"Provider name mismatch: profile '{profile.Key}' targets provider '{profile.Provider}' " +
                $"but this provider is '{ProviderName}'. This error is non-retryable.",
                EmbeddingFailureKind.ProfileMismatch);

        if (!string.Equals(
                profile.Model,
                SupportedModel,
                StringComparison.Ordinal))
            throw new EmbeddingProviderException(
                $"Provider '{ProviderName}' supports only model '{SupportedModel}', but profile '{profile.Key}' requires '{profile.Model}'.",
                EmbeddingFailureKind.ProfileMismatch);

        // Pre-flight: dimensions must be in supported set
        if (!Capabilities.SupportedDimensions.Contains(profile.Dimensions))
            throw new EmbeddingProviderException(
                $"Provider '{ProviderName}' does not support {profile.Dimensions} dimensions. " +
                $"Supported: [{string.Join(", ", Capabilities.SupportedDimensions)}]. " +
                "This error is non-retryable.",
                EmbeddingFailureKind.ProfileMismatch);

        if (texts.Count == 0)
            return new EmbeddingBatch(profile, []);

        try
        {
            var reply = await _runtime.EmbedAsync(texts, cancellationToken);

            if (reply.Dimensions != profile.Dimensions)
                throw new EmbeddingProviderException(
                    $"AI runtime returned {reply.Dimensions} dimensions but profile '{profile.Key}' " +
                    $"requires {profile.Dimensions}. This error is non-retryable.",
                    EmbeddingFailureKind.ProfileMismatch);

            if (reply.Embeddings.Any(vector => vector.Count != profile.Dimensions))
                throw new EmbeddingProviderException(
                    $"AI runtime returned at least one vector incompatible with profile '{profile.Key}'.",
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
