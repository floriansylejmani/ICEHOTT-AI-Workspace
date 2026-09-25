using System.Diagnostics;
using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Knowledge;

namespace ICEHOTT.Application.Knowledge;

public sealed record OfflineSemanticEvaluationCase(
    string Id,
    string Query,
    IReadOnlyList<string> ExpectedSources,
    IReadOnlyList<string> ForbiddenSources,
    IReadOnlyList<string> TenantForbiddenSources,
    IReadOnlyList<string> ReferenceFacts,
    int TopK,
    bool IsNegativeSafetyControl = false);

public sealed record OfflineSemanticEvaluationDataset(
    string Version,
    IReadOnlyList<OfflineSemanticEvaluationCase> Cases);

public sealed record OfflineSemanticEvaluationThresholds(
    double Groundedness,
    double AnswerRelevance,
    double Faithfulness,
    double ContextPrecision,
    double ContextRecall,
    double CitationCorrectness,
    int TenantLeakageCount);

public sealed record OfflineSemanticEvaluationPricing(
    decimal EmbeddingUsdPerMillionInputTokens,
    decimal JudgeUsdPerMillionInputTokens,
    decimal JudgeUsdPerMillionOutputTokens);

public sealed record OfflineSemanticEvaluationResult(
    RagEvaluationEvidence Evidence,
    int? EmbeddingInputTokens,
    int JudgeInputTokens,
    int JudgeOutputTokens,
    int SemanticCaseCount,
    double DurationMs,
    decimal? EstimatedCostUsd,
    bool ThresholdsPassed);

