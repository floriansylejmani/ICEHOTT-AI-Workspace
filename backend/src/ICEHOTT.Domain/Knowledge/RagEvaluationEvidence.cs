namespace ICEHOTT.Domain.Knowledge;

public sealed class RagEvaluationEvidence
{
    private RagEvaluationEvidence() { }

    public RagEvaluationEvidence(
        Guid id,
        string datasetVersion,
        Guid embeddingProfileId,
        string provider,
        string model,
        int dimensions,
        int indexVersion,
        double hitRateAtK,
        double meanRecallAtK,
        double meanPrecisionAtK,
        double citationCorrectness,
        int tenantLeakageCount,
        DateTimeOffset completedAtUtc,
        RagEvaluationKind kind,
        string runnerVersion)
    {
        if (id == Guid.Empty) throw new ArgumentException("Evidence ID is required.", nameof(id));
        if (embeddingProfileId == Guid.Empty) throw new ArgumentException("Profile ID is required.", nameof(embeddingProfileId));
        DatasetVersion = Required(datasetVersion, nameof(datasetVersion));
        Provider = Required(provider, nameof(provider));
        Model = Required(model, nameof(model));
        RunnerVersion = Required(runnerVersion, nameof(runnerVersion));
        if (dimensions <= 0 || dimensions > 16000) throw new ArgumentOutOfRangeException(nameof(dimensions));
        if (indexVersion <= 0) throw new ArgumentOutOfRangeException(nameof(indexVersion));
        ValidateMetric(hitRateAtK, nameof(hitRateAtK));
        ValidateMetric(meanRecallAtK, nameof(meanRecallAtK));
        ValidateMetric(meanPrecisionAtK, nameof(meanPrecisionAtK));
        ValidateMetric(citationCorrectness, nameof(citationCorrectness));
        if (tenantLeakageCount < 0) throw new ArgumentOutOfRangeException(nameof(tenantLeakageCount));

        Id = id;
        EmbeddingProfileId = embeddingProfileId;
        Dimensions = dimensions;
        IndexVersion = indexVersion;
        HitRateAtK = hitRateAtK;
        MeanRecallAtK = meanRecallAtK;
        MeanPrecisionAtK = meanPrecisionAtK;
        CitationCorrectness = citationCorrectness;
        TenantLeakageCount = tenantLeakageCount;
        CompletedAtUtc = completedAtUtc;
        Kind = kind;
    }

    public Guid Id { get; private set; }
    public string DatasetVersion { get; private set; } = string.Empty;
    public Guid EmbeddingProfileId { get; private set; }
    public string Provider { get; private set; } = string.Empty;
    public string Model { get; private set; } = string.Empty;
    public int Dimensions { get; private set; }
    public int IndexVersion { get; private set; }
    public double HitRateAtK { get; private set; }
    public double MeanRecallAtK { get; private set; }
    public double MeanPrecisionAtK { get; private set; }
    public double CitationCorrectness { get; private set; }
    public int TenantLeakageCount { get; private set; }
    public DateTimeOffset CompletedAtUtc { get; private set; }
    public RagEvaluationKind Kind { get; private set; }
    public string RunnerVersion { get; private set; } = string.Empty;

    private static string Required(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Value is required.", parameterName);
        return value.Trim();
    }
    private static void ValidateMetric(double value, string parameterName)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0 || value > 1)
            throw new ArgumentOutOfRangeException(parameterName, "Metric must be between 0 and 1.");
    }
}
