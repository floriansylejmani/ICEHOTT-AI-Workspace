namespace ICEHOTT.Domain.Knowledge;

public enum RagEvaluationKind
{
    Deterministic = 1,
    OfflineSemantic = 2
}

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
        RagEvaluationKind evaluationKind,
        string runnerVersion,
        DateTimeOffset completedAtUtc,
        double? groundedness = null,
        double? answerRelevance = null,
        double? faithfulness = null,
        double? contextPrecision = null,
        double? contextRecall = null)
    {
        if (id == Guid.Empty) throw new ArgumentException("Evidence ID is required.", nameof(id));
        if (embeddingProfileId == Guid.Empty)
            throw new ArgumentException("Embedding profile ID is required.", nameof(embeddingProfileId));
        if (string.IsNullOrWhiteSpace(datasetVersion))
            throw new ArgumentException("Dataset version is required.", nameof(datasetVersion));
        if (string.IsNullOrWhiteSpace(provider))
            throw new ArgumentException("Provider is required.", nameof(provider));
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(model));
        if (dimensions <= 0 || dimensions > 16000)
            throw new ArgumentOutOfRangeException(nameof(dimensions));
        if (indexVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(indexVersion));
        ValidateMetric(hitRateAtK, nameof(hitRateAtK));
        ValidateMetric(meanRecallAtK, nameof(meanRecallAtK));
        ValidateMetric(meanPrecisionAtK, nameof(meanPrecisionAtK));
        ValidateMetric(citationCorrectness, nameof(citationCorrectness));
        if (tenantLeakageCount < 0)
            throw new ArgumentOutOfRangeException(nameof(tenantLeakageCount));
        if (string.IsNullOrWhiteSpace(runnerVersion))
            throw new ArgumentException("Runner version is required.", nameof(runnerVersion));
        ValidateOptionalMetric(groundedness, nameof(groundedness));
        ValidateOptionalMetric(answerRelevance, nameof(answerRelevance));
        ValidateOptionalMetric(faithfulness, nameof(faithfulness));
        ValidateOptionalMetric(contextPrecision, nameof(contextPrecision));
        ValidateOptionalMetric(contextRecall, nameof(contextRecall));

        Id = id;
        DatasetVersion = datasetVersion.Trim();
        EmbeddingProfileId = embeddingProfileId;
        Provider = provider.Trim();
        Model = model.Trim();
        Dimensions = dimensions;
        IndexVersion = indexVersion;
        HitRateAtK = hitRateAtK;
        MeanRecallAtK = meanRecallAtK;
        MeanPrecisionAtK = meanPrecisionAtK;
        CitationCorrectness = citationCorrectness;
        TenantLeakageCount = tenantLeakageCount;
        EvaluationKind = evaluationKind;
        RunnerVersion = runnerVersion.Trim();
        CompletedAtUtc = completedAtUtc;
        Groundedness = groundedness;
        AnswerRelevance = answerRelevance;
        Faithfulness = faithfulness;
        ContextPrecision = contextPrecision;
        ContextRecall = contextRecall;
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
    public RagEvaluationKind EvaluationKind { get; private set; }
    public string RunnerVersion { get; private set; } = string.Empty;
    public DateTimeOffset CompletedAtUtc { get; private set; }
    public double? Groundedness { get; private set; }
    public double? AnswerRelevance { get; private set; }
    public double? Faithfulness { get; private set; }
    public double? ContextPrecision { get; private set; }
    public double? ContextRecall { get; private set; }

    private static void ValidateMetric(double value, string parameterName)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value is < 0 or > 1)
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Evaluation metrics must be finite values between 0 and 1.");
    }

    private static void ValidateOptionalMetric(
        double? value,
        string parameterName)
    {
        if (value is not null)
            ValidateMetric(value.Value, parameterName);
    }
}
