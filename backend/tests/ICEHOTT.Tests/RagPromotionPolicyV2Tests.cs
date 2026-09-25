using ICEHOTT.Application.Abstractions;
using ICEHOTT.Application.Knowledge;
using ICEHOTT.Domain.Knowledge;

namespace ICEHOTT.Tests;

public sealed class RagPromotionPolicyV2Tests
{
    [Fact]
    public void Provider_Benchmark_Default_Accepts_RagV2_Thresholds_With_Offline_Evidence()
    {
        var profile = Profile();
        var policy = new RagPromotionPolicy(
            RagPromotionRequirements.ProviderBenchmarkDefault);

        var deterministic = Evidence(
            profile,
            "rag-v2",
            RagEvaluationKind.Deterministic,
            hit: 0.95,
            recall: 0.95,
            precision: 0.95,
            citation: 1.0);

        var offline = new RagEvaluationEvidence(
            Guid.NewGuid(),
            "rag-v2",
            profile.Id,
            profile.Provider,
            profile.Model,
            profile.Dimensions,
            profile.IndexVersion,
            0.95,
            0.95,
            0.95,
            1.0,
            0,
            RagEvaluationKind.OfflineSemantic,
            "offline-v1",
            DateTimeOffset.UtcNow,
            groundedness: 0.80,
            answerRelevance: 0.80,
            faithfulness: 0.80,
            contextPrecision: 0.80,
            contextRecall: 0.80);

        policy.Validate(profile, deterministic, offline);
    }

    [Fact]
    public void Provider_Benchmark_Default_Rejects_RagV1_Evidence()
    {
        var profile = Profile();
        var policy = new RagPromotionPolicy(
            RagPromotionRequirements.ProviderBenchmarkDefault);

        var deterministic = Evidence(
            profile,
            "rag-v1",
            RagEvaluationKind.Deterministic,
            1.0,
            1.0,
            1.0,
            1.0);

        Assert.Throws<InvalidOperationException>(() =>
            policy.Validate(profile, deterministic, offlineEvidence: null));
    }

    private static EmbeddingProfileDescriptor Profile() =>
        new(
            Guid.NewGuid(),
            "openai-small-rag-v2-test",
            "openai",
            "text-embedding-3-small",
            1536,
            "1",
            2,
            "cosine",
            "unit");

    private static RagEvaluationEvidence Evidence(
        EmbeddingProfileDescriptor profile,
        string dataset,
        RagEvaluationKind kind,
        double hit,
        double recall,
        double precision,
        double citation) =>
        new(
            Guid.NewGuid(),
            dataset,
            profile.Id,
            profile.Provider,
            profile.Model,
            profile.Dimensions,
            profile.IndexVersion,
            hit,
            recall,
            precision,
            citation,
            0,
            kind,
            "test-runner",
            DateTimeOffset.UtcNow);
}
