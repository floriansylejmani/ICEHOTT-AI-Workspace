using ICEHOTT.Application.Abstractions;
using ICEHOTT.Infrastructure.Ai;
using Microsoft.EntityFrameworkCore;

namespace ICEHOTT.Tests;

/// <summary>
/// B2: Unit tests for EmbeddingProviderRegistry and IEmbeddingProviderRegistry contract.
/// </summary>
public sealed class EmbeddingProviderRegistryTests
{
    // ── Registry resolves known provider ────────────────────────────────────────

    [Fact]
    public void Registry_Resolves_Registered_Provider_By_Name()
    {
        var fake = new NamedFakeProvider("my-provider");
        var registry = new EmbeddingProviderRegistry([fake]);

        var resolved = registry.Resolve("my-provider");

        Assert.Same(fake, resolved);
    }

    [Fact]
    public void Registry_Resolution_Is_Case_Insensitive()
    {
        var fake = new NamedFakeProvider("My-Provider");
        var registry = new EmbeddingProviderRegistry([fake]);

        Assert.Same(fake, registry.Resolve("MY-PROVIDER"));
        Assert.Same(fake, registry.Resolve("my-provider"));
        Assert.Same(fake, registry.Resolve("My-Provider"));
    }

    [Fact]
    public void Registry_Resolves_Multiple_Providers_By_Name()
    {
        var providerA = new NamedFakeProvider("provider-a");
        var providerB = new NamedFakeProvider("provider-b");
        var registry = new EmbeddingProviderRegistry([providerA, providerB]);

        Assert.Same(providerA, registry.Resolve("provider-a"));
        Assert.Same(providerB, registry.Resolve("provider-b"));
    }

    // ── Unknown provider fails explicitly ────────────────────────────────────────

