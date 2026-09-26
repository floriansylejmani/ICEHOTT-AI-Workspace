using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ICEHOTT.Domain.Workflows;
using ICEHOTT.Domain.Workspaces;
using ICEHOTT.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace ICEHOTT.Tests;

public sealed class WorkflowExperienceIntegrationTests : IClassFixture<IcehottApiFactory>
{
    private readonly IcehottApiFactory _factory;

    public WorkflowExperienceIntegrationTests(IcehottApiFactory factory) =>
        _factory = factory;

    [Fact]
    public async Task Workflow_Experience_Returns_Safe_Metadata_And_Timeline()
    {
        using var owner = _factory.CreateClient();
        var identity = await RegisterAsync(
            owner,
            $"workflow-ui-{Guid.NewGuid():N}@icehott.dev");
        owner.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", identity.Token);

        var workspaceId = await CreateWorkspaceAsync(owner, "Workflow UI");
        var seed = await SeedAsync(workspaceId, identity.UserId);

        var list = await owner.GetAsync(
            $"/api/workspaces/{workspaceId}/workflows?limit=25");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var listJson = await list.Content.ReadAsStringAsync();
        Assert.Contains("Release workflow", listJson, StringComparison.Ordinal);

        var definition = await owner.GetAsync(
            $"/api/workspaces/{workspaceId}/workflows/{seed.WorkflowId}");
        Assert.Equal(HttpStatusCode.OK, definition.StatusCode);
        var definitionJson = await definition.Content.ReadAsStringAsync();
        Assert.Contains("\"versions\"", definitionJson, StringComparison.Ordinal);
        Assert.DoesNotContain("definitionJson", definitionJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("definition-secret-marker", definitionJson, StringComparison.OrdinalIgnoreCase);

        var runs = await owner.GetAsync(
            $"/api/workspaces/{workspaceId}/workflow-runs?limit=25");
        Assert.Equal(HttpStatusCode.OK, runs.StatusCode);
        Assert.Contains(seed.RunId.ToString(), await runs.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var run = await owner.GetAsync(
            $"/api/workspaces/{workspaceId}/workflow-runs/{seed.RunId}");
        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        var runJson = await run.Content.ReadAsStringAsync();
        Assert.Contains("\"steps\"", runJson, StringComparison.Ordinal);
        Assert.Contains("\"stepKey\":\"review\"", runJson, StringComparison.Ordinal);
        Assert.DoesNotContain("inputJson", runJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("outputJson", runJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("errorMessage", runJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("super-secret", runJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Workflow_Experience_Is_Nondisclosing_Across_Tenants()
    {
        using var owner = _factory.CreateClient();
        var ownerIdentity = await RegisterAsync(
            owner,
            $"workflow-owner-{Guid.NewGuid():N}@icehott.dev");
        owner.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", ownerIdentity.Token);

        var workspaceId = await CreateWorkspaceAsync(owner, "Private workflows");
        var seed = await SeedAsync(workspaceId, ownerIdentity.UserId);

        using var outsider = _factory.CreateClient();
        var outsiderIdentity = await RegisterAsync(
            outsider,
            $"workflow-outsider-{Guid.NewGuid():N}@icehott.dev");
        outsider.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", outsiderIdentity.Token);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await outsider.GetAsync($"/api/workspaces/{workspaceId}/workflows")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await outsider.GetAsync($"/api/workspaces/{workspaceId}/workflows/{seed.WorkflowId}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await outsider.GetAsync($"/api/workspaces/{workspaceId}/workflow-runs")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await outsider.GetAsync($"/api/workspaces/{workspaceId}/workflow-runs/{seed.RunId}")).StatusCode);
    }

    private async Task<SeedResult> SeedAsync(Guid workspaceId, Guid userId)
    {
        var now = DateTimeOffset.UtcNow;
        var workflowId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ICEHOTTDbContext>();

        var definition = new WorkflowDefinition(
            workflowId,
            workspaceId,
            "Release workflow",
            "Approval and artifact handoff",
            WorkspaceRole.Member,
            userId,
            now);
        definition.MarkActive(now.AddSeconds(1));

        var version = new WorkflowVersion(
            versionId,
            workflowId,
            workspaceId,
            1,
            "{\"secret\":\"definition-secret-marker\"}",
            new string('a', 64),
            userId,
            now);
        version.Activate(now.AddSeconds(1));

        var run = new WorkflowRun(
            runId,
            workspaceId,
            workflowId,
            versionId,
            userId,
            userId,
            $"ui-{Guid.NewGuid():N}",
            now.AddSeconds(2));
        run.Start("review", now.AddSeconds(3));

        var step = new WorkflowStepRun(
            Guid.NewGuid(),
            runId,
            workspaceId,
            "review",
            1,
            WorkflowStepType.Checkpoint,
            "{\"password\":\"super-secret\"}");
        step.MarkReady();
        step.Start(now.AddSeconds(4));
        step.Succeed("{\"token\":\"super-secret\"}", now.AddSeconds(5));

        db.WorkflowDefinitions.Add(definition);
        db.WorkflowVersions.Add(version);
        db.WorkflowRuns.Add(run);
        db.WorkflowStepRuns.Add(step);
        await db.SaveChangesAsync();

        return new SeedResult(workflowId, runId);
    }

    private static async Task<Guid> CreateWorkspaceAsync(
        HttpClient client,
        string name)
    {
        var response = await client.PostAsJsonAsync(
            "/api/workspaces",
            new { name });
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<RegisteredIdentity> RegisterAsync(
        HttpClient client,
        string email)
    {
        var response = await client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                email,
                displayName = "Workflow User",
                password = "StrongPassword123!"
            });
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        return new RegisteredIdentity(
            json.RootElement.GetProperty("user").GetProperty("id").GetGuid(),
            json.RootElement.GetProperty("accessToken").GetString()!);
    }

    private sealed record SeedResult(Guid WorkflowId, Guid RunId);
    private sealed record RegisteredIdentity(Guid UserId, string Token);
}
