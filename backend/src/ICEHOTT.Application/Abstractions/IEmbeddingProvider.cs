namespace ICEHOTT.Application.Abstractions;

public sealed record EmbeddingProfileDescriptor(
    Guid Id,
    string Key,
    string Provider,
    string Model,
    int Dimensions,
    string Version,
    int IndexVersion,
    string DistanceMetric,
    string Normalization)
{
    public void Validate()
    {
        if (Id == Guid.Empty) throw new InvalidOperationException("Embedding profile ID is required.");
        if (string.IsNullOrWhiteSpace(Key)) throw new InvalidOperationException("Embedding profile key is required.");
        if (string.IsNullOrWhiteSpace(Provider)) throw new InvalidOperationException("Embedding provider is required.");
        if (string.IsNullOrWhiteSpace(Model)) throw new InvalidOperationException("Embedding model is required.");
        if (Dimensions <= 0 || Dimensions > 16000)
            throw new InvalidOperationException(
                "Embedding dimensions must be between 1 and 16000.");
        if (IndexVersion <= 0) throw new InvalidOperationException("Embedding index version must be positive.");
        if (!string.Equals(DistanceMetric, "cosine", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Currently only cosine distance metric is supported.");
    }
}

public static class EmbeddingProfileDefaults
{
    public static readonly Guid LocalDeterministic64Id =
        Guid.Parse("3f36a640-0d25-4a76-9d0d-640000000001");

    public static EmbeddingProfileDescriptor LocalDeterministic64 { get; } =
        new(
            LocalDeterministic64Id,
            "local-deterministic-64-v1",
            "icehott-ai-runtime",
            "deterministic-64d",
            64,
            "1",
            1,
            "cosine",
            "unit");
}

public sealed record EmbeddingUsage(
    int? InputTokens,
    int? TotalTokens);

public sealed record EmbeddingBatch(
    EmbeddingProfileDescriptor Profile,
    IReadOnlyList<IReadOnlyList<float>> Embeddings,
    EmbeddingUsage? Usage = null)
{
    public int Dimensions => Profile.Dimensions;
    public string Provider => Profile.Provider;
    public string Model => Profile.Model;
}

public enum EmbeddingFailureKind
{
    Transient = 1,
    RateLimited = 2,
    Authentication = 3,
    Configuration = 4,
    InvalidInput = 5,
    ProfileMismatch = 6,
    Permanent = 7
}

public sealed class EmbeddingProviderException(
    string message,
    EmbeddingFailureKind kind,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    public EmbeddingFailureKind Kind { get; } = kind;

    public bool IsRetryable =>
        Kind is EmbeddingFailureKind.Transient or EmbeddingFailureKind.RateLimited;
}

/// <summary>
/// Embedding purpose passed to every provider call.
/// Providers that support purpose routing (SupportsPurposeRouting=true) may use
/// asymmetric query/document models; others apply the same model for both.
/// </summary>
public enum EmbeddingPurpose
{
    Document = 1,
    Query = 2
}

/// <summary>
/// Static capabilities of an embedding provider/model combination.
/// Used to compute effective batch size and validate profiles before any network call.
/// </summary>
public sealed record EmbeddingProviderCapabilities(
    string Provider,
    IReadOnlySet<int> SupportedDimensions,
    int MaxBatchInputs,
    int? MaxInputTokens,
    bool SupportsPurposeRouting);

/// <summary>
/// Stateless embedding provider abstraction.
/// The provider does NOT own a profile; the profile is passed per call.
/// Provider name and capabilities are fixed at registration time.
/// </summary>
public interface IEmbeddingProvider
{
    /// <summary>The stable provider name that must match EmbeddingProfile.Provider.</summary>
    string Provider { get; }

    /// <summary>Static capability descriptor for this provider/model.</summary>
    EmbeddingProviderCapabilities Capabilities { get; }

    /// <summary>
    /// Embed <paramref name="texts"/> using the given <paramref name="profile"/> and
    /// <paramref name="purpose"/>. The provider validates that its name matches the
    /// profile and that the requested dimensions are supported before issuing any request.
    /// Provider/profile mismatch is non-retryable.
    /// </summary>
    Task<EmbeddingBatch> EmbedAsync(
        EmbeddingProfileDescriptor profile,
        EmbeddingPurpose purpose,
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Registry of embedding providers keyed by provider name.
/// Resolves the correct provider for a given profile; throws explicitly on unknown names.
/// </summary>
public interface IEmbeddingProviderConfigurationProbe
{
    bool IsConfigured(EmbeddingProfileDescriptor profile);
}

public interface IEmbeddingProviderRegistry
{
    /// <summary>
    /// Returns the registered provider for <paramref name="provider"/>.
    /// Throws <see cref="EmbeddingProviderException"/> with
    /// <see cref="EmbeddingFailureKind.Configuration"/> if no provider is registered.
    /// </summary>
    IEmbeddingProvider Resolve(string provider);
}
