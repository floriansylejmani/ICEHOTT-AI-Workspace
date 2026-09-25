using ICEHOTT.API.Background;
using ICEHOTT.Application.Abstractions;
using ICEHOTT.Application.Knowledge;
using ICEHOTT.Domain.Knowledge;
using ICEHOTT.Infrastructure.Ai;
using ICEHOTT.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace ICEHOTT.Tests;

public sealed class IcehottApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public IcehottApiFactory()
    {
        _connection.Open();
        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICEHOTTDbContext>().Database.EnsureCreated();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("KnowledgeWorker:Enabled", "false");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IDbContextOptionsConfiguration<ICEHOTTDbContext>>();
            services.RemoveAll<DbContextOptions<ICEHOTTDbContext>>();
            services.RemoveAll<ICEHOTTDbContext>();
            services.RemoveAll<IAiRuntimeClient>();
            // Remove real provider and registry; replace with fakes
            services.RemoveAll<IEmbeddingProvider>();
            services.RemoveAll<IEmbeddingProviderRegistry>();
            services.RemoveAll<IVectorStore>();
            services.RemoveAll<IHostedService>();

            services.AddDbContext<ICEHOTTDbContext>(options => options.UseSqlite(_connection));
            services.AddSingleton<IAiRuntimeClient, FakeAiRuntimeClient>();
            // FakeEmbeddingProvider registered as IEmbeddingProvider so EmbeddingProviderRegistry picks it up
            services.AddSingleton<IEmbeddingProvider, FakeEmbeddingProvider>();
            services.AddSingleton<IEmbeddingProviderRegistry, EmbeddingProviderRegistry>();
            services.AddScoped<IVectorStore, FakeVectorStore>();
            // IEmbeddingProfileRepository, IServingEmbeddingProfileResolver, and
            // IBuildEmbeddingProfileResolver are registered by Program.cs and will use
            // the SQLite ICEHOTTDbContext replaced above — no override needed.
        });
    }

    public async Task<bool> ProcessNextKnowledgeJobAsync()
    {
        using var scope = Services.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<IKnowledgeJobQueue>();
        var processor = scope.ServiceProvider.GetRequiredService<KnowledgeIndexingProcessor>();

        var lease = await queue.LeaseNextAsync(
            "integration-test-worker",
            TimeSpan.FromMinutes(2));
        if (lease is null) return false;

        await processor.ProcessAsync(lease.WorkspaceId, lease.DocumentId);
        await queue.CompleteAsync(lease.Id, lease.WorkerId);
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _connection.Dispose();
    }

    private sealed class FakeAiRuntimeClient : IAiRuntimeClient
    {
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<AiRuntimeReply> ReplyAsync(
            AiRuntimeRequest request,
            CancellationToken cancellationToken = default)
        {
            var lastUser = request.Messages.Last(x => x.Role == "user").Content;
            return Task.FromResult(new AiRuntimeReply(
                $"Test ICEHOTT reply: {lastUser} | knowledge={request.Knowledge.Count}",
                "test-runtime",
                "test-model"));
        }

        public Task<AiEmbeddingReply> EmbedAsync(
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken = default)
        {
            if (texts.Count > 64)
                throw new AiRuntimeUnavailableException("Test embedding runtime accepts at most 64 texts.");

            IReadOnlyList<IReadOnlyList<float>> vectors = texts
                .Select(text =>
                {
                    var vector = new float[64];
                    vector[Math.Abs(text.Length) % vector.Length] = 1f;
                    return (IReadOnlyList<float>)vector;
                })
                .ToArray();

            return Task.FromResult(new AiEmbeddingReply(64, vectors));
        }
    }

    /// <summary>
    /// Fake embedding provider for tests. Provider name "icehott-ai-runtime" matches the
    /// seeded baseline Active profile so the registry resolves it correctly.
    /// </summary>
    private sealed class FakeEmbeddingProvider(IAiRuntimeClient runtime) :
        IEmbeddingProvider,
        IEmbeddingProviderConfigurationProbe
    {
        private const string ProviderName = "icehott-ai-runtime";

        public string Provider => ProviderName;

        public EmbeddingProviderCapabilities Capabilities { get; } = new(
            ProviderName,
            new HashSet<int> { 64 },
            MaxBatchInputs: 64,
            MaxInputTokens: null,
            SupportsPurposeRouting: false);

        public bool IsConfigured(EmbeddingProfileDescriptor profile) =>
            profile == EmbeddingProfileDefaults.LocalDeterministic64;

        public async Task<EmbeddingBatch> EmbedAsync(
            EmbeddingProfileDescriptor profile,
            EmbeddingPurpose purpose,
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken = default)
        {
            var reply = await runtime.EmbedAsync(texts, cancellationToken);

            if (reply.Dimensions != profile.Dimensions)
                throw new EmbeddingProviderException(
                    $"Fake provider: runtime returned {reply.Dimensions} dimensions but profile requires {profile.Dimensions}.",
                    EmbeddingFailureKind.ProfileMismatch);

            return new EmbeddingBatch(profile, reply.Embeddings);
        }
    }

    private sealed class FakeVectorStore(ICEHOTTDbContext db) : IVectorStore
    {
        public Task<bool> IsProfileReadyAsync(
            EmbeddingProfileDescriptor profile,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(profile == EmbeddingProfileDefaults.LocalDeterministic64);

        public Task StoreManyAsync(
            Guid workspaceId,
            EmbeddingProfileDescriptor profile,
            IReadOnlyList<VectorEmbedding> embeddings,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public async Task<IReadOnlyList<KnowledgeMatch>> SearchAsync(
            Guid workspaceId,
            EmbeddingProfileDescriptor profile,
            string queryText,
            IReadOnlyList<float> queryEmbedding,
            int limit,
            bool allowBuildingProfile = false,
            CancellationToken cancellationToken = default)
        {
            var documents = await db.KnowledgeDocuments.AsNoTracking()
                .Where(x => x.WorkspaceId == workspaceId && x.Status == KnowledgeDocumentStatus.Ready)
                .ToListAsync(cancellationToken);

            var chunks = await db.KnowledgeChunks.AsNoTracking()
                .Where(x => x.WorkspaceId == workspaceId)
                .ToListAsync(cancellationToken);

            var stopWords = new HashSet<string>(
                ["the", "and", "for", "are", "what", "how", "long", "with", "from", "that", "this", "into"],
                StringComparer.OrdinalIgnoreCase);

            var terms = queryText
                .Split(new[] { ' ', '\t', '\r', '\n', '.', ',', '?', '!', ':', ';' },
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(term => term.Length >= 3 && !stopWords.Contains(term))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var matches = (
                from chunk in chunks
                join document in documents on chunk.DocumentId equals document.Id
                let termHits = terms.Count(term =>
                    chunk.Content.Contains(term, StringComparison.OrdinalIgnoreCase))
                let score = termHits == 0
                    ? 0.05
                    : Math.Min(0.99, 0.55 + termHits * 0.12)
                orderby score descending, chunk.Ordinal
                select new KnowledgeMatch(
                    chunk.Id,
                    document.Id,
                    document.Title,
                    document.SourceName,
                    chunk.Content,
                    score))
                .Take(Math.Clamp(limit, 1, 30))
                .ToArray();

            return matches;
        }
    }
}
