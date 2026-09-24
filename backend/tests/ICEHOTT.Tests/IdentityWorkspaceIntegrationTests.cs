using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ICEHOTT.Tests;

public sealed class IdentityWorkspaceIntegrationTests : IClassFixture<IcehottApiFactory>
{
    private readonly IcehottApiFactory _factory;

    public IdentityWorkspaceIntegrationTests(IcehottApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Workspace_Is_Isolated_Between_Users()
    {
        using var owner = _factory.CreateClient();
        var ownerToken = await RegisterAsync(owner, "owner@icehott.dev", "Owner User");
        owner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);

        var create = await owner.PostAsJsonAsync("/api/workspaces", new { name = "Private Workspace" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var workspaceId = JsonDocument.Parse(await create.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        using var outsider = _factory.CreateClient();
        var outsiderToken = await RegisterAsync(outsider, "outsider@icehott.dev", "Outsider User");
        outsider.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", outsiderToken);

        var forbiddenLookup = await outsider.GetAsync($"/api/workspaces/{workspaceId}");
        Assert.Equal(HttpStatusCode.NotFound, forbiddenLookup.StatusCode);

        var ownerLookup = await owner.GetAsync($"/api/workspaces/{workspaceId}");
        Assert.Equal(HttpStatusCode.OK, ownerLookup.StatusCode);
    }

    [Fact]
    public async Task Duplicate_Email_Returns_Conflict()
    {
        using var client = _factory.CreateClient();
        var payload = new { email = "duplicate@icehott.dev", displayName = "Duplicate", password = "StrongPassword123!" };
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/register", payload)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/auth/register", payload)).StatusCode);
    }

    [Fact]
    public async Task Invalid_Login_Returns_Unauthorized()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { email = "missing@icehott.dev", password = "StrongPassword123!" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<string> RegisterAsync(HttpClient client, string email, string displayName)
    {
        var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email,
            displayName,
            password = "StrongPassword123!"
        });

        response.EnsureSuccessStatusCode();
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("accessToken").GetString()!;
    }
}
