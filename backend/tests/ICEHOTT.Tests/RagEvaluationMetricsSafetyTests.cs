using ICEHOTT.Application.Knowledge;

namespace ICEHOTT.Tests;

public sealed class RagEvaluationMetricsSafetyTests
{
    [Fact]
    public void Negative_Safety_Control_Does_Not_Dilute_Retrieval_Metrics()
    {
        var summary = RagEvaluationMetrics.Summarize(
        [
            new RagEvaluationObservation(
                "positive",
                ["support.txt"],
                ["support.txt"],
                CitationCorrect: true,
                TenantLeakage: false),
            new RagEvaluationObservation(
                "prompt-injection",
                [],
                ["safe-fallback.txt"],
                CitationCorrect: true,
                TenantLeakage: false,
                IsNegativeSafetyControl: true)
        ]);

        Assert.Equal(2, summary.CaseCount);
        Assert.Equal(1, summary.HitRateAtK);
        Assert.Equal(1, summary.MeanRecallAtK);
        Assert.Equal(1, summary.MeanPrecisionAtK);
        Assert.Equal(1, summary.CitationCorrectness);
        Assert.Equal(0, summary.TenantLeakageCount);
    }

    [Fact]
    public void Negative_Safety_Control_Still_Counts_Citation_And_Tenant_Failures()
    {
        var summary = RagEvaluationMetrics.Summarize(
        [
            new RagEvaluationObservation(
                "prompt-injection",
                [],
                ["forbidden.txt"],
                CitationCorrect: false,
                TenantLeakage: true,
                IsNegativeSafetyControl: true)
        ]);

        Assert.Equal(0, summary.HitRateAtK);
        Assert.Equal(0, summary.MeanRecallAtK);
        Assert.Equal(0, summary.MeanPrecisionAtK);
        Assert.Equal(0, summary.CitationCorrectness);
        Assert.Equal(1, summary.TenantLeakageCount);
    }
}
