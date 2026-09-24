using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Knowledge;
using ICEHOTT.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IDbContextOptionsConfiguration<ICEHOTTDbContext>>();
            services.RemoveAll<DbContextOptions<ICEHOTTDbContext>>();
            services.RemoveAll<ICEHOTTDbContext>();
            services.RemoveAll<IAiRuntimeClient>();
            services.RemoveAll<IVectorStore>();

            services.AddDbContext<ICEHOTTDbContext>(options => options.UseSqlite(_connection));
            services.AddSingleton<IAiRuntimeClient, FakeAiRuntimeClient>();
            services.AddScoped<IVectorStore, FakeVectorStore>();
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _connection.Dispose();
    }

    private sealed class FakeAiRuntimeClient : IAiRuntimeClient
    {
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

    private sealed class FakeVectorStore(ICEHOTTDbContext db) : IVectorStore
    {
        public Task StoreManyAsync(
            Guid workspaceId,
            IReadOnlyList<VectorEmbedding> embeddings,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public async Task<IReadOnlyList<KnowledgeMatch>> SearchAsync(
            Guid workspaceId,
            string queryText,
            IReadOnlyList<float> queryEmbedding,
            int limit,
            CancellationToken cancellationToken = default)
        {
            var documents = await db.KnowledgeDocuments.AsNoTracking()
                .Where(x => x.WorkspaceId == workspaceId && x.Status == KnowledgeDocumentStatus.Ready)
                .ToListAsync(cancellationToken);

            var chunks = await db.KnowledgeChunks.AsNoTracking()
                .Where(x => x.WorkspaceId == workspaceId)
                .ToListAsync(cancellationToken);

            var matches = (
                from chunk in chunks
                join document in documents on chunk.DocumentId equals document.Id
                orderby chunk.Ordinal
                select new KnowledgeMatch(
                    chunk.Id,
                    document.Id,
                    document.Title,
                    document.SourceName,
                    chunk.Content,
                    0.95))
                .Take(Math.Clamp(limit, 1, 10))
                .ToArray();

            return matches;
        }
    }
}
