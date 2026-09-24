using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ICEHOTT.Application.Abstractions;
using ICEHOTT.Application.Knowledge;
using ICEHOTT.Domain.Knowledge;
using ICEHOTT.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ICEHOTT.Tests;

public sealed class KnowledgeRagIntegrationTests : IClassFixture<IcehottApiFactory>
{
    private readonly IcehottApiFactory _factory;

    public KnowledgeRagIntegrationTests(IcehottApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Knowledge_Is_Ingested_Searched_And_Cited_Inside_Workspace()
    {
        using var owner = _factory.CreateClient();
        var ownerIdentity = await RegisterAsync(owner, $"knowledge-owner-{Guid.NewGuid():N}@icehott.dev");
        owner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerIdentity.Token);

        var workspaceResponse = await owner.PostAsJsonAsync("/api/workspaces", new { name = "RAG Workspace" });
        workspaceResponse.EnsureSuccessStatusCode();
        var workspaceId = JsonDocument.Parse(await workspaceResponse.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        var content = string.Join(" ", Enumerable.Range(1, 190)
            .Select(index => index == 50
                ? "ICEHOTT knowledge says the support window is thirty days."
                : $"workspace-word-{index}"));

        var ingest = await owner.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/knowledge/documents",
            new
            {
                title = "Support Policy",
                sourceName = "support-policy.txt",
                content
            });

        Assert.Equal(HttpStatusCode.Accepted, ingest.StatusCode);
        var ingested = JsonDocument.Parse(await ingest.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Queued", ingested.GetProperty("status").GetString());

        await ProcessQueuedKnowledgeAsync(workspaceId);
        var listed = await owner.GetAsync($"/api/workspaces/{workspaceId}/knowledge/documents");
        listed.EnsureSuccessStatusCode();
        var indexed = JsonDocument.Parse(await listed.Content.ReadAsStringAsync()).RootElement[0];
        Assert.Equal("Ready", indexed.GetProperty("status").GetString());
        Assert.True(indexed.GetProperty("chunkCount").GetInt32() >= 1);

        var search = await owner.GetAsync(
            $"/api/workspaces/{workspaceId}/knowledge/search?query=support%20window&limit=5");
        search.EnsureSuccessStatusCode();
        var searchJson = JsonDocument.Parse(await search.Content.ReadAsStringAsync());
        Assert.True(searchJson.RootElement.GetArrayLength() >= 1);
        Assert.Equal("Support Policy", searchJson.RootElement[0].GetProperty("title").GetString());

        var chat = await owner.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/conversations/chat",
            new { conversationId = (Guid?)null, content = "What is the support window?" });
        chat.EnsureSuccessStatusCode();

        var chatJson = JsonDocument.Parse(await chat.Content.ReadAsStringAsync()).RootElement;
        var assistant = chatJson.GetProperty("assistantMessage");
        Assert.Contains("knowledge=", assistant.GetProperty("content").GetString());
        Assert.True(assistant.GetProperty("citations").GetArrayLength() >= 1);
        Assert.Equal(
            "Support Policy",
            assistant.GetProperty("citations")[0].GetProperty("title").GetString());

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ICEHOTTDbContext>();
            Assert.True(await db.KnowledgeDocuments.AnyAsync(x => x.WorkspaceId == workspaceId));
            Assert.True(await db.KnowledgeChunks.CountAsync(x => x.WorkspaceId == workspaceId) >= 1);
            Assert.True(await db.ConversationMessageCitations.AnyAsync(x => x.WorkspaceId == workspaceId));
        }

