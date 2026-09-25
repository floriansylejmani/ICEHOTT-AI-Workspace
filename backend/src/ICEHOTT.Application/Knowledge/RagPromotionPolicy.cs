using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Knowledge;

namespace ICEHOTT.Application.Knowledge;

public sealed record RagPromotionRequirements(
    string DatasetVersion,
    double MinHitRateAtK,
    double MinMeanRecallAtK,
    double MinMeanPrecisionAtK,
    double MinCitationCorrectness,
    bool RequireOfflineSemanticEvidence,
    double MinGroundedness,
    double MinAnswerRelevance,
    double MinFaithfulness,
    double MinContextPrecision,
    double MinContextRecall)
{
    public static RagPromotionRequirements FoundationDefault { get; } =
        new(
            "rag-v1",
            1.0,
            1.0,
            1.0,
            1.0,
            RequireOfflineSemanticEvidence: false,
            MinGroundedness: 0.80,
            MinAnswerRelevance: 0.80,
            MinFaithfulness: 0.80,
            MinContextPrecision: 0.80,
            MinContextRecall: 0.80);
}

public sealed class RagPromotionPolicy(RagPromotionRequirements requirements)
{
    public void Validate(
        EmbeddingProfileDescriptor profile,
        RagEvaluationEvidence deterministicEvidence,
        RagEvaluationEvidence? offlineEvidence)
    {
        ValidateBinding(profile, deterministicEvidence);

        if (deterministicEvidence.EvaluationKind != RagEvaluationKind.Deterministic)
            throw new InvalidOperationException(
                "Deterministic promotion evidence has the wrong evaluation kind.");

        if (!string.Equals(
                deterministicEvidence.DatasetVersion,
                requirements.DatasetVersion,
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Expected dataset '{requirements.DatasetVersion}', but evidence used '{deterministicEvidence.DatasetVersion}'.");

        if (deterministicEvidence.TenantLeakageCount != 0)
            throw new InvalidOperationException(
                "Promotion evidence contains tenant leakage.");

        if (deterministicEvidence.HitRateAtK < requirements.MinHitRateAtK ||
            deterministicEvidence.MeanRecallAtK < requirements.MinMeanRecallAtK ||
            deterministicEvidence.MeanPrecisionAtK < requirements.MinMeanPrecisionAtK ||
            deterministicEvidence.CitationCorrectness < requirements.MinCitationCorrectness)
            throw new InvalidOperationException(
                "Deterministic RAG evaluation thresholds did not pass.");

        if (!requirements.RequireOfflineSemanticEvidence)
            return;

        if (offlineEvidence is null)
            throw new InvalidOperationException(
                "Offline semantic evaluation evidence is required for promotion.");

        ValidateBinding(profile, offlineEvidence);

        if (offlineEvidence.EvaluationKind != RagEvaluationKind.OfflineSemantic)
            throw new InvalidOperationException(
                "Offline evidence has the wrong evaluation kind.");

        if (!string.Equals(
                offlineEvidence.DatasetVersion,
                requirements.DatasetVersion,
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Expected offline dataset '{requirements.DatasetVersion}', but evidence used '{offlineEvidence.DatasetVersion}'.");

        if (offlineEvidence.TenantLeakageCount != 0)
            throw new InvalidOperationException(
                "Offline promotion evidence contains tenant leakage.");

        RequireSemanticMetric(
            offlineEvidence.Groundedness,
            requirements.MinGroundedness,
            nameof(offlineEvidence.Groundedness));
        RequireSemanticMetric(
            offlineEvidence.AnswerRelevance,
            requirements.MinAnswerRelevance,
            nameof(offlineEvidence.AnswerRelevance));
        RequireSemanticMetric(
            offlineEvidence.Faithfulness,
            requirements.MinFaithfulness,
            nameof(offlineEvidence.Faithfulness));
        RequireSemanticMetric(
            offlineEvidence.ContextPrecision,
            requirements.MinContextPrecision,
            nameof(offlineEvidence.ContextPrecision));
        RequireSemanticMetric(
            offlineEvidence.ContextRecall,
            requirements.MinContextRecall,
            nameof(offlineEvidence.ContextRecall));
    }

    private static void ValidateBinding(
        EmbeddingProfileDescriptor profile,
        RagEvaluationEvidence evidence)
    {
        if (evidence.EmbeddingProfileId != profile.Id ||
            !string.Equals(evidence.Provider, profile.Provider, StringComparison.Ordinal) ||
            !string.Equals(evidence.Model, profile.Model, StringComparison.Ordinal) ||
            evidence.Dimensions != profile.Dimensions ||
            evidence.IndexVersion != profile.IndexVersion)
            throw new InvalidOperationException(
                "Evaluation evidence is not bound to the candidate embedding profile.");
    }

    private static void RequireSemanticMetric(
        double? actual,
        double minimum,
        string name)
    {
        if (actual is null || actual.Value < minimum)
            throw new InvalidOperationException(
                $"Offline semantic metric {name} did not meet the promotion threshold.");
    }
}
