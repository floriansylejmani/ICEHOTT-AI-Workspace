using System.Diagnostics;
using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Knowledge;

namespace ICEHOTT.Application.Knowledge;

public sealed record RagBenchmarkCase(
    string Id,
    string Query,
    IReadOnlyList<string> ExpectedSources,
    IReadOnlyList<string> ForbiddenSources,
    int TopK,
    IReadOnlyList<string>? TenantForbiddenSources = null,
    bool IsNegativeSafetyControl = false);

public sealed record RagBenchmarkThresholds(
    double HitRateAtK,
    double MeanRecallAtK,
    double MeanPrecisionAtK,
    double CitationCorrectness,
    int TenantLeakageCount);

public sealed record RagBenchmarkDataset(
    string Version,
    IReadOnlyList<RagBenchmarkCase> Cases,
    RagBenchmarkThresholds Thresholds);

public sealed record RagBenchmarkPricing(
    decimal UsdPerMillionInputTokens);

public sealed record RagBenchmarkResult(
    RagEvaluationEvidence Evidence,
    int? InputTokens,
    double DurationMs,
    decimal? EstimatedCostUsd,
    bool ThresholdsPassed);

public sealed class RagBenchmarkRunner(
    IProfileKnowledgeRetriever profileRetriever,
    IRagEvaluationEvidenceRepository evidenceRepository,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
{
    public async Task<RagBenchmarkResult> RunAsync(
        Guid workspaceId,
        EmbeddingProfileDescriptor profile,
        RagBenchmarkDataset dataset,
        string runnerVersion,
        RagBenchmarkPricing? pricing = null,
        CancellationToken cancellationToken = default)
    {
        if (workspaceId == Guid.Empty)
            throw new ArgumentException("Workspace ID is required.", nameof(workspaceId));
        if (string.IsNullOrWhiteSpace(dataset.Version))
            throw new ArgumentException("Dataset version is required.", nameof(dataset));
        if (dataset.Cases.Count == 0)
            throw new ArgumentException("Benchmark dataset has no cases.", nameof(dataset));
        if (string.IsNullOrWhiteSpace(runnerVersion))
            throw new ArgumentException("Runner version is required.", nameof(runnerVersion));

        profile.Validate();

        var observations = new List<RagEvaluationObservation>(dataset.Cases.Count);
        var stopwatch = Stopwatch.StartNew();
        int? totalInputTokens = 0;

        foreach (var testCase in dataset.Cases)
        {
            var result = await profileRetriever.RetrieveAsync(
                workspaceId,
                profile,
                testCase.Query,
                Math.Clamp(testCase.TopK, 1, 10),
                allowBuildingProfile: true,
                cancellationToken);

            var retrievedSources = result.Matches
                .Select(x => x.SourceName)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var expected = testCase.ExpectedSources
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var forbidden = testCase.ForbiddenSources
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var tenantForbidden = (testCase.TenantForbiddenSources ?? [])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var retrieved = retrievedSources
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var expectedSatisfied = expected.Count == 0
                ? testCase.IsNegativeSafetyControl || retrieved.Count == 0
                : expected.All(retrieved.Contains);

            var forbiddenAbsent =
                !retrieved.Any(forbidden.Contains);
            var tenantLeakage =
                retrieved.Any(tenantForbidden.Contains);

            observations.Add(new RagEvaluationObservation(
                testCase.Id,
                testCase.ExpectedSources,
                retrievedSources,
                CitationCorrect:
                    expectedSatisfied &&
                    forbiddenAbsent &&
                    !tenantLeakage,
                TenantLeakage: tenantLeakage,
                IsNegativeSafetyControl:
                    testCase.IsNegativeSafetyControl));

            if (totalInputTokens is not null)
            {
                if (result.Usage?.InputTokens is int inputTokens)
                    totalInputTokens += inputTokens;
                else
                    totalInputTokens = null;
            }
        }

        stopwatch.Stop();

        var summary = RagEvaluationMetrics.Summarize(observations);
        var evidence = new RagEvaluationEvidence(
            Guid.NewGuid(),
            dataset.Version,
            profile.Id,
            profile.Provider,
            profile.Model,
            profile.Dimensions,
            profile.IndexVersion,
            summary.HitRateAtK,
            summary.MeanRecallAtK,
            summary.MeanPrecisionAtK,
            summary.CitationCorrectness,
            summary.TenantLeakageCount,
            RagEvaluationKind.Deterministic,
            runnerVersion,
            clock.GetUtcNow());

        await evidenceRepository.AddAsync(
            evidence,
            cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        decimal? estimatedCost = null;
        if (pricing is not null &&
            totalInputTokens is int measuredTokens)
        {
            estimatedCost =
                measuredTokens *
                pricing.UsdPerMillionInputTokens /
                1_000_000m;
        }

        var thresholdsPassed =
            summary.HitRateAtK >= dataset.Thresholds.HitRateAtK &&
            summary.MeanRecallAtK >= dataset.Thresholds.MeanRecallAtK &&
            summary.MeanPrecisionAtK >= dataset.Thresholds.MeanPrecisionAtK &&
            summary.CitationCorrectness >= dataset.Thresholds.CitationCorrectness &&
            summary.TenantLeakageCount <= dataset.Thresholds.TenantLeakageCount;

        return new RagBenchmarkResult(
            evidence,
            totalInputTokens,
            stopwatch.Elapsed.TotalMilliseconds,
            estimatedCost,
            thresholdsPassed);
    }
}
