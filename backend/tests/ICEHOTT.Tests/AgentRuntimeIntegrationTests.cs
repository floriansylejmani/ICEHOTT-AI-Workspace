using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ICEHOTT.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ICEHOTT.Tests;

public sealed class AgentRuntimeIntegrationTests : IClassFixture<IcehottApiFactory>
{
    private readonly IcehottApiFactory _factory;

    public AgentRuntimeIntegrationTests(IcehottApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Chat_Persists_History_And_Is_Isolated_By_Workspace()
    {
        using var owner = _factory.CreateClient();
        var ownerIdentity = await RegisterAsync(owner, $"agent-owner-{Guid.NewGuid():N}@icehott.dev");
        owner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerIdentity.Token);

        var workspaceResponse = await owner.PostAsJsonAsync("/api/workspaces", new { name = "Agent Workspace" });
        workspaceResponse.EnsureSuccessStatusCode();
        var workspaceId = JsonDocument.Parse(await workspaceResponse.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        var firstTurn = await owner.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/conversations/chat",
            new { conversationId = (Guid?)null, content = "Hello Phase 2" });

        Assert.Equal(HttpStatusCode.OK, firstTurn.StatusCode);
        var firstPayload = JsonDocument.Parse(await firstTurn.Content.ReadAsStringAsync());
        var conversationId = firstPayload.RootElement.GetProperty("conversationId").GetGuid();
        Assert.Equal("test-runtime", firstPayload.RootElement.GetProperty("provider").GetString());

        var secondTurn = await owner.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/conversations/chat",
            new { conversationId, content = "Keep the context" });
        Assert.Equal(HttpStatusCode.OK, secondTurn.StatusCode);

        var historyResponse = await owner.GetAsync(
            $"/api/workspaces/{workspaceId}/conversations/{conversationId}");
        historyResponse.EnsureSuccessStatusCode();
        var history = JsonDocument.Parse(await historyResponse.Content.ReadAsStringAsync());
        Assert.Equal(4, history.RootElement.GetProperty("messages").GetArrayLength());

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ICEHOTTDbContext>();
            Assert.Equal(4, await db.ConversationMessages.CountAsync(
                x => x.WorkspaceId == workspaceId && x.ConversationId == conversationId));
        }

        using var outsider = _factory.CreateClient();
        var outsiderIdentity = await RegisterAsync(outsider, $"agent-outsider-{Guid.NewGuid():N}@icehott.dev");
        outsider.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", outsiderIdentity.Token);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await outsider.GetAsync($"/api/workspaces/{workspaceId}/conversations/{conversationId}")).StatusCode);
    }

    private static async Task<RegisteredIdentity> RegisterAsync(HttpClient client, string email)
    {
        var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email,
            displayName = "Agent Test User",
            password = "StrongPassword123!"
        });

        response.EnsureSuccessStatusCode();
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return new RegisteredIdentity(json.RootElement.GetProperty("accessToken").GetString()!);
    }

    private sealed record RegisteredIdentity(string Token);
}
