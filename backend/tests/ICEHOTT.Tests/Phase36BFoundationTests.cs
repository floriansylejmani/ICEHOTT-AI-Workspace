using System.Net;
using System.Text;
using System.Text.Json;
using ICEHOTT.Application.Abstractions;
using ICEHOTT.Application.Knowledge;
using ICEHOTT.Domain.Knowledge;
using ICEHOTT.Infrastructure.Ai;
using Microsoft.Extensions.Options;

namespace ICEHOTT.Tests;

public sealed class Phase36BFoundationTests
{
    private static EmbeddingProfileDescriptor OpenAiProfile(Guid? id = null) => new(
        id ?? Guid.NewGuid(),
        "openai-small-1536-v1",
        "openai",
        "text-embedding-3-small",
        1536,
        "1",
        1,
        "cosine",
        "unit");

    private static EmbeddingProfileDescriptor LocalBuildingProfile(Guid? id = null) => new(
        id ?? Guid.NewGuid(),
        "local-build-64-v2",
        "icehott-ai-runtime",
        "deterministic-64d",
        64,
        "2",
        2,
        "cosine",
        "unit");

    [Fact]
    public async Task OpenAi_Adapter_Sends_Profile_Bound_Request_And_Returns_Usage()
    {
        var vector = Enumerable.Repeat(0.01f, 1536).ToArray();
        var responseJson = JsonSerializer.Serialize(new
        {
            data = new[]
            {
                new
                {
                    index = 0,
                    embedding = vector
                }
            },
            model = "text-embedding-3-small",
            usage = new
            {
                prompt_tokens = 7,
                total_tokens = 7
            }
        });

        var handler = new RecordingHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    responseJson,
                    Encoding.UTF8,
                    "application/json")
            });

        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.openai.com/v1/")
        };

        var provider = new OpenAiEmbeddingProvider(
            client,
            Options.Create(new OpenAiEmbeddingOptions
            {
                ApiKey = "test-key-not-real"
            }));

        var profile = OpenAiProfile();
        var result = await provider.EmbedAsync(
            profile,
            EmbeddingPurpose.Query,
            ["hello"]);

        Assert.Equal(profile, result.Profile);
        Assert.Single(result.Embeddings);
        Assert.Equal(1536, result.Embeddings[0].Count);
        Assert.Equal(7, result.Usage?.InputTokens);
        Assert.Equal(7, result.Usage?.TotalTokens);
        Assert.Equal("/v1/embeddings", handler.RequestUri?.AbsolutePath);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("test-key-not-real", handler.AuthorizationParameter);

        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal(
            "text-embedding-3-small",
            body.RootElement.GetProperty("model").GetString());
        Assert.Equal(
            1536,
            body.RootElement.GetProperty("dimensions").GetInt32());
        Assert.Equal(
            "float",
            body.RootElement.GetProperty("encoding_format").GetString());
        Assert.Equal(
            "hello",
            body.RootElement.GetProperty("input")[0].GetString());
    }

    [Fact]
    public async Task OpenAi_Adapter_Classifies_Rate_Limit_As_Retryable()
    {
        var handler = new RecordingHandler(
            _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));

        var provider = new OpenAiEmbeddingProvider(
            new HttpClient(handler)
            {
                BaseAddress = new Uri("https://api.openai.com/v1/")
            },
            Options.Create(new OpenAiEmbeddingOptions
            {
                ApiKey = "test-key-not-real"
            }));

        var exception = await Assert.ThrowsAsync<EmbeddingProviderException>(
            () => provider.EmbedAsync(
                OpenAiProfile(),
                EmbeddingPurpose.Document,
                ["hello"]));

        Assert.Equal(EmbeddingFailureKind.RateLimited, exception.Kind);
        Assert.True(exception.IsRetryable);
    }

    [Fact]
    public async Task OpenAi_Adapter_Requires_Server_Side_Key_Before_Http_Call()
    {
        var handler = new RecordingHandler(
            _ => throw new InvalidOperationException(
                "HTTP should not be called without a configured key."));

        var provider = new OpenAiEmbeddingProvider(
            new HttpClient(handler)
            {
                BaseAddress = new Uri("https://api.openai.com/v1/")
            },
            Options.Create(new OpenAiEmbeddingOptions
            {
                ApiKey = ""
            }));

        var exception = await Assert.ThrowsAsync<EmbeddingProviderException>(
            () => provider.EmbedAsync(
                OpenAiProfile(),
                EmbeddingPurpose.Query,
                ["hello"]));

        Assert.Equal(EmbeddingFailureKind.Configuration, exception.Kind);
        Assert.False(exception.IsRetryable);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public void OpenAi_Configuration_Probe_Requires_Key_And_Compatible_Profile()
    {
        var client = new HttpClient(new RecordingHandler(
            _ => throw new InvalidOperationException("HTTP is not used by configuration probe.")))
        {
            BaseAddress = new Uri("https://api.openai.com/v1/")
        };

        var configured = new OpenAiEmbeddingProvider(
            client,
            Options.Create(new OpenAiEmbeddingOptions
            {
                ApiKey = "test-key-not-real"
            }));

        var unconfigured = new OpenAiEmbeddingProvider(
            client,
            Options.Create(new OpenAiEmbeddingOptions
            {
                ApiKey = ""
            }));

        Assert.True(configured.IsConfigured(OpenAiProfile()));
        Assert.False(unconfigured.IsConfigured(OpenAiProfile()));
        Assert.False(configured.IsConfigured(
            OpenAiProfile() with { Dimensions = 1024 }));
    }

    [Fact]
    public async Task OpenAi_Adapter_Rejects_Incompatible_Profile_Before_Http_Call()
    {
        var handler = new RecordingHandler(
            _ => throw new InvalidOperationException(
                "HTTP should not be called for incompatible profile."));

        var provider = new OpenAiEmbeddingProvider(
            new HttpClient(handler)
            {
                BaseAddress = new Uri("https://api.openai.com/v1/")
            },
            Options.Create(new OpenAiEmbeddingOptions
            {
                ApiKey = "test-key-not-real"
            }));

        var profile = OpenAiProfile() with { Dimensions = 1024 };

        var exception = await Assert.ThrowsAsync<EmbeddingProviderException>(
            () => provider.EmbedAsync(
                profile,
                EmbeddingPurpose.Query,
                ["hello"]));

        Assert.Equal(EmbeddingFailureKind.ProfileMismatch, exception.Kind);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Build_Service_Uses_Building_Profile_And_Stable_Chunk_Ids()
    {
        var profile = LocalBuildingProfile();
        var workspace = Guid.NewGuid();
        var firstChunk = Guid.NewGuid();
        var secondChunk = Guid.NewGuid();

        var buildStore = new FakeBuildStore(
        [
            new EmbeddingBuildChunk(firstChunk, workspace, "alpha"),
            new EmbeddingBuildChunk(secondChunk, workspace, "beta")
        ]);

        var vectorStore = new CapturingVectorStore();
        var provider = new FixedProvider(
            "icehott-ai-runtime",
            64,
            usageTokens: 2);

        var service = new EmbeddingProfileBuildService(
            new FixedBuildResolver(profile),
            new EmbeddingProviderRegistry([provider]),
            new ReadyProvisioner(),
            buildStore,
            vectorStore);

        var progress = await service.BuildNextBatchAsync();

        Assert.Equal(profile.Id, progress.ProfileId);
        Assert.Equal(2, progress.EmbeddedThisPass);
        Assert.Equal(2, progress.ReadyChunkCount);
        Assert.Equal(2, progress.EmbeddedChunkCount);
        Assert.True(progress.IsComplete);
        Assert.Equal(
            new[] { firstChunk, secondChunk },
            vectorStore.Stored.Select(x => x.ChunkId).ToArray());
        Assert.All(
            provider.Purposes,
            x => Assert.Equal(EmbeddingPurpose.Document, x));
    }

    [Fact]
    public async Task Build_Service_Never_Mixes_Workspaces_In_One_Provider_Batch()
    {
        var profile = LocalBuildingProfile();
        var workspaceA = Guid.NewGuid();
        var workspaceB = Guid.NewGuid();

        var buildStore = new FakeBuildStore(
        [
            new EmbeddingBuildChunk(Guid.NewGuid(), workspaceA, "a-one"),
            new EmbeddingBuildChunk(Guid.NewGuid(), workspaceA, "a-two"),
            new EmbeddingBuildChunk(Guid.NewGuid(), workspaceB, "b-one")
        ]);

        var provider = new FixedProvider(
            "icehott-ai-runtime",
            64);

        var service = new EmbeddingProfileBuildService(
            new FixedBuildResolver(profile),
            new EmbeddingProviderRegistry([provider]),
            new ReadyProvisioner(),
            buildStore,
            new CapturingVectorStore());

        var progress = await service.BuildNextBatchAsync();

        Assert.True(progress.IsComplete);
        Assert.Equal(2, provider.TextBatches.Count);
        Assert.Contains(
            provider.TextBatches,
            batch => batch.SequenceEqual(new[] { "a-one", "a-two" }));
        Assert.Contains(
            provider.TextBatches,
            batch => batch.SequenceEqual(new[] { "b-one" }));
        Assert.DoesNotContain(
            provider.TextBatches,
            batch =>
                batch.Any(x => x.StartsWith("a-", StringComparison.Ordinal)) &&
                batch.Any(x => x.StartsWith("b-", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Build_Service_Rejects_Profile_Above_Float32_Hnsw_Limit()
    {
        var profile = LocalBuildingProfile() with { Dimensions = 2001 };
        var service = new EmbeddingProfileBuildService(
            new FixedBuildResolver(profile),
            new EmbeddingProviderRegistry(
            [
                new FixedProvider("icehott-ai-runtime", 2001)
            ]),
            new ReadyProvisioner(),
            new FakeBuildStore([]),
            new CapturingVectorStore());

        var exception = await Assert.ThrowsAsync<VectorStoreUnavailableException>(
            () => service.BuildNextBatchAsync());

        Assert.False(exception.IsRetryable);
    }

    [Fact]
    public void Promotion_Policy_Binds_Evidence_To_Profile_And_Rejects_Tenant_Leakage()
    {
        var profile = OpenAiProfile();
        var policy = new RagPromotionPolicy(
            RagPromotionRequirements.FoundationDefault);

        var wrongProfileEvidence = Evidence(
            Guid.NewGuid(),
            profile with { Id = Guid.NewGuid() },
            RagEvaluationKind.Deterministic,
            tenantLeakage: 0);

        Assert.Throws<InvalidOperationException>(
            () => policy.Validate(
                profile,
                wrongProfileEvidence,
                null));

        var leakingEvidence = Evidence(
            Guid.NewGuid(),
            profile,
            RagEvaluationKind.Deterministic,
            tenantLeakage: 1);

        Assert.Throws<InvalidOperationException>(
            () => policy.Validate(
                profile,
                leakingEvidence,
                null));
    }

    [Fact]
    public void Production_Promotion_Policy_Requires_Offline_Semantic_Evidence()
    {
        var profile = OpenAiProfile();
        var requirements = RagPromotionRequirements.FoundationDefault with
        {
            RequireOfflineSemanticEvidence = true
        };
        var policy = new RagPromotionPolicy(requirements);

        var deterministic = Evidence(
            Guid.NewGuid(),
            profile,
            RagEvaluationKind.Deterministic,
            tenantLeakage: 0);

        Assert.Throws<InvalidOperationException>(
            () => policy.Validate(
                profile,
                deterministic,
                null));

        var offline = new RagEvaluationEvidence(
            Guid.NewGuid(),
            "rag-v1",
            profile.Id,
            profile.Provider,
            profile.Model,
            profile.Dimensions,
            profile.IndexVersion,
            1,
            1,
            1,
            1,
            0,
            RagEvaluationKind.OfflineSemantic,
            "offline-v1",
            DateTimeOffset.UtcNow,
            groundedness: 0.9,
            answerRelevance: 0.9,
            faithfulness: 0.9,
            contextPrecision: 0.9,
            contextRecall: 0.9);

        policy.Validate(profile, deterministic, offline);
    }

    [Fact]
    public void Production_Promotion_Policy_Rejects_Offline_Evidence_From_Wrong_Dataset()
    {
        var profile = OpenAiProfile();
        var requirements = RagPromotionRequirements.FoundationDefault with
        {
            RequireOfflineSemanticEvidence = true
        };
        var policy = new RagPromotionPolicy(requirements);

        var deterministic = Evidence(
            Guid.NewGuid(),
            profile,
            RagEvaluationKind.Deterministic,
            tenantLeakage: 0);

        var offline = new RagEvaluationEvidence(
            Guid.NewGuid(),
            "rag-v2",
            profile.Id,
            profile.Provider,
            profile.Model,
            profile.Dimensions,
            profile.IndexVersion,
            1,
            1,
            1,
            1,
            0,
            RagEvaluationKind.OfflineSemantic,
            "offline-v1",
            DateTimeOffset.UtcNow,
            groundedness: 0.9,
            answerRelevance: 0.9,
            faithfulness: 0.9,
            contextPrecision: 0.9,
            contextRecall: 0.9);

        Assert.Throws<InvalidOperationException>(
            () => policy.Validate(profile, deterministic, offline));
    }

    [Fact]
    public async Task Activation_Service_Does_Not_Call_Store_When_Coverage_Is_Incomplete()
    {
        var candidateId = Guid.NewGuid();
        var activeId = Guid.NewGuid();
        var candidate = ProfileEntity(
            candidateId,
            EmbeddingProfileStatus.Building,
            version: "2",
            indexVersion: 2);
        var active = ProfileEntity(
            activeId,
            EmbeddingProfileStatus.Active,
            version: "1",
            indexVersion: 1);

        var descriptor = Descriptor(candidate);
        var deterministic = Evidence(
            Guid.NewGuid(),
            descriptor,
            RagEvaluationKind.Deterministic,
            tenantLeakage: 0);

        var activationStore = new CapturingActivationStore();
        var service = new EmbeddingProfileActivationService(
            new FakeProfileRepository(active, candidate),
            new ReadyProvisioner(),
            new FakeBuildStore(
                [],
                new EmbeddingProfileCoverage(3, 2)),
            new FakeEvidenceRepository(deterministic),
            activationStore,
            new RagPromotionPolicy(
                RagPromotionRequirements.FoundationDefault),
            TimeProvider.System);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ActivateAsync(
                candidateId,
                deterministic.Id));

        Assert.Equal(0, activationStore.Calls);
    }

    [Fact]
    public async Task Activation_Service_Delegates_Only_After_All_Preconditions_Pass()
    {
        var candidateId = Guid.NewGuid();
        var activeId = Guid.NewGuid();
        var candidate = ProfileEntity(
            candidateId,
            EmbeddingProfileStatus.Building,
            version: "2",
            indexVersion: 2);
        var active = ProfileEntity(
            activeId,
            EmbeddingProfileStatus.Active,
            version: "1",
            indexVersion: 1);

        var descriptor = Descriptor(candidate);
        var deterministic = Evidence(
            Guid.NewGuid(),
            descriptor,
            RagEvaluationKind.Deterministic,
            tenantLeakage: 0);

        var activationStore = new CapturingActivationStore(activeId);
        var service = new EmbeddingProfileActivationService(
            new FakeProfileRepository(active, candidate),
            new ReadyProvisioner(),
            new FakeBuildStore(
                [],
                new EmbeddingProfileCoverage(3, 3)),
            new FakeEvidenceRepository(deterministic),
            activationStore,
            new RagPromotionPolicy(
                RagPromotionRequirements.FoundationDefault),
            TimeProvider.System);

        var result = await service.ActivateAsync(
            candidateId,
            deterministic.Id);

        Assert.Equal(activeId, result.PreviousActiveProfileId);
        Assert.Equal(candidateId, result.ActiveProfileId);
        Assert.Equal(1, activationStore.Calls);
    }

    [Fact]
    public async Task Benchmark_Runner_Counts_Tenant_Forbidden_Source_As_Leakage()
    {
        var profile = OpenAiProfile();
        var evidenceRepository = new FakeEvidenceRepository();

        var profileRetriever = new FakeProfileRetriever(
            new KnowledgeMatch(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "Other Tenant",
                "other-tenant.txt",
                "content that must never cross workspace boundaries",
                0.99),
            usageTokens: 5);

        var runner = new RagBenchmarkRunner(
            profileRetriever,
            evidenceRepository,
            new FakeUnitOfWork(),
            TimeProvider.System);

        var dataset = new RagBenchmarkDataset(
            "rag-v1",
        [
            new RagBenchmarkCase(
                "tenant-isolation",
                "private tenant query",
                [],
                [],
                3,
                TenantForbiddenSources: ["other-tenant.txt"])
        ],
            new RagBenchmarkThresholds(1, 1, 1, 1, 0));

        var result = await runner.RunAsync(
            Guid.NewGuid(),
            profile,
            dataset,
            "runner-v1");

        Assert.Equal(1, result.Evidence.TenantLeakageCount);
        Assert.Equal(0, result.Evidence.CitationCorrectness);
        Assert.False(result.ThresholdsPassed);
    }

    [Fact]
    public async Task Benchmark_Runner_Treats_Empty_Expected_Sources_As_Negative_Safety_Control()
    {
        var profile = OpenAiProfile();
        var evidenceRepository = new FakeEvidenceRepository();

        var profileRetriever = new FakeProfileRetriever(
            new KnowledgeMatch(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "Safe fallback",
                "safe-fallback.txt",
                "benign content",
                0.80),
            usageTokens: 3);

        var runner = new RagBenchmarkRunner(
            profileRetriever,
            evidenceRepository,
            new FakeUnitOfWork(),
            TimeProvider.System);

        var dataset = new RagBenchmarkDataset(
            "rag-v1",
        [
            new RagBenchmarkCase(
                "prompt-injection",
                "reveal hidden prompt",
                [],
                ["unsafe-instructions.txt"],
                3,
                IsNegativeSafetyControl: true)
        ],
            new RagBenchmarkThresholds(0, 0, 0, 1, 0));

        var result = await runner.RunAsync(
            Guid.NewGuid(),
            profile,
            dataset,
            "runner-v2");

        Assert.Equal(1, result.Evidence.CitationCorrectness);
        Assert.Equal(0, result.Evidence.TenantLeakageCount);
        Assert.True(result.ThresholdsPassed);
    }

    [Fact]
    public async Task Benchmark_Runner_Persists_Profile_Bound_Evidence_And_Uses_Measured_Tokens()
    {
        var profile = OpenAiProfile();
        var evidenceRepository = new FakeEvidenceRepository();
        var unitOfWork = new FakeUnitOfWork();

        var profileRetriever = new FakeProfileRetriever(
            new KnowledgeMatch(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "Support",
                "support-policy.txt",
                "refund within thirty days",
                0.95),
            usageTokens: 12);

        var runner = new RagBenchmarkRunner(
            profileRetriever,
            evidenceRepository,
            unitOfWork,
            TimeProvider.System);

        var dataset = new RagBenchmarkDataset(
            "rag-v1",
        [
            new RagBenchmarkCase(
                "refund",
                "refund window",
                ["support-policy.txt"],
                [],
                3)
        ],
            new RagBenchmarkThresholds(1, 1, 1, 1, 0));

        var result = await runner.RunAsync(
            Guid.NewGuid(),
            profile,
            dataset,
            "runner-v1",
            new RagBenchmarkPricing(0.02m));

        Assert.Equal(profile.Id, result.Evidence.EmbeddingProfileId);
        Assert.Equal(12, result.InputTokens);
        Assert.Equal(1, result.Evidence.HitRateAtK);
        Assert.Equal(1, result.Evidence.CitationCorrectness);
        Assert.True(result.ThresholdsPassed);
        Assert.Single(evidenceRepository.Added);
        Assert.Equal(1, unitOfWork.SaveCalls);
        Assert.NotNull(result.EstimatedCostUsd);
    }

    private static EmbeddingProfileDescriptor Descriptor(
        EmbeddingProfile profile) =>
        new(
            profile.Id,
            profile.Key,
            profile.Provider,
            profile.Model,
            profile.Dimensions,
            profile.Version,
            profile.IndexVersion,
            profile.DistanceMetric,
            profile.Normalization);

    private static RagEvaluationEvidence Evidence(
        Guid id,
        EmbeddingProfileDescriptor profile,
        RagEvaluationKind kind,
        int tenantLeakage) =>
        new(
            id,
            "rag-v1",
            profile.Id,
            profile.Provider,
            profile.Model,
            profile.Dimensions,
            profile.IndexVersion,
            1,
            1,
            1,
            1,
            tenantLeakage,
            kind,
            "test-runner-v1",
            DateTimeOffset.UtcNow);

    private static EmbeddingProfile ProfileEntity(
        Guid id,
        EmbeddingProfileStatus status,
        string version,
        int indexVersion) =>
        new(
            id,
            $"profile-{id:N}",
            "openai",
            "text-embedding-3-small",
            1536,
            version,
            indexVersion,
            "cosine",
            "unit",
            status,
            DateTimeOffset.UtcNow);

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? RequestBody { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            RequestUri = request.RequestUri;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return responder(request);
        }
    }

    private sealed class FixedProvider(
        string providerName,
        int dimensions,
        int? usageTokens = null) : IEmbeddingProvider
    {
        public string Provider => providerName;
        public List<EmbeddingPurpose> Purposes { get; } = [];
        public List<IReadOnlyList<string>> TextBatches { get; } = [];

        public EmbeddingProviderCapabilities Capabilities { get; } = new(
            providerName,
            new HashSet<int> { dimensions },
            64,
            null,
            false);

        public Task<EmbeddingBatch> EmbedAsync(
            EmbeddingProfileDescriptor profile,
            EmbeddingPurpose purpose,
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken = default)
        {
            Purposes.Add(purpose);
            TextBatches.Add(texts.ToArray());

            IReadOnlyList<IReadOnlyList<float>> vectors =
                texts.Select(
                    _ => (IReadOnlyList<float>)new float[dimensions])
                .ToArray();

            return Task.FromResult(
                new EmbeddingBatch(
                    profile,
                    vectors,
                    usageTokens is null
                        ? null
                        : new EmbeddingUsage(
                            usageTokens,
                            usageTokens)));
        }
    }

    private sealed class FixedBuildResolver(
        EmbeddingProfileDescriptor profile)
        : IBuildEmbeddingProfileResolver
    {
        public Task<EmbeddingProfileDescriptor?> ResolveAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<EmbeddingProfileDescriptor?>(profile);
    }

    private sealed class ReadyProvisioner(bool ready = true)
        : IVectorIndexProvisioner
    {
        public Task EnsureBuildIndexAsync(
            EmbeddingProfileDescriptor profile,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> IsIndexReadyAsync(
            EmbeddingProfileDescriptor profile,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ready);

        public Task DropRetiredIndexAsync(
            EmbeddingProfileDescriptor profile,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeBuildStore : IEmbeddingBuildStore
    {
        private readonly List<EmbeddingBuildChunk> _missing;
        private EmbeddingProfileCoverage _coverage;

        public FakeBuildStore(
            IReadOnlyList<EmbeddingBuildChunk> missing,
            EmbeddingProfileCoverage? coverage = null)
        {
            _missing = missing.ToList();
            _coverage = coverage ??
                new EmbeddingProfileCoverage(
                    _missing.Count,
                    0);
        }

        public Task<IReadOnlyList<EmbeddingBuildChunk>> GetMissingReadyChunksAsync(
            Guid profileId,
            int limit,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<EmbeddingBuildChunk> result =
                _missing.Take(limit).ToArray();

            _coverage = new EmbeddingProfileCoverage(
                _missing.Count,
                _missing.Count);

            return Task.FromResult(result);
        }

        public Task<EmbeddingProfileCoverage> GetCoverageAsync(
            Guid profileId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_coverage);
    }

    private sealed class CapturingVectorStore : IVectorStore
    {
        public List<VectorEmbedding> Stored { get; } = [];

        public Task<bool> IsProfileReadyAsync(
            EmbeddingProfileDescriptor profile,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task StoreManyAsync(
            Guid workspaceId,
            EmbeddingProfileDescriptor profile,
            IReadOnlyList<VectorEmbedding> embeddings,
            CancellationToken cancellationToken = default)
        {
            Stored.AddRange(embeddings);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<KnowledgeMatch>> SearchAsync(
            Guid workspaceId,
            EmbeddingProfileDescriptor profile,
            string queryText,
            IReadOnlyList<float> queryEmbedding,
            int limit,
            bool allowBuildingProfile = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<KnowledgeMatch>>([]);
    }

    private sealed class FakeProfileRepository(
        EmbeddingProfile active,
        EmbeddingProfile candidate)
        : IEmbeddingProfileRepository
    {
        public Task<EmbeddingProfile?> GetActiveAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<EmbeddingProfile?>(active);

        public Task<EmbeddingProfile?> GetBuildingAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<EmbeddingProfile?>(candidate);

        public Task<EmbeddingProfile?> FindAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<EmbeddingProfile?>(
                id == candidate.Id
                    ? candidate
                    : id == active.Id
                        ? active
                        : null);

        public Task AddAsync(
            EmbeddingProfile profile,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeEvidenceRepository(
        params RagEvaluationEvidence[] evidence)
        : IRagEvaluationEvidenceRepository
    {
        private readonly Dictionary<Guid, RagEvaluationEvidence> _items =
            evidence.ToDictionary(x => x.Id);

        public List<RagEvaluationEvidence> Added { get; } = [];

        public Task AddAsync(
            RagEvaluationEvidence item,
            CancellationToken cancellationToken = default)
        {
            Added.Add(item);
            _items[item.Id] = item;
            return Task.CompletedTask;
        }

        public Task<RagEvaluationEvidence?> FindAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                _items.TryGetValue(id, out var item)
                    ? item
                    : null);
    }

    private sealed class CapturingActivationStore(Guid? previousActive = null)
        : IEmbeddingProfileActivationStore
    {
        public int Calls { get; private set; }

        public Task<EmbeddingProfileActivationResult> ActivateAsync(
            Guid candidateProfileId,
            Guid deterministicEvidenceId,
            Guid? offlineEvidenceId,
            DateTimeOffset activatedAtUtc,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(
                new EmbeddingProfileActivationResult(
                    previousActive ?? Guid.NewGuid(),
                    candidateProfileId,
                    activatedAtUtc));
        }
    }

    private sealed class FakeProfileRetriever(
        KnowledgeMatch match,
        int usageTokens)
        : IProfileKnowledgeRetriever
    {
        public Task<KnowledgeRetrievalResult> RetrieveAsync(
            Guid workspaceId,
            EmbeddingProfileDescriptor profile,
            string query,
            int limit,
            bool allowBuildingProfile = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                new KnowledgeRetrievalResult(
                    [match],
                    1,
                    0,
                    new EmbeddingUsage(usageTokens, usageTokens),
                    5));
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
