using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ICEHOTT.Domain.Workflows;
using ICEHOTT.Domain.Workspaces;
using ICEHOTT.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ICEHOTT.Tests;

/// <summary>HTTP-level Phase 6A contract: authoring, tenancy, retry, artifact binding.</summary>
public sealed class WorkflowPhase6ContractTests : IClassFixture<IcehottApiFactory>
{
    private readonly IcehottApiFactory _factory;
    public WorkflowPhase6ContractTests(IcehottApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Version_Authoring_Canonicalizes_Hashes_Numbers_And_Activates_One()
    {
        using var owner = await NewClientAsync();
        var workspaceId = await CreateWorkspaceAsync(owner.Client);
        var workflowId = await CreateWorkflowAsync(owner.Client, workspaceId);

        var a = await PostVersionAsync(owner.Client, workspaceId, workflowId,
            """{"steps":[{"type":"delay","key":"a","delaySeconds":1}]}""");
        var b = await PostVersionAsync(owner.Client, workspaceId, workflowId,
            """{"steps":[{"delaySeconds":1,"key":"a","type":"delay"}]}""");
        var c = await PostVersionAsync(owner.Client, workspaceId, workflowId,
            """{"steps":[{"type":"delay","key":"a","delaySeconds":2}]}""");

        Assert.Equal(HttpStatusCode.Created, a.Status);
        Assert.Equal(1, a.Json.GetProperty("versionNumber").GetInt32());
        Assert.Equal(2, b.Json.GetProperty("versionNumber").GetInt32());
        Assert.Equal(3, c.Json.GetProperty("versionNumber").GetInt32());
        // Key order does not change the canonical hash; content does.
        Assert.Equal(
            a.Json.GetProperty("definitionHash").GetString(),
            b.Json.GetProperty("definitionHash").GetString());
        Assert.NotEqual(
            a.Json.GetProperty("definitionHash").GetString(),
            c.Json.GetProperty("definitionHash").GetString());
        Assert.Matches("^[0-9a-f]{64}$", a.Json.GetProperty("definitionHash").GetString()!);

        var idA = a.Json.GetProperty("id").GetGuid();
        var idC = c.Json.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await Activate(owner.Client, workspaceId, workflowId, idA)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Activate(owner.Client, workspaceId, workflowId, idC)).StatusCode);
        // An already-active/retired version cannot be re-activated.
        Assert.Equal(HttpStatusCode.Conflict, (await Activate(owner.Client, workspaceId, workflowId, idA)).StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ICEHOTTDbContext>();
        var versions = await db.WorkflowVersions
            .Where(x => x.WorkflowDefinitionId == workflowId).ToListAsync();
        Assert.Single(versions, x => x.Status == WorkflowVersionStatus.Active);
        Assert.Equal(idC, versions.Single(x => x.Status == WorkflowVersionStatus.Active).Id);
        Assert.Equal(WorkflowVersionStatus.Retired, versions.Single(x => x.Id == idA).Status);
        var audit = await db.WorkflowAuditEvents
            .Where(x => x.WorkflowDefinitionId == workflowId)
            .Select(x => x.EventType).ToListAsync();
        Assert.Equal(3, audit.Count(x => x == WorkflowAuditEventType.VersionCreated));
        Assert.Equal(2, audit.Count(x => x == WorkflowAuditEventType.VersionActivated));
        Assert.Equal(1, audit.Count(x => x == WorkflowAuditEventType.VersionRetired));
    }

    [Fact]
    public async Task Version_Authoring_Rejects_Duplicate_Keys_Oversize_And_Archived()
    {
        using var owner = await NewClientAsync();
        var workspaceId = await CreateWorkspaceAsync(owner.Client);
        var workflowId = await CreateWorkflowAsync(owner.Client, workspaceId);

        var duplicate = await PostVersionAsync(owner.Client, workspaceId, workflowId,
            """{"steps":[{"key":"a","key":"b","type":"delay","delaySeconds":1}]}""");
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.Status);

