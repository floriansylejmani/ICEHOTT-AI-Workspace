using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ICEHOTT.Domain.Tools;
using ICEHOTT.Domain.Workspaces;
using ICEHOTT.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ICEHOTT.Tests;

/// <summary>
/// Shared Phase 4 test fixture. Users are registered once per fixture because
/// the auth endpoints are rate limited (10/min per client address); every test
/// creates its own workspaces so tests stay isolated from each other.
/// </summary>
public sealed class ToolSecurityFixture : IAsyncLifetime
{
    public IcehottApiFactory Factory { get; } = new();

    public ToolTestIdentity Owner { get; private set; } = null!;
    public ToolTestIdentity Admin { get; private set; } = null!;
    public ToolTestIdentity Admin2 { get; private set; } = null!;
    public ToolTestIdentity Member { get; private set; } = null!;
    public ToolTestIdentity Outsider { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Owner = await RegisterAsync("owner");
        Admin = await RegisterAsync("admin");
        Admin2 = await RegisterAsync("admin2");
        Member = await RegisterAsync("member");
        Outsider = await RegisterAsync("outsider");
    }

    public Task DisposeAsync()
    {
        Factory.Dispose();
        return Task.CompletedTask;
    }

    public HttpClient Client(ToolTestIdentity identity)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", identity.Token);
        return client;
    }

    /// <summary>
    /// Creates a workspace owned by <paramref name="owner"/> through the API
    /// and adds the given extra memberships directly in the database.
    /// </summary>
    public async Task<Guid> CreateWorkspaceAsync(
        ToolTestIdentity owner,
        params (ToolTestIdentity Identity, WorkspaceRole Role)[] members)
    {
        using var client = Client(owner);
        var response = await client.PostAsJsonAsync(
            "/api/workspaces",
            new { name = $"Tools {Guid.NewGuid():N}"[..20] });
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        var workspaceId = json.RootElement.GetProperty("id").GetGuid();

        foreach (var (identity, role) in members)
            await SetMembershipAsync(workspaceId, identity.UserId, role);

        return workspaceId;
    }

    /// <summary>Replaces (or with null removes) a membership.</summary>
    public async Task SetMembershipAsync(
        Guid workspaceId,
        Guid userId,
        WorkspaceRole? role)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ICEHOTTDbContext>();

        var existing = await db.WorkspaceMemberships.SingleOrDefaultAsync(
            x => x.WorkspaceId == workspaceId && x.UserId == userId);
        if (existing is not null)
        {
            db.WorkspaceMemberships.Remove(existing);
            await db.SaveChangesAsync();
        }

        if (role is not null)
        {
            db.WorkspaceMemberships.Add(new WorkspaceMembership(
                workspaceId,
                userId,
                role.Value,
                DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }
    }

    public async Task<ToolExecution?> FindExecutionAsync(Guid executionId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ICEHOTTDbContext>();
        return await db.ToolExecutions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == executionId);
    }

    public async Task<int> CountNotesAsync(Guid executionId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ICEHOTTDbContext>();
        return await db.WorkspaceAuditNotes.AsNoTracking()
            .CountAsync(x => x.ToolExecutionId == executionId);
    }

    public async Task<int> CountExecutionsAsync(Guid workspaceId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ICEHOTTDbContext>();
        return await db.ToolExecutions.AsNoTracking()
            .CountAsync(x => x.WorkspaceId == workspaceId);
    }

    public async Task<ToolExecutionAuditEventType[]> AuditTrailAsync(Guid executionId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ICEHOTTDbContext>();
        var events = await db.ToolExecutionAuditEvents.AsNoTracking()
            .Where(x => x.ExecutionId == executionId)
            .ToListAsync();
        return events
            .OrderBy(x => x.OccurredAtUtc)
            .ThenBy(x => (int)x.EventType)
            .Select(x => x.EventType)
            .ToArray();
    }

    public static Task<HttpResponseMessage> RequestToolAsync(
        HttpClient client,
        Guid workspaceId,
        string toolName,
        object arguments,
        string idempotencyKey) =>
        client.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/tool-executions",
            new { toolName, arguments, idempotencyKey });

    public static Task<HttpResponseMessage> RequestRawAsync(
        HttpClient client,
        Guid workspaceId,
        string rawJsonBody) =>
        client.PostAsync(
            $"/api/workspaces/{workspaceId}/tool-executions",
            new StringContent(rawJsonBody, Encoding.UTF8, "application/json"));

    public static Task<HttpResponseMessage> ApproveAsync(
        HttpClient client, Guid workspaceId, Guid executionId) =>
        client.PostAsync(
            $"/api/workspaces/{workspaceId}/tool-executions/{executionId}/approve",
            null);

    public static Task<HttpResponseMessage> RejectAsync(
        HttpClient client, Guid workspaceId, Guid executionId) =>
        client.PostAsync(
            $"/api/workspaces/{workspaceId}/tool-executions/{executionId}/reject",
            null);

    public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    public static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        var json = await ReadJsonAsync(response);
        return json.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    public static string NewKey(string prefix = "key") => $"{prefix}-{Guid.NewGuid():N}";

    private async Task<ToolTestIdentity> RegisterAsync(string role)
    {
        using var client = Factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                email = $"p4-{role}-{Guid.NewGuid():N}@icehott.dev",
                displayName = $"Phase4 {role}",
                password = "StrongPassword123!"
            });
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return new ToolTestIdentity(
            json.RootElement.GetProperty("user").GetProperty("id").GetGuid(),
            json.RootElement.GetProperty("accessToken").GetString()!);
    }
}

public sealed record ToolTestIdentity(Guid UserId, string Token);