        using var outsider = _factory.CreateClient();
        var outsiderIdentity = await RegisterAsync(outsider, $"knowledge-outsider-{Guid.NewGuid():N}@icehott.dev");
        outsider.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", outsiderIdentity.Token);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await outsider.GetAsync(
                $"/api/workspaces/{workspaceId}/knowledge/search?query=support")).StatusCode);
    }

    [Fact]
    public async Task Large_Knowledge_Is_Embedded_In_Batches()
    {
        using var owner = _factory.CreateClient();
        var identity = await RegisterAsync(owner, $"batch-owner-{Guid.NewGuid():N}@icehott.dev");
        owner.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", identity.Token);

        var workspaceResponse = await owner.PostAsJsonAsync(
            "/api/workspaces",
            new { name = "Batch Workspace" });
        workspaceResponse.EnsureSuccessStatusCode();
        var workspaceId = JsonDocument.Parse(await workspaceResponse.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        var content = string.Join(' ', Enumerable.Range(1, 8000).Select(index => $"word{index}"));
        var ingest = await owner.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/knowledge/documents",
            new
            {
                title = "Large Knowledge",
                sourceName = "large.txt",
                content
            });

        Assert.Equal(HttpStatusCode.Accepted, ingest.StatusCode);
        await ProcessQueuedKnowledgeAsync(workspaceId);

        var list = await owner.GetAsync($"/api/workspaces/{workspaceId}/knowledge/documents");
        list.EnsureSuccessStatusCode();
        var payload = JsonDocument.Parse(await list.Content.ReadAsStringAsync()).RootElement[0];
        Assert.Equal("Ready", payload.GetProperty("status").GetString());
        Assert.True(payload.GetProperty("chunkCount").GetInt32() > 1);
    }

    [Fact]
    public async Task Text_File_Can_Be_Uploaded_And_Deleted_Inside_Workspace()
    {
        using var owner = _factory.CreateClient();
        var identity = await RegisterAsync(owner, $"upload-owner-{Guid.NewGuid():N}@icehott.dev");
        owner.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", identity.Token);

        var workspaceResponse = await owner.PostAsJsonAsync(
            "/api/workspaces",
            new { name = "Upload Workspace" });
        workspaceResponse.EnsureSuccessStatusCode();
        var workspaceId = JsonDocument.Parse(await workspaceResponse.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        using var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent("Uploaded Policy"), "Title");
        var fileContent = new ByteArrayContent(
            Encoding.UTF8.GetBytes("Uploaded knowledge says support is available for thirty days."));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        multipart.Add(fileContent, "File", "../uploaded-policy.txt");

        var upload = await owner.PostAsync(
            $"/api/workspaces/{workspaceId}/knowledge/documents/upload",
            multipart);
        Assert.Equal(HttpStatusCode.Accepted, upload.StatusCode);

        var uploaded = JsonDocument.Parse(await upload.Content.ReadAsStringAsync()).RootElement;
        var documentId = uploaded.GetProperty("id").GetGuid();
        Assert.Equal("Queued", uploaded.GetProperty("status").GetString());
        Assert.Equal("uploaded-policy.txt", uploaded.GetProperty("sourceName").GetString());

        await ProcessQueuedKnowledgeAsync(workspaceId);

        var delete = await owner.DeleteAsync(
            $"/api/workspaces/{workspaceId}/knowledge/documents/{documentId}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var list = await owner.GetAsync(
            $"/api/workspaces/{workspaceId}/knowledge/documents");
        list.EnsureSuccessStatusCode();
        var documents = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        Assert.Empty(documents.RootElement.EnumerateArray());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ICEHOTTDbContext>();
        Assert.False(await db.KnowledgeDocuments.AnyAsync(x => x.Id == documentId));
        Assert.False(await db.KnowledgeChunks.AnyAsync(x => x.DocumentId == documentId));
    }

    [Fact]
    public async Task Database_Rejects_Cross_Workspace_Knowledge_Chunk()
    {
        using var owner = _factory.CreateClient();
        var identity = await RegisterAsync(owner, $"tenant-chunk-{Guid.NewGuid():N}@icehott.dev");
        owner.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", identity.Token);

        var workspaceOneResponse = await owner.PostAsJsonAsync(
            "/api/workspaces",
            new { name = "Tenant One" });
        workspaceOneResponse.EnsureSuccessStatusCode();
        var workspaceOneId = JsonDocument.Parse(
            await workspaceOneResponse.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        var workspaceTwoResponse = await owner.PostAsJsonAsync(
            "/api/workspaces",
            new { name = "Tenant Two" });
        workspaceTwoResponse.EnsureSuccessStatusCode();
        var workspaceTwoId = JsonDocument.Parse(
            await workspaceTwoResponse.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        var ingest = await owner.PostAsJsonAsync(
            $"/api/workspaces/{workspaceOneId}/knowledge/documents",
            new
            {
                title = "Tenant Integrity",
                sourceName = "integrity.txt",
                content = "Workspace one knowledge."
            });
        ingest.EnsureSuccessStatusCode();
        var documentId = JsonDocument.Parse(await ingest.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ICEHOTTDbContext>();
        db.KnowledgeChunks.Add(new KnowledgeChunk(
            Guid.NewGuid(),
            documentId,
            workspaceTwoId,
            999,
            "Cross-workspace chunk must fail.",
            DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Reindex_Is_Idempotent_While_Job_Is_Active()
    {
        using var owner = _factory.CreateClient();
        var identity = await RegisterAsync(owner, $"reindex-owner-{Guid.NewGuid():N}@icehott.dev");
        owner.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", identity.Token);

        var workspaceResponse = await owner.PostAsJsonAsync(
            "/api/workspaces",
            new { name = "Reindex Workspace" });
        workspaceResponse.EnsureSuccessStatusCode();
        var workspaceId = JsonDocument.Parse(
            await workspaceResponse.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        var ingest = await owner.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/knowledge/documents",
            new
            {
                title = "Reindex Policy",
                sourceName = "reindex.txt",
                content = "Production RAG reindexing must be idempotent."
            });
        Assert.Equal(HttpStatusCode.Accepted, ingest.StatusCode);
        var documentId = JsonDocument.Parse(await ingest.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        var duplicateReindex = await owner.PostAsync(
            $"/api/workspaces/{workspaceId}/knowledge/documents/{documentId}/reindex",
            content: null);
        Assert.Equal(HttpStatusCode.Accepted, duplicateReindex.StatusCode);
        var duplicatePayload = JsonDocument.Parse(
            await duplicateReindex.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Queued", duplicatePayload.GetProperty("status").GetString());

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ICEHOTTDbContext>();
            Assert.Equal(
                1,
                await db.KnowledgeProcessingJobs.CountAsync(
                    x => x.DocumentId == documentId && x.WorkspaceId == workspaceId));
        }

        await ProcessQueuedKnowledgeAsync(workspaceId);

        var reindex = await owner.PostAsync(
            $"/api/workspaces/{workspaceId}/knowledge/documents/{documentId}/reindex",
            content: null);
        Assert.Equal(HttpStatusCode.Accepted, reindex.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ICEHOTTDbContext>();
            var job = await db.KnowledgeProcessingJobs.SingleAsync(
                x => x.DocumentId == documentId && x.WorkspaceId == workspaceId);
            Assert.Equal(KnowledgeProcessingStatus.Queued, job.Status);
            Assert.Equal(0, job.Attempts);
        }
    }

    [Fact]
    public async Task Job_Lease_Ownership_Rejects_Stale_Worker_Mutations()
    {
        using var owner = _factory.CreateClient();
        var identity = await RegisterAsync(owner, $"lease-owner-{Guid.NewGuid():N}@icehott.dev");
        owner.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", identity.Token);

        var workspaceResponse = await owner.PostAsJsonAsync(
            "/api/workspaces",
            new { name = "Lease Workspace" });
        workspaceResponse.EnsureSuccessStatusCode();
        var workspaceId = JsonDocument.Parse(
            await workspaceResponse.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        var ingest = await owner.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/knowledge/documents",
            new
            {
                title = "Lease Policy",
                sourceName = "lease.txt",
                content = "Only the active worker may mutate a processing lease."
            });
        ingest.EnsureSuccessStatusCode();
        var documentId = JsonDocument.Parse(await ingest.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        using var scope = _factory.Services.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<IKnowledgeJobQueue>();
        var processor = scope.ServiceProvider.GetRequiredService<KnowledgeIndexingProcessor>();

        KnowledgeJobLease? lease = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var candidate = await queue.LeaseNextAsync(
                "worker-owner",
                TimeSpan.FromMinutes(2));

            if (candidate is null) break;
            if (candidate.DocumentId == documentId)
            {
                lease = candidate;
                break;
            }

            await queue.CompleteAsync(candidate.Id, candidate.WorkerId);
        }

        Assert.NotNull(lease);
        Assert.False(await queue.CompleteAsync(lease!.Id, "stale-worker"));
        Assert.False(await queue.RetryAsync(
            lease.Id,
            "stale-worker",
            "must not mutate",
            DateTimeOffset.UtcNow));

        await processor.ProcessAsync(lease.WorkspaceId, lease.DocumentId);
        Assert.True(await queue.CompleteAsync(lease.Id, lease.WorkerId));
    }

    private async Task ProcessQueuedKnowledgeAsync(Guid workspaceId)
    {
        using var scope = _factory.Services.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<IKnowledgeJobQueue>();
        var processor = scope.ServiceProvider.GetRequiredService<KnowledgeIndexingProcessor>();
        KnowledgeJobLease? lease = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var candidate = await queue.LeaseNextAsync("test-worker", TimeSpan.FromMinutes(2));
            if (candidate is null) break;

            if (candidate.WorkspaceId == workspaceId)
            {
                lease = candidate;
                break;
            }

            await processor.ProcessAsync(candidate.WorkspaceId, candidate.DocumentId);
            await queue.CompleteAsync(candidate.Id, candidate.WorkerId);
        }

        Assert.NotNull(lease);
        await processor.ProcessAsync(lease!.WorkspaceId, lease.DocumentId);
        await queue.CompleteAsync(lease.Id, lease.WorkerId);
    }

    private static async Task<RegisteredIdentity> RegisterAsync(HttpClient client, string email)
    {
        var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email,
            displayName = "Knowledge Test User",
            password = "StrongPassword123!"
        });

        response.EnsureSuccessStatusCode();
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return new RegisteredIdentity(json.RootElement.GetProperty("accessToken").GetString()!);
    }

    private sealed record RegisteredIdentity(string Token);
}
