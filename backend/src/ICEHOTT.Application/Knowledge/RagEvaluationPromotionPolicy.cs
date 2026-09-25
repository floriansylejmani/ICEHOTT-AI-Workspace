using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Knowledge;

namespace ICEHOTT.Application.Knowledge;

public sealed record RagEvaluationPromotionPolicy(
    string RequiredDatasetVersion,
    double MinHitRateAtK,
    double MinMeanRecallAtK,
    double MinMeanPrecisionAtK,
    double MinCitationCorrectness,
    int MaxTenantLeakageCount)
{
    public static RagEvaluationPromotionPolicy StrictV1 { get; } =
        new("rag-v1", 1.0, 1.0, 1.0, 1.0, 0);

    public void Validate(
        RagEvaluationEvidence evidence,
        EmbeddingProfileDescriptor profile)
    {
        if (evidence.Kind != RagEvaluationKind.Deterministic)
            throw new InvalidOperationException(
                "Phase 3.6B foundation requires deterministic evaluation evidence.");
        if (!string.Equals(
                evidence.DatasetVersion,
                RequiredDatasetVersion,
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Evaluation dataset must be {RequiredDatasetVersion}.");

        if (evidence.EmbeddingProfileId != profile.Id ||
            !string.Equals(evidence.Provider, profile.Provider, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(evidence.Model, profile.Model, StringComparison.Ordinal) ||
            evidence.Dimensions != profile.Dimensions ||
            evidence.IndexVersion != profile.IndexVersion)
        {
            throw new InvalidOperationException(
                "Evaluation evidence does not match the candidate embedding profile.");
        }

        if (evidence.HitRateAtK < MinHitRateAtK ||
            evidence.MeanRecallAtK < MinMeanRecallAtK ||
            evidence.MeanPrecisionAtK < MinMeanPrecisionAtK ||
            evidence.CitationCorrectness < MinCitationCorrectness ||
            evidence.TenantLeakageCount > MaxTenantLeakageCount)
        {
            throw new InvalidOperationException(
                "Evaluation evidence did not satisfy promotion thresholds.");
        }
    }
}
