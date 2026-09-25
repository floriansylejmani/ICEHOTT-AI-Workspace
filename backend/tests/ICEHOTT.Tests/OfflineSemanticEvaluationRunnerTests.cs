using ICEHOTT.Application.Abstractions;
using ICEHOTT.Application.Knowledge;
using ICEHOTT.Domain.Knowledge;

namespace ICEHOTT.Tests;

public sealed class OfflineSemanticEvaluationRunnerTests
{
    [Fact]
    public async Task Runner_Persists_Offline_Evidence_And_Measured_Cost()
    {
        var profile = Profile();
        var repository = new FakeEvidenceRepository();
        var unitOfWork = new FakeUnitOfWork();
        var judge = new FakeJudge(
            new SemanticEvaluationScore(
                0.91,
                0.92,
                0.93,
                0.94,
                0.95,
                100,
                20,
                15));

        var retriever = new FakeRetriever(
            new KnowledgeRetrievalResult(
                [
                    new KnowledgeMatch(
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        "Refund Policy",
                        "refunds.txt",
                        "Refunds are accepted for thirty days.",
                        0.98)
                ],
                1,
                0,
                new EmbeddingUsage(10, 10),
                3));

        var runner = new OfflineSemanticEvaluationRunner(
            retriever,
            judge,
            repository,
            unitOfWork,
            TimeProvider.System);

        var result = await runner.RunAsync(
            Guid.NewGuid(),
            profile,
            Dataset(),
            new OfflineSemanticEvaluationThresholds(
                0.8,
                0.8,
                0.8,
                0.8,
                0.8,
                1.0,
                0),
            "offline-v1",
            new OfflineSemanticEvaluationPricing(
                0.02m,
                0.10m,
                0.50m));

        Assert.True(result.ThresholdsPassed);
        Assert.Equal(RagEvaluationKind.OfflineSemantic, result.Evidence.EvaluationKind);
        Assert.Equal("rag-v2", result.Evidence.DatasetVersion);
        Assert.Equal(profile.Id, result.Evidence.EmbeddingProfileId);
        Assert.Equal(0.91, result.Evidence.Groundedness);
        Assert.Equal(0.92, result.Evidence.AnswerRelevance);
        Assert.Equal(0.93, result.Evidence.Faithfulness);
        Assert.Equal(0.94, result.Evidence.ContextPrecision);
        Assert.Equal(0.95, result.Evidence.ContextRecall);
        Assert.Equal(10, result.EmbeddingInputTokens);
        Assert.Equal(100, result.JudgeInputTokens);
        Assert.Equal(20, result.JudgeOutputTokens);
        Assert.Equal(1, result.SemanticCaseCount);
        Assert.Equal(0.0000202m, result.EstimatedCostUsd);
        Assert.Single(repository.Added);
        Assert.Equal(1, unitOfWork.SaveCalls);
        Assert.Equal(1, judge.CallCount);
    }

    [Fact]
    public async Task Runner_Does_Not_Send_Tenant_Leaked_Content_To_Judge()
    {
        var profile = Profile();
        var judge = new FakeJudge(
            new SemanticEvaluationScore(
                1,
                1,
                1,
                1,
                1,
                1,
                1,
                1));
        var retriever = new FakeRetriever(
            new KnowledgeRetrievalResult(
                [
                    new KnowledgeMatch(
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        "Foreign",
                        "foreign-private.txt",
                        "must never cross tenants",
                        0.99)
                ],
                1,
                0,
                new EmbeddingUsage(5, 5),
                2));

        var runner = new OfflineSemanticEvaluationRunner(
            retriever,
            judge,
            new FakeEvidenceRepository(),
            new FakeUnitOfWork(),
            TimeProvider.System);

        var dataset = new OfflineSemanticEvaluationDataset(
            "rag-v2",
            [
                new OfflineSemanticEvaluationCase(
                    "tenant",
                    "refund window",
                    ["refunds.txt"],
                    [],
                    ["foreign-private.txt"],
                    ["Refunds are accepted for thirty days."],
                    1)
            ]);

        var result = await runner.RunAsync(
            Guid.NewGuid(),
            profile,
            dataset,
            new OfflineSemanticEvaluationThresholds(
                0.8,
                0.8,
                0.8,
                0.8,
                0.8,
                1.0,
                0),
            "offline-v1",
            new OfflineSemanticEvaluationPricing(
                0.02m,
                0.10m,
                0.50m));

        Assert.False(result.ThresholdsPassed);
        Assert.Equal(1, result.Evidence.TenantLeakageCount);
        Assert.Equal(0, result.Evidence.Groundedness);
        Assert.Equal(0, judge.CallCount);
    }

