namespace ICEHOTT.Application.Knowledge;

public sealed record RagEvaluationObservation(
    string CaseId,
    IReadOnlyList<string> ExpectedSources,
    IReadOnlyList<string> RetrievedSources,
    bool CitationCorrect,
    bool TenantLeakage);

public sealed record RagEvaluationSummary(
    int CaseCount,
    double HitRateAtK,
    double MeanRecallAtK,
    double MeanPrecisionAtK,
    double CitationCorrectness,
    int TenantLeakageCount);

public static class RagEvaluationMetrics
{
    public static RagEvaluationSummary Summarize(
        IReadOnlyList<RagEvaluationObservation> observations)
    {
        if (observations.Count == 0)
            return new RagEvaluationSummary(0, 0, 0, 0, 0, 0);

        double hits = 0;
        double recall = 0;
        double precision = 0;
        double citationCorrect = 0;
        var tenantLeakage = 0;

        foreach (var observation in observations)
        {
            var expected = observation.ExpectedSources
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var retrieved = observation.RetrievedSources
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var relevantRetrieved = retrieved.Count(expected.Contains);

            if (expected.Count == 0)
            {
                if (retrieved.Count == 0) hits++;
                recall += 1;
                precision += retrieved.Count == 0 ? 1 : 0;
            }
            else
            {
                if (relevantRetrieved > 0) hits++;
                recall += relevantRetrieved / (double)expected.Count;
                precision += retrieved.Count == 0
                    ? 0
                    : relevantRetrieved / (double)retrieved.Count;
            }

            if (observation.CitationCorrect) citationCorrect++;
            if (observation.TenantLeakage) tenantLeakage++;
        }

        return new RagEvaluationSummary(
            observations.Count,
            hits / observations.Count,
            recall / observations.Count,
            precision / observations.Count,
            citationCorrect / observations.Count,
            tenantLeakage);
    }
}