        var oversize = await owner.Client.PostAsync(
            $"/api/workspaces/{workspaceId}/workflows/{workflowId}/versions",
            new StringContent(
                "{\"definition\":{\"steps\":[],\"pad\":\"" + new string('x', 90 * 1024) + "\"}}",
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversize.StatusCode);

        var badName = await owner.Client.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/workflows",
            new { name = new string('n', 121), minimumRunRole = "Member" });
        Assert.Equal(HttpStatusCode.BadRequest, badName.StatusCode);

        var archive = await owner.Client.PostAsync(
            $"/api/workspaces/{workspaceId}/workflows/{workflowId}/archive", null);
        Assert.Equal(HttpStatusCode.OK, archive.StatusCode);
        var afterArchive = await PostVersionAsync(owner.Client, workspaceId, workflowId,
            """{"steps":[{"key":"a","type":"delay","delaySeconds":1}]}""");
        Assert.Equal(HttpStatusCode.Conflict, afterArchive.Status);
        var runAfterArchive = await owner.Client.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/workflow-runs",
            new { workflowDefinitionId = workflowId, idempotencyKey = "k-archived" });
        Assert.Equal(HttpStatusCode.Conflict, runAfterArchive.StatusCode);
    }

    [Fact]
    public async Task Run_Requests_Bound_Idempotency_Key_And_Are_Tenant_Isolated()
    {
        using var owner = await NewClientAsync();
        var workspaceId = await CreateWorkspaceAsync(owner.Client);
        var workflowId = await CreateActiveWorkflowAsync(owner.Client, workspaceId);

        Assert.Equal(HttpStatusCode.BadRequest, (await owner.Client.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/workflow-runs",
            new { workflowDefinitionId = workflowId, idempotencyKey = new string('k', 129) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.Client.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/workflow-runs",
            new { workflowDefinitionId = workflowId, idempotencyKey = "  " })).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await owner.Client.PostAsync(
            $"/api/workspaces/{workspaceId}/workflow-runs",
            new StringContent(
                "{\"workflowDefinitionId\":\"" + workflowId + "\",\"idempotencyKey\":\"" +
                new string('k', 8 * 1024) + "\"}",
                Encoding.UTF8, "application/json"))).StatusCode);

        var created = await owner.Client.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/workflow-runs",
            new { workflowDefinitionId = workflowId, idempotencyKey = "shared-key" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var runId = (await ReadAsync(created)).GetProperty("id").GetGuid();

        // Another member replaying the same key must not read the run back.
        using var member = await NewClientAsync();
        await AddMemberAsync(workspaceId, member.UserId, WorkspaceRole.Member);
        var replayByOther = await member.Client.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/workflow-runs",
            new { workflowDefinitionId = workflowId, idempotencyKey = "shared-key" });
        Assert.Equal(HttpStatusCode.Conflict, replayByOther.StatusCode);
        Assert.Contains("idempotency_conflict", await replayByOther.Content.ReadAsStringAsync());

        // Members who are neither requester nor admin cannot cancel or probe retry.
        Assert.Equal(HttpStatusCode.Forbidden, (await member.Client.PostAsync(
            $"/api/workspaces/{workspaceId}/workflow-runs/{runId}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.Client.PostAsync(
            $"/api/workspaces/{workspaceId}/workflow-runs/{runId}/retry", null)).StatusCode);

        // A non-member sees nothing: uniform 404 for every mutation.
        using var outsider = await NewClientAsync();
        foreach (var path in new[]
                 {
                     $"/api/workspaces/{workspaceId}/workflow-runs/{runId}/cancel",
                     $"/api/workspaces/{workspaceId}/workflow-runs/{runId}/retry"
                 })
            Assert.Equal(HttpStatusCode.NotFound, (await outsider.Client.PostAsync(path, null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.Client.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/workflow-runs",
            new { workflowDefinitionId = workflowId, idempotencyKey = "x" })).StatusCode);

        // A run id from workspace A cannot be reached through workspace B.
        var otherWorkspace = await CreateWorkspaceAsync(outsider.Client);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.Client.PostAsync(
            $"/api/workspaces/{otherWorkspace}/workflow-runs/{runId}/cancel", null)).StatusCode);

        // Retry stays fail-closed for the authorized caller.
        var retry = await owner.Client.PostAsync(
            $"/api/workspaces/{workspaceId}/workflow-runs/{runId}/retry", null);
        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
        Assert.Contains("run_retry_not_supported", await retry.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Minimum_Run_Role_Is_Enforced_At_The_Api()
    {
        using var owner = await NewClientAsync();
        var workspaceId = await CreateWorkspaceAsync(owner.Client);
        var create = await owner.Client.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/workflows",
            new { name = "Admins only", minimumRunRole = "Admin" });
        var workflowId = (await ReadAsync(create)).GetProperty("id").GetGuid();
        var version = await PostVersionAsync(owner.Client, workspaceId, workflowId,
            """{"steps":[{"key":"a","type":"delay","delaySeconds":1}]}""");
        await Activate(owner.Client, workspaceId, workflowId, version.Json.GetProperty("id").GetGuid());

        using var member = await NewClientAsync();
        await AddMemberAsync(workspaceId, member.UserId, WorkspaceRole.Member);
        var denied = await member.Client.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/workflow-runs",
            new { workflowDefinitionId = workflowId, idempotencyKey = "member-key" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var allowed = await owner.Client.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/workflow-runs",
            new { workflowDefinitionId = workflowId, idempotencyKey = "owner-key" });
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
    }

    [Fact]
    public async Task Artifact_Upload_Binding_Contract()
    {
        using var owner = await NewClientAsync();
        var workspaceId = await CreateWorkspaceAsync(owner.Client);
        var (runId, stepId) = await SeedWaitingArtifactRunAsync(workspaceId, owner.UserId);

        // Partial binding: either half alone is a client error.
        Assert.Equal(HttpStatusCode.BadRequest,
            (await UploadAsync(owner.Client, workspaceId, runId, null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await UploadAsync(owner.Client, workspaceId, null, stepId)).StatusCode);

        // Unknown run / step / cross-run step are indistinguishable not-founds.
        Assert.Equal(HttpStatusCode.NotFound,
            (await UploadAsync(owner.Client, workspaceId, Guid.NewGuid(), stepId)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await UploadAsync(owner.Client, workspaceId, runId, Guid.NewGuid())).StatusCode);

        // A bystander member may not satisfy someone else's artifact step.
        using var member = await NewClientAsync();
        await AddMemberAsync(workspaceId, member.UserId, WorkspaceRole.Member);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await UploadAsync(member.Client, workspaceId, runId, stepId)).StatusCode);

        // A non-member cannot even learn the run exists.
        using var outsider = await NewClientAsync();
        Assert.Equal(HttpStatusCode.NotFound,
            (await UploadAsync(outsider.Client, workspaceId, runId, stepId)).StatusCode);

        // Nothing above created an artifact.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ICEHOTTDbContext>();
            Assert.False(await db.Artifacts.AnyAsync(x => x.StepRunId == stepId));
        }

        // The run-as user satisfies the waiting step; the result never embeds content.
        var accepted = await UploadAsync(owner.Client, workspaceId, runId, stepId);
        Assert.True(
            accepted.StatusCode is HttpStatusCode.Created or HttpStatusCode.Accepted,
            $"Unexpected status {accepted.StatusCode}");

        // Once the run is cancelled the step is no longer waiting: uploads conflict.
        var cancel = await owner.Client.PostAsync(
            $"/api/workspaces/{workspaceId}/workflow-runs/{runId}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await UploadAsync(owner.Client, workspaceId, runId, stepId)).StatusCode);
    }

    private async Task<(Guid RunId, Guid StepId)> SeedWaitingArtifactRunAsync(
        Guid workspaceId, Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ICEHOTTDbContext>();
        var now = DateTimeOffset.UtcNow;
        var definition = new WorkflowDefinition(
            Guid.NewGuid(), workspaceId, "Artifact flow", null,
            WorkspaceRole.Member, userId, now);
        definition.MarkActive(now);
        var version = new WorkflowVersion(
            Guid.NewGuid(), definition.Id, workspaceId, 1,
            """{"steps":[{"key":"step","type":"artifact"}]}""",
            new string('a', 64), userId, now);
        version.Activate(now);
        var run = new WorkflowRun(
            Guid.NewGuid(), workspaceId, definition.Id, version.Id,
            userId, userId, $"seed-{Guid.NewGuid():N}", now);
        run.Start("step", now);
        var step = new WorkflowStepRun(
            Guid.NewGuid(), run.Id, workspaceId, "step", 1, WorkflowStepType.Artifact, "{}");
        step.MarkReady();
        step.Start(now);
        step.WaitForArtifact();
        run.Wait(WorkflowWaitReason.Artifact);
        db.WorkflowDefinitions.Add(definition);
        db.WorkflowVersions.Add(version);
        db.WorkflowRuns.Add(run);
        db.WorkflowStepRuns.Add(step);
        await db.SaveChangesAsync();
        return (run.Id, step.Id);
    }

    private async Task AddMemberAsync(Guid workspaceId, Guid userId, WorkspaceRole role)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ICEHOTTDbContext>();
        db.WorkspaceMemberships.Add(
            new WorkspaceMembership(workspaceId, userId, role, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
    }

    private static Task<HttpResponseMessage> UploadAsync(
        HttpClient client, Guid workspaceId, Guid? runId, Guid? stepId)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes("secret artifact body"));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(file, "File", "result.txt");
        if (runId is { } r) form.Add(new StringContent(r.ToString()), "WorkflowRunId");
        if (stepId is { } s) form.Add(new StringContent(s.ToString()), "StepRunId");
        return client.PostAsync($"/api/workspaces/{workspaceId}/artifacts/upload", form);
    }

    private async Task<Guid> CreateActiveWorkflowAsync(HttpClient client, Guid workspaceId)
    {
        var workflowId = await CreateWorkflowAsync(client, workspaceId);
        var version = await PostVersionAsync(client, workspaceId, workflowId,
            """{"steps":[{"key":"a","type":"delay","delaySeconds":60}]}""");
        var activate = await Activate(
            client, workspaceId, workflowId, version.Json.GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.OK, activate.StatusCode);
        return workflowId;
    }

    private static async Task<Guid> CreateWorkflowAsync(HttpClient client, Guid workspaceId)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/workflows",
            new { name = "Contract flow", description = "p6", minimumRunRole = "Member" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<(HttpStatusCode Status, JsonElement Json)> PostVersionAsync(
        HttpClient client, Guid workspaceId, Guid workflowId, string definitionJson)
    {
        var response = await client.PostAsync(
            $"/api/workspaces/{workspaceId}/workflows/{workflowId}/versions",
            new StringContent(
                "{\"definition\":" + definitionJson + "}", Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, JsonDocument.Parse(body).RootElement.Clone());
    }

    private static Task<HttpResponseMessage> Activate(
        HttpClient client, Guid workspaceId, Guid workflowId, Guid versionId) =>
        client.PostAsync(
            $"/api/workspaces/{workspaceId}/workflows/{workflowId}/versions/{versionId}/activate",
            null);

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<Guid> CreateWorkspaceAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/workspaces", new { name = $"P6 {Guid.NewGuid():N}"[..20] });
        response.EnsureSuccessStatusCode();
        return (await ReadAsync(response)).GetProperty("id").GetGuid();
    }

    private async Task<Identity> NewClientAsync()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email = $"p6-{Guid.NewGuid():N}@icehott.dev",
            displayName = "Phase Six",
            password = "StrongPassword123!"
        });
        response.EnsureSuccessStatusCode();
        var json = await ReadAsync(response);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", json.GetProperty("accessToken").GetString()!);
        return new(client, json.GetProperty("user").GetProperty("id").GetGuid());
    }

    private sealed record Identity(HttpClient Client, Guid UserId) : IDisposable
    {
        public void Dispose() => Client.Dispose();
    }
}