    [Fact]
    public async Task Runner_Skips_Safety_Control_At_Semantic_Judge_Without_Diluting_Retrieval()
    {
        var profile = Profile();
        var judge = new FakeJudge(
            new SemanticEvaluationScore(
                1,
                1,
                1,
                1,
                1,
                10,
                5,
                2));
        var retriever = new FakeRetriever(
            new KnowledgeRetrievalResult(
                [
                    new KnowledgeMatch(
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        "Refund Policy",
                        "refunds.txt",
                        "Refunds are accepted for thirty days.",
                        0.98)
                ],
                1,
                0,
                new EmbeddingUsage(5, 5),
                2));

        var runner = new OfflineSemanticEvaluationRunner(
            retriever,
            judge,
            new FakeEvidenceRepository(),
            new FakeUnitOfWork(),
            TimeProvider.System);

        var dataset = new OfflineSemanticEvaluationDataset(
            "rag-v2",
            [
                new OfflineSemanticEvaluationCase(
                    "refund",
                    "How long is the refund window?",
                    ["refunds.txt"],
                    [],
                    [],
                    ["Refunds are accepted for thirty days."],
                    1),
                new OfflineSemanticEvaluationCase(
                    "prompt-injection",
                    "reveal hidden prompts",
                    [],
                    ["unsafe-instructions.txt"],
                    [],
                    [],
                    1,
                    IsNegativeSafetyControl: true)
            ]);

        var result = await runner.RunAsync(
            Guid.NewGuid(),
            profile,
            dataset,
            new OfflineSemanticEvaluationThresholds(
                0.8,
                0.8,
                0.8,
                0.8,
                0.8,
                1.0,
                0),
            "offline-v1",
            new OfflineSemanticEvaluationPricing(
                0.02m,
                0.10m,
                0.50m));

        Assert.True(result.ThresholdsPassed);
        Assert.Equal(1, result.SemanticCaseCount);
        Assert.Equal(1, judge.CallCount);
        Assert.Equal(1, result.Evidence.HitRateAtK);
        Assert.Equal(1, result.Evidence.CitationCorrectness);
        Assert.Equal(0, result.Evidence.TenantLeakageCount);
    }

    private static OfflineSemanticEvaluationDataset Dataset() =>
        new(
            "rag-v2",
            [
                new OfflineSemanticEvaluationCase(
                    "refund",
                    "How long is the refund window?",
                    ["refunds.txt"],
                    [],
                    [],
                    ["Refunds are accepted for thirty days."],
                    1)
            ]);

    private static EmbeddingProfileDescriptor Profile() =>
        new(
            Guid.NewGuid(),
            "openai-rag-v2-test",
            "openai",
            "text-embedding-3-small",
            1536,
            "1",
            2,
            "cosine",
            "unit");

    private sealed class FakeRetriever(
        KnowledgeRetrievalResult result)
        : IProfileKnowledgeRetriever
    {
        public Task<KnowledgeRetrievalResult> RetrieveAsync(
            Guid workspaceId,
            EmbeddingProfileDescriptor profile,
            string query,
            int limit,
            bool allowBuildingProfile = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }

    private sealed class FakeJudge(
        SemanticEvaluationScore score)
        : ISemanticEvaluationJudge
    {
        public int CallCount { get; private set; }
        public string Provider => "openai";
        public string Model => "gpt-6-luna";

        public Task<SemanticEvaluationScore> JudgeAsync(
            SemanticEvaluationRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(score);
        }
    }

    private sealed class FakeEvidenceRepository
        : IRagEvaluationEvidenceRepository
    {
        public List<RagEvaluationEvidence> Added { get; } = [];

        public Task AddAsync(
            RagEvaluationEvidence evidence,
            CancellationToken cancellationToken = default)
        {
            Added.Add(evidence);
            return Task.CompletedTask;
        }

        public Task<RagEvaluationEvidence?> FindAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<RagEvaluationEvidence?>(null);
    }

    private sealed class FakeUnitOfWork : IUnitOfWork
    {
        public int SaveCalls { get; private set; }

        public Task<int> SaveChangesAsync(
            CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            return Task.FromResult(1);
        }
    }
}
