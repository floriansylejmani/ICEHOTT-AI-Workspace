using ICEHOTT.Application.Abstractions;

namespace ICEHOTT.Infrastructure.Ai;

public sealed class AiRuntimeOptions
{
    public const string SectionName = "AiRuntime";

    public string BaseUrl { get; init; } = "http://localhost:8000";
    public int TimeoutSeconds { get; init; } = 30;

    public Guid EmbeddingProfileId { get; init; } =
        EmbeddingProfileDefaults.LocalDeterministic64Id;

    public string EmbeddingProfileKey { get; init; } =
        EmbeddingProfileDefaults.LocalDeterministic64.Key;

    public string EmbeddingProvider { get; init; } =
        EmbeddingProfileDefaults.LocalDeterministic64.Provider;

    public string EmbeddingModel { get; init; } =
        EmbeddingProfileDefaults.LocalDeterministic64.Model;

    public int EmbeddingDimensions { get; init; } =
        EmbeddingProfileDefaults.LocalDeterministic64.Dimensions;

    public string EmbeddingVersion { get; init; } =
        EmbeddingProfileDefaults.LocalDeterministic64.Version;

    public int EmbeddingIndexVersion { get; init; } =
        EmbeddingProfileDefaults.LocalDeterministic64.IndexVersion;

    public string EmbeddingDistanceMetric { get; init; } =
        EmbeddingProfileDefaults.LocalDeterministic64.DistanceMetric;

    public string EmbeddingNormalization { get; init; } =
        EmbeddingProfileDefaults.LocalDeterministic64.Normalization;
}
