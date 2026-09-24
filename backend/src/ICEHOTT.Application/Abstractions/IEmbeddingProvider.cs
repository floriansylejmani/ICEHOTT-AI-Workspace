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
            throw new InvalidOperationException("Phase 3.6A currently supports cosine distance only.");
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

public sealed record EmbeddingBatch(
    EmbeddingProfileDescriptor Profile,
    IReadOnlyList<IReadOnlyList<float>> Embeddings)
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

public interface IEmbeddingProvider
{
    EmbeddingProfileDescriptor Profile { get; }

    Task<EmbeddingBatch> EmbedAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default);
}
