using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ICEHOTT.Domain.Workspaces;
using ICEHOTT.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace ICEHOTT.Tests;

public sealed class IdentityWorkspaceIntegrationTests : IClassFixture<IcehottApiFactory>
{
    private readonly IcehottApiFactory _factory;

    public IdentityWorkspaceIntegrationTests(IcehottApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Workspace_Is_Isolated_Between_Users()
    {
        using var owner = _factory.CreateClient();
        var ownerIdentity = await RegisterAsync(owner, $"owner-{Guid.NewGuid():N}@icehott.dev", "Owner User");
        owner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerIdentity.Token);

        var create = await owner.PostAsJsonAsync("/api/workspaces", new { name = "Private Workspace" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var workspaceId = JsonDocument.Parse(await create.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        using var outsider = _factory.CreateClient();
        var outsiderIdentity = await RegisterAsync(outsider, $"outsider-{Guid.NewGuid():N}@icehott.dev", "Outsider User");
        outsider.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", outsiderIdentity.Token);

        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/workspaces/{workspaceId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/workspaces/{workspaceId}")).StatusCode);
    }

    [Fact]
    public async Task Member_Cannot_Read_Admin_Settings_But_Owner_Can()
    {
        using var owner = _factory.CreateClient();
        var ownerIdentity = await RegisterAsync(owner, $"settings-owner-{Guid.NewGuid():N}@icehott.dev", "Settings Owner");
        owner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerIdentity.Token);

        var create = await owner.PostAsJsonAsync("/api/workspaces", new { name = "Admin Boundary" });
        create.EnsureSuccessStatusCode();
        var workspaceId = JsonDocument.Parse(await create.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        using var member = _factory.CreateClient();
        var memberIdentity = await RegisterAsync(member, $"member-{Guid.NewGuid():N}@icehott.dev", "Member User");
        member.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", memberIdentity.Token);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ICEHOTTDbContext>();
            db.WorkspaceMemberships.Add(new WorkspaceMembership(
                workspaceId,
                memberIdentity.UserId,
                WorkspaceRole.Member,
                DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync($"/api/workspaces/{workspaceId}/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/workspaces/{workspaceId}/settings")).StatusCode);
    }

    [Fact]
    public async Task Duplicate_Email_Returns_Conflict()
    {
        using var client = _factory.CreateClient();
        var email = $"duplicate-{Guid.NewGuid():N}@icehott.dev";
        var payload = new { email, displayName = "Duplicate", password = "StrongPassword123!" };
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/register", payload)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/auth/register", payload)).StatusCode);
    }

    [Fact]
    public async Task Invalid_Login_Returns_Unauthorized()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            email = $"missing-{Guid.NewGuid():N}@icehott.dev",
            password = "StrongPassword123!"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<RegisteredIdentity> RegisterAsync(HttpClient client, string email, string displayName)
    {
        var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email,
            displayName,
            password = "StrongPassword123!"
        });

        response.EnsureSuccessStatusCode();
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return new RegisteredIdentity(
            json.RootElement.GetProperty("user").GetProperty("id").GetGuid(),
            json.RootElement.GetProperty("accessToken").GetString()!);
    }

    private sealed record RegisteredIdentity(Guid UserId, string Token);
}