public sealed class OfflineSemanticEvaluationRunner(
    IProfileKnowledgeRetriever profileRetriever,
    ISemanticEvaluationJudge judge,
    IRagEvaluationEvidenceRepository evidenceRepository,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
{
    public async Task<OfflineSemanticEvaluationResult> RunAsync(
        Guid workspaceId,
        EmbeddingProfileDescriptor profile,
        OfflineSemanticEvaluationDataset dataset,
        OfflineSemanticEvaluationThresholds thresholds,
        string runnerVersion,
        OfflineSemanticEvaluationPricing pricing,
        CancellationToken cancellationToken = default)
    {
        ValidateInputs(
            workspaceId,
            profile,
            dataset,
            thresholds,
            runnerVersion,
            pricing);

        var observations =
            new List<RagEvaluationObservation>(dataset.Cases.Count);
        var semanticScores =
            new List<SemanticEvaluationScore>(dataset.Cases.Count);

        int? embeddingInputTokens = 0;
        var judgeInputTokens = 0;
        var judgeOutputTokens = 0;
        var stopwatch = Stopwatch.StartNew();

        foreach (var testCase in dataset.Cases)
        {
            var retrieval = await profileRetriever.RetrieveAsync(
                workspaceId,
                profile,
                testCase.Query,
                Math.Clamp(testCase.TopK, 1, 10),
                allowBuildingProfile: true,
                cancellationToken);

            if (embeddingInputTokens is not null)
            {
                if (retrieval.Usage?.InputTokens is int inputTokens)
                    embeddingInputTokens += inputTokens;
                else
                    embeddingInputTokens = null;
            }

            var retrievedSources = retrieval.Matches
                .Select(x => x.SourceName)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var expected = testCase.ExpectedSources
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var forbidden = testCase.ForbiddenSources
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var tenantForbidden = testCase.TenantForbiddenSources
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var retrieved = retrievedSources
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var expectedSatisfied = expected.Count == 0
                ? testCase.IsNegativeSafetyControl || retrieved.Count == 0
                : expected.All(retrieved.Contains);
            var forbiddenPresent = retrieved.Any(forbidden.Contains);
            var tenantLeakage = retrieved.Any(tenantForbidden.Contains);

            observations.Add(new RagEvaluationObservation(
                testCase.Id,
                testCase.ExpectedSources,
                retrievedSources,
                CitationCorrect:
                    expectedSatisfied &&
                    !forbiddenPresent &&
                    !tenantLeakage,
                TenantLeakage: tenantLeakage,
                IsNegativeSafetyControl:
                    testCase.IsNegativeSafetyControl));

            // Safety-only cases with no expected source are evaluated by
            // retrieval/citation policy, not by the external semantic judge.
            if (expected.Count == 0)
                continue;

            // Never forward cross-tenant or explicitly forbidden content to
            // an external judge. A zero score keeps the gate fail-closed.
            if (tenantLeakage || forbiddenPresent)
            {
                semanticScores.Add(
                    ZeroSemanticScore());
                continue;
            }

            var contexts = retrieval.Matches
                .Where(x => !string.IsNullOrWhiteSpace(x.SourceName))
                .Select(x => new SemanticEvaluationContext(
                    x.SourceName!,
                    x.Content,
                    x.Score))
                .ToArray();

            var score = await judge.JudgeAsync(
                new SemanticEvaluationRequest(
                    testCase.Id,
                    testCase.Query,
                    testCase.ReferenceFacts,
                    contexts),
                cancellationToken);

            semanticScores.Add(score);
            judgeInputTokens += score.InputTokens;
            judgeOutputTokens += score.OutputTokens;
        }

        stopwatch.Stop();

        if (semanticScores.Count == 0)
            throw new InvalidOperationException(
                "Offline semantic evaluation had no semantic cases.");

        var retrievalSummary =
            RagEvaluationMetrics.Summarize(observations);

        var groundedness =
            semanticScores.Average(x => x.Groundedness);
        var answerRelevance =
            semanticScores.Average(x => x.AnswerRelevance);
        var faithfulness =
            semanticScores.Average(x => x.Faithfulness);
        var contextPrecision =
            semanticScores.Average(x => x.ContextPrecision);
        var contextRecall =
            semanticScores.Average(x => x.ContextRecall);

        var evidence = new RagEvaluationEvidence(
            Guid.NewGuid(),
            dataset.Version,
            profile.Id,
            profile.Provider,
            profile.Model,
            profile.Dimensions,
            profile.IndexVersion,
            retrievalSummary.HitRateAtK,
            retrievalSummary.MeanRecallAtK,
            retrievalSummary.MeanPrecisionAtK,
            retrievalSummary.CitationCorrectness,
            retrievalSummary.TenantLeakageCount,
            RagEvaluationKind.OfflineSemantic,
            runnerVersion,
            clock.GetUtcNow(),
            groundedness,
            answerRelevance,
            faithfulness,
            contextPrecision,
            contextRecall);

        await evidenceRepository.AddAsync(
            evidence,
            cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        decimal? estimatedCostUsd = null;
        if (embeddingInputTokens is int embeddingTokens)
        {
            estimatedCostUsd =
                embeddingTokens *
                pricing.EmbeddingUsdPerMillionInputTokens /
                1_000_000m +
                judgeInputTokens *
                pricing.JudgeUsdPerMillionInputTokens /
                1_000_000m +
                judgeOutputTokens *
                pricing.JudgeUsdPerMillionOutputTokens /
                1_000_000m;
        }

        var thresholdsPassed =
            groundedness >= thresholds.Groundedness &&
            answerRelevance >= thresholds.AnswerRelevance &&
            faithfulness >= thresholds.Faithfulness &&
            contextPrecision >= thresholds.ContextPrecision &&
            contextRecall >= thresholds.ContextRecall &&
            retrievalSummary.CitationCorrectness >=
                thresholds.CitationCorrectness &&
            retrievalSummary.TenantLeakageCount <=
                thresholds.TenantLeakageCount;

        return new OfflineSemanticEvaluationResult(
            evidence,
            embeddingInputTokens,
            judgeInputTokens,
            judgeOutputTokens,
            semanticScores.Count,
            stopwatch.Elapsed.TotalMilliseconds,
            estimatedCostUsd,
            thresholdsPassed);
    }

    private static SemanticEvaluationScore ZeroSemanticScore() =>
        new(
            Groundedness: 0,
            AnswerRelevance: 0,
            Faithfulness: 0,
            ContextPrecision: 0,
            ContextRecall: 0,
            InputTokens: 0,
            OutputTokens: 0,
            DurationMs: 0);

    private static void ValidateInputs(
        Guid workspaceId,
        EmbeddingProfileDescriptor profile,
        OfflineSemanticEvaluationDataset dataset,
        OfflineSemanticEvaluationThresholds thresholds,
        string runnerVersion,
        OfflineSemanticEvaluationPricing pricing)
    {
        if (workspaceId == Guid.Empty)
            throw new ArgumentException(
                "Workspace ID is required.",
                nameof(workspaceId));

        profile.Validate();

        if (string.IsNullOrWhiteSpace(dataset.Version) ||
            dataset.Cases.Count == 0)
            throw new ArgumentException(
                "Offline semantic dataset is required.",
                nameof(dataset));

        if (string.IsNullOrWhiteSpace(runnerVersion))
            throw new ArgumentException(
                "Runner version is required.",
                nameof(runnerVersion));

        ValidateThreshold(thresholds.Groundedness);
        ValidateThreshold(thresholds.AnswerRelevance);
        ValidateThreshold(thresholds.Faithfulness);
        ValidateThreshold(thresholds.ContextPrecision);
        ValidateThreshold(thresholds.ContextRecall);
        ValidateThreshold(thresholds.CitationCorrectness);

        if (thresholds.TenantLeakageCount < 0)
            throw new ArgumentOutOfRangeException(nameof(thresholds));

        if (pricing.EmbeddingUsdPerMillionInputTokens <= 0 ||
            pricing.JudgeUsdPerMillionInputTokens <= 0 ||
            pricing.JudgeUsdPerMillionOutputTokens <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(pricing),
                "Offline evaluation pricing must be positive.");
    }

    private static void ValidateThreshold(double value)
    {
        if (double.IsNaN(value) ||
            double.IsInfinity(value) ||
            value is < 0 or > 1)
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "Evaluation thresholds must be between 0 and 1.");
    }
}