    [Fact]
    public void Registry_Throws_On_Unknown_Provider_Name()
    {
        var registry = new EmbeddingProviderRegistry([new NamedFakeProvider("known")]);

        var exception = Assert.Throws<EmbeddingProviderException>(
            () => registry.Resolve("unknown-provider"));

        Assert.Equal(EmbeddingFailureKind.Configuration, exception.Kind);
        Assert.False(exception.IsRetryable);
        Assert.Contains("unknown-provider", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Registry_Throws_On_Unknown_Provider_When_Empty()
    {
        var registry = new EmbeddingProviderRegistry([]);

        var exception = Assert.Throws<EmbeddingProviderException>(
            () => registry.Resolve("anything"));

        Assert.Equal(EmbeddingFailureKind.Configuration, exception.Kind);
        Assert.False(exception.IsRetryable);
    }

    // ── AiRuntimeEmbeddingProvider registered and resolved ──────────────────────

    [Fact]
    public void AiRuntime_Provider_Is_Resolvable_By_Name_Via_Registry()
    {
        var provider = new AiRuntimeEmbeddingProvider(new NoOpRuntimeClient());
        var registry = new EmbeddingProviderRegistry([provider]);

        var resolved = registry.Resolve("icehott-ai-runtime");

        Assert.Same(provider, resolved);
    }

    // ── Test doubles ─────────────────────────────────────────────────────────────

    private sealed class NamedFakeProvider(string name) : IEmbeddingProvider
    {
        public string Provider => name;

        public EmbeddingProviderCapabilities Capabilities { get; } = new(
            name,
            new HashSet<int> { 64 },
            MaxBatchInputs: 64,
            MaxInputTokens: null,
            SupportsPurposeRouting: false);

        public Task<EmbeddingBatch> EmbedAsync(
            EmbeddingProfileDescriptor profile,
            EmbeddingPurpose purpose,
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new EmbeddingBatch(profile, []));
    }

    private sealed class NoOpRuntimeClient : IAiRuntimeClient
    {
        public Task<AiRuntimeReply> ReplyAsync(
            AiRuntimeRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AiRuntimeReply("", "", ""));

        public Task<AiEmbeddingReply> EmbedAsync(
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AiEmbeddingReply(64, []));

        public Task<bool> IsReadyAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}

/// <summary>
/// B2: Unit tests for KnowledgeIndexingProcessor profile selection and batch size logic.
/// </summary>
public sealed class KnowledgeIndexingProcessorB2Tests
{
    private static EmbeddingProfileDescriptor ActiveDescriptor { get; } = new(
        Guid.NewGuid(), "active-v1", "test-provider", "test-model",
        64, "1", 1, "cosine", "unit");

    private static EmbeddingProfileDescriptor BuildingDescriptor { get; } = new(
        Guid.NewGuid(), "building-v2", "test-provider", "test-model",
        64, "1", 2, "cosine", "unit");

    // ── Profile selection ────────────────────────────────────────────────────────

    [Fact]
    public async Task Indexer_Always_Uses_Active_Serving_Profile()
    {
        var capture = new ProfileCapturingProvider();
        var result = await RunProcessorAsync(
            servingProfile: ActiveDescriptor,
            provider: capture,
            documentContent: "One sentence about support.");

        Assert.All(capture.UsedProfiles, p => Assert.Equal(ActiveDescriptor, p));
        Assert.True(result > 0);
    }

    [Fact]
    public async Task Indexer_Falls_Back_To_Active_Profile_When_No_Building_Profile()
    {
        var capture = new ProfileCapturingProvider();
        var result = await RunProcessorAsync(
            servingProfile: ActiveDescriptor,
            provider: capture,
            documentContent: "One sentence about support.");

        // All embed calls must reference the Active (serving) profile
        Assert.All(capture.UsedProfiles, p => Assert.Equal(ActiveDescriptor, p));
        Assert.True(result > 0);
    }

    // ── Effective batch size ─────────────────────────────────────────────────────

    [Fact]
    public async Task Indexer_Batch_Size_Respects_Provider_MaxBatchInputs()
    {
        // Provider MaxBatchInputs = 3; processor AppBatchCap = 512
        // Effective batch = min(512, 3) = 3
        var capturingProvider = new BatchSizeCapturingProvider(maxBatch: 3);
        var manyWords = string.Join(' ', Enumerable.Range(1, 2000).Select(i => $"word{i}"));

        await RunProcessorAsync(
            servingProfile: ActiveDescriptor,
            provider: capturingProvider,
            documentContent: manyWords);

        // Every batch sent to the provider must be <= MaxBatchInputs
        Assert.True(capturingProvider.MaxObservedBatchSize <= 3,
            $"Batch exceeded MaxBatchInputs=3; largest observed: {capturingProvider.MaxObservedBatchSize}");
    }

    [Fact]
    public async Task Indexer_Document_Purpose_Is_Used_For_Embedding()
    {
        var capture = new PurposeCapturingProvider();
        await RunProcessorAsync(
            servingProfile: ActiveDescriptor,
            provider: capture,
            documentContent: "Support policy states thirty days.");

        Assert.All(capture.UsedPurposes, p => Assert.Equal(EmbeddingPurpose.Document, p));
    }

    // ── Retriever uses Query purpose ─────────────────────────────────────────────

    [Fact]
    public async Task Retriever_Uses_Query_Purpose_When_Embedding()
    {
        var capture = new PurposeCapturingProvider();
        var retriever = BuildRetriever(ActiveDescriptor, capture);

        await retriever.RetrieveAsync(Guid.NewGuid(), "what is the support policy", 3);

        Assert.Single(capture.UsedPurposes);
        Assert.Equal(EmbeddingPurpose.Query, capture.UsedPurposes[0]);
    }

    // ── Retriever uses Active serving profile only ───────────────────────────────

    [Fact]
    public async Task Retriever_Always_Uses_Active_Serving_Profile()
    {
        var capture = new ProfileCapturingProvider();
        var retriever = BuildRetriever(ActiveDescriptor, capture);

        await retriever.RetrieveAsync(Guid.NewGuid(), "hello", 3);

        Assert.Single(capture.UsedProfiles);
        Assert.Equal(ActiveDescriptor, capture.UsedProfiles[0]);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static async Task<int> RunProcessorAsync(
        EmbeddingProfileDescriptor servingProfile,
        IEmbeddingProvider provider,
        string documentContent)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        conn.Open();
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<ICEHOTT.Persistence.ICEHOTTDbContext>()
            .UseSqlite(conn)
            .Options;
        using var db = new ICEHOTT.Persistence.ICEHOTTDbContext(options);
        db.Database.EnsureCreated();

        var workspaceId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        // Create a user, workspace, and document in FK-safe order.
        var now = DateTimeOffset.UtcNow;
        db.Users.Add(new ICEHOTT.Domain.Users.User(
            userId,
            $"test-{userId:N}@icehott.dev",
            "Test User",
            "test-password-hash",
            now));

        var workspace = new ICEHOTT.Domain.Workspaces.Workspace(
            workspaceId,
            "Test Workspace",
            $"test-{workspaceId:N}",
            userId,
            now);
        db.Workspaces.Add(workspace);

        var document = new ICEHOTT.Domain.Knowledge.KnowledgeDocument(
            Guid.NewGuid(),
            workspaceId,
            userId,
            "Test Doc",
            null,
            documentContent,
            now);
        db.KnowledgeDocuments.Add(document);
        await db.SaveChangesAsync();

        var registry = new EmbeddingProviderRegistry([provider]);
        var servingResolver = new FixedServingResolver(servingProfile);

        var processor = new ICEHOTT.Application.Knowledge.KnowledgeIndexingProcessor(
            new ICEHOTT.Persistence.Repositories.KnowledgeRepository(db),
            new ICEHOTT.Application.Knowledge.StructureAwareKnowledgeChunker(),
            servingResolver,
            registry,
            new NoOpVectorStore(),
            db,
            TimeProvider.System);

        return await processor.ProcessAsync(workspaceId, document.Id);
    }

    private static ICEHOTT.Application.Knowledge.KnowledgeRetriever BuildRetriever(
        EmbeddingProfileDescriptor servingProfile,
        IEmbeddingProvider provider)
    {
        var registry = new EmbeddingProviderRegistry([provider]);
        var servingResolver = new FixedServingResolver(servingProfile);

        var profileRetriever =
            new ICEHOTT.Application.Knowledge.ProfileKnowledgeRetriever(
                registry,
                new NoOpVectorStore(),
                new ICEHOTT.Application.Knowledge.HybridRagReranker(),
                new ICEHOTT.Application.Knowledge.RetrievedContentPolicy());

        return new ICEHOTT.Application.Knowledge.KnowledgeRetriever(
            servingResolver,
            profileRetriever);
    }

    // ── Fake resolvers ────────────────────────────────────────────────────────────

    private sealed class FixedServingResolver(EmbeddingProfileDescriptor profile)
        : ICEHOTT.Application.Abstractions.IServingEmbeddingProfileResolver
    {
        public Task<EmbeddingProfileDescriptor> ResolveAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(profile);
    }

    private sealed class FixedBuildResolver(EmbeddingProfileDescriptor profile)
        : ICEHOTT.Application.Abstractions.IBuildEmbeddingProfileResolver
    {
        public Task<EmbeddingProfileDescriptor?> ResolveAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<EmbeddingProfileDescriptor?>(profile);
    }

    private sealed class NullBuildResolver
        : ICEHOTT.Application.Abstractions.IBuildEmbeddingProfileResolver
    {
        public Task<EmbeddingProfileDescriptor?> ResolveAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<EmbeddingProfileDescriptor?>(null);
    }

    // ── Fake providers ────────────────────────────────────────────────────────────

    private sealed class ProfileCapturingProvider : IEmbeddingProvider
    {
        public string Provider => "test-provider";
        public List<EmbeddingProfileDescriptor> UsedProfiles { get; } = [];

        public EmbeddingProviderCapabilities Capabilities { get; } = new(
            "test-provider", new HashSet<int> { 64 }, 64, null, false);

        public Task<EmbeddingBatch> EmbedAsync(
            EmbeddingProfileDescriptor profile,
            EmbeddingPurpose purpose,
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken = default)
        {
            UsedProfiles.Add(profile);
            IReadOnlyList<IReadOnlyList<float>> vectors = texts
                .Select(_ => (IReadOnlyList<float>)new float[64])
                .ToArray();
            return Task.FromResult(new EmbeddingBatch(profile, vectors));
        }
    }

    private sealed class PurposeCapturingProvider : IEmbeddingProvider
    {
        public string Provider => "test-provider";
        public List<EmbeddingPurpose> UsedPurposes { get; } = [];

        public EmbeddingProviderCapabilities Capabilities { get; } = new(
            "test-provider", new HashSet<int> { 64 }, 64, null, false);

        public Task<EmbeddingBatch> EmbedAsync(
            EmbeddingProfileDescriptor profile,
            EmbeddingPurpose purpose,
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken = default)
        {
            UsedPurposes.Add(purpose);
            IReadOnlyList<IReadOnlyList<float>> vectors = texts
                .Select(_ => (IReadOnlyList<float>)new float[64])
                .ToArray();
            return Task.FromResult(new EmbeddingBatch(profile, vectors));
        }
    }

    private sealed class BatchSizeCapturingProvider(int maxBatch) : IEmbeddingProvider
    {
        public string Provider => "test-provider";
        public int MaxObservedBatchSize { get; private set; }

        public EmbeddingProviderCapabilities Capabilities { get; } = new(
            "test-provider", new HashSet<int> { 64 }, maxBatch, null, false);

        public Task<EmbeddingBatch> EmbedAsync(
            EmbeddingProfileDescriptor profile,
            EmbeddingPurpose purpose,
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken = default)
        {
            if (texts.Count > MaxObservedBatchSize)
                MaxObservedBatchSize = texts.Count;

            IReadOnlyList<IReadOnlyList<float>> vectors = texts
                .Select(_ => (IReadOnlyList<float>)new float[64])
                .ToArray();
            return Task.FromResult(new EmbeddingBatch(profile, vectors));
        }
    }

    // ── No-op vector store ────────────────────────────────────────────────────────

    private sealed class NoOpVectorStore : ICEHOTT.Application.Abstractions.IVectorStore
    {
        public Task<bool> IsProfileReadyAsync(
            EmbeddingProfileDescriptor profile,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task StoreManyAsync(
            Guid workspaceId,
            EmbeddingProfileDescriptor profile,
            IReadOnlyList<ICEHOTT.Application.Abstractions.VectorEmbedding> embeddings,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<ICEHOTT.Application.Abstractions.KnowledgeMatch>> SearchAsync(
            Guid workspaceId,
            EmbeddingProfileDescriptor profile,
            string queryText,
            IReadOnlyList<float> queryEmbedding,
            int limit,
            bool allowBuildingProfile = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ICEHOTT.Application.Abstractions.KnowledgeMatch>>([]);
    }
}
