using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ICEHOTT.Domain.Workspaces;
using ICEHOTT.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ICEHOTT.Tests;

public sealed class WorkflowAdministrationIntegrationTests : IClassFixture<IcehottApiFactory>
{
    private readonly IcehottApiFactory _factory;
    public WorkflowAdministrationIntegrationTests(IcehottApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Owner_Can_Create_Activate_Run_Idempotently_And_Cancel()
    {
        using var client = _factory.CreateClient();
        var identity = await RegisterAsync(client, $"wf-admin-{Guid.NewGuid():N}@icehott.dev");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", identity.Token);
        var workspaceId = await CreateWorkspaceAsync(client, "Workflow Admin");

        var create = await client.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/workflows",
            new { name = "Release Gate", description = "phase 5h", minimumRunRole = "Member" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var workflowId = await ReadGuidAsync(create, "id");

        var invalid = await client.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/workflows/{workflowId}/versions",
            new
            {
                definition = new
                {
                    steps = new[]
                    {
                        new { key = "a", type = "delay", delaySeconds = 0, dependsOn = new[] { "b" } },
                        new { key = "b", type = "delay", delaySeconds = 0, dependsOn = new[] { "a" } }
                    }
                }
            });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var versionResponse = await client.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/workflows/{workflowId}/versions",
            new
            {
                definition = new
                {
                    steps = new[]
                    {
                        new { key = "gate", type = "checkpoint", minimumApproverRole = "Owner",
                            requiresDifferentApprover = false }
                    }
                }
            });
        Assert.Equal(HttpStatusCode.Created, versionResponse.StatusCode);
        var versionId = await ReadGuidAsync(versionResponse, "id");

        var activate = await client.PostAsync(
            $"/api/workspaces/{workspaceId}/workflows/{workflowId}/versions/{versionId}/activate",
            null);
        Assert.Equal(HttpStatusCode.OK, activate.StatusCode);

        var idempotencyKey = $"phase5h-{Guid.NewGuid():N}";
        var firstRun = await client.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/workflow-runs",
            new { workflowDefinitionId = workflowId, idempotencyKey });
        Assert.Equal(HttpStatusCode.Created, firstRun.StatusCode);
        var runId = await ReadGuidAsync(firstRun, "id");

        var replay = await client.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/workflow-runs",
            new { workflowDefinitionId = workflowId, idempotencyKey });
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(runId, await ReadGuidAsync(replay, "id"));

        var retry = await client.PostAsync(
            $"/api/workspaces/{workspaceId}/workflow-runs/{runId}/retry", null);
        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);

        var cancel = await client.PostAsync(
            $"/api/workspaces/{workspaceId}/workflow-runs/{runId}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        var cancelJson = await cancel.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":\"Cancelled\"", cancelJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Workflow_Mutations_Are_Role_Gated_And_Nondisclosing()
    {
        using var owner = _factory.CreateClient();
        var identity = await RegisterAsync(owner, $"wf-role-{Guid.NewGuid():N}@icehott.dev");
        owner.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", identity.Token);
        var workspaceId = await CreateWorkspaceAsync(owner, "Role Gate");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ICEHOTTDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE workspace_memberships SET \"Role\" = {0} " +
                "WHERE \"WorkspaceId\" = {1} AND \"UserId\" = {2}",
                "Member", workspaceId, identity.UserId);
        }

        var forbidden = await owner.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/workflows",
            new { name = "Blocked", minimumRunRole = "Member" });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        using var outsider = _factory.CreateClient();
        var outsiderIdentity = await RegisterAsync(
            outsider, $"wf-outsider-admin-{Guid.NewGuid():N}@icehott.dev");
        outsider.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", outsiderIdentity.Token);

        var hidden = await outsider.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/workflows",
            new { name = "Hidden", minimumRunRole = "Member" });
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
    }

    private static async Task<RegisteredIdentity> RegisterAsync(HttpClient client, string email)
    {
        var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email,
            displayName = "Workflow User",
            password = "StrongPassword123!"
        });
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return new(
            json.RootElement.GetProperty("user").GetProperty("id").GetGuid(),
            json.RootElement.GetProperty("accessToken").GetString()!);
    }

    private static async Task<Guid> CreateWorkspaceAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/workspaces", new { name });
        response.EnsureSuccessStatusCode();
        return await ReadGuidAsync(response, "id");
    }

    private static async Task<Guid> ReadGuidAsync(HttpResponseMessage response, string property)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty(property).GetGuid();
    }

    private sealed record RegisteredIdentity(Guid UserId, string Token);
}
