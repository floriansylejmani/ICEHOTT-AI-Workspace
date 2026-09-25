using System.Net;
using System.Text;
using ICEHOTT.API.Filters;
using static ICEHOTT.Tests.ToolSecurityFixture;

namespace ICEHOTT.Tests;

/// <summary>
/// Phase 4.5 packet C: request bodies of tool endpoints are bounded and
/// rejected with 413 before any JSON parsing (docs/PHASE-4.5-C-BUDGETS-SECRETS.md C5).
/// </summary>
public sealed class ToolRequestBodyLimitTests(ToolBudgetFixture fx) : IClassFixture<ToolBudgetFixture>
{
    private const string Echo = "workspace.echo";
    private const int Limit = ToolRequestLimits.MaxExecutionRequestBytes;

    /// <summary>A valid execution request padded with JSON whitespace to exactly <paramref name="bytes"/>.</summary>
    private static string PaddedRequest(int bytes)
    {
        var core = $$"""{"toolName":"{{Echo}}","arguments":{"text":"padded"},"idempotencyKey":"{{NewKey("pad")}}"}""";
        var padding = bytes - Encoding.UTF8.GetByteCount(core);
        Assert.True(padding > 0);
        return core[..^1] + new string(' ', padding) + "}";
    }

    private static async Task AssertTooLargeAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("request_body_too_large", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public void Body_Limit_Leaves_Room_For_The_Largest_Admissible_Arguments() =>
        Assert.True(Limit >= 2 * ICEHOTT.Application.Tools.ToolExecutionService.MaxArgumentsBytes);

    [Fact]
    public async Task A_Body_Of_Exactly_The_Limit_Is_Accepted_And_One_Byte_More_Is_Rejected()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(fx.Owner);
        using var client = fx.Client(fx.Owner);

        var atLimit = PaddedRequest(Limit);
        Assert.Equal(Limit, Encoding.UTF8.GetByteCount(atLimit));
        Assert.Equal(HttpStatusCode.OK, (await RequestRawAsync(client, workspaceId, atLimit)).StatusCode);

        await AssertTooLargeAsync(await RequestRawAsync(client, workspaceId, PaddedRequest(Limit + 1)));
        Assert.Equal(1, await fx.CountExecutionsAsync(workspaceId));
        Assert.Equal(1, await fx.QuotaUsedAsync(workspaceId, Echo));
    }

    [Fact]
    public async Task Oversized_Body_Is_Rejected_Before_Json_Parsing()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(fx.Owner);
        using var client = fx.Client(fx.Owner);

        // Not JSON at all: a parser would answer 400, the size gate answers 413.
        var garbage = "{" + new string('[', Limit) + TestSecrets.GitHubToken;
        var response = await RequestRawAsync(client, workspaceId, garbage);

        await AssertTooLargeAsync(response);
        Assert.DoesNotContain(TestSecrets.GitHubToken, await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(TestSecrets.GitHubToken, fx.Logs.All);
        Assert.Equal(0, await fx.CountExecutionsAsync(workspaceId));
    }

    [Fact]
    public async Task Oversized_Chunked_Body_Without_Content_Length_Is_Rejected()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(fx.Owner);
        using var client = fx.Client(fx.Owner);

        var content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(PaddedRequest(Limit * 4))));
        content.Headers.ContentType = new("application/json");
        content.Headers.ContentLength = null;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{workspaceId}/tool-executions")
        {
            Content = content
        };
        request.Headers.TransferEncodingChunked = true;

        await AssertTooLargeAsync(await client.SendAsync(request));
        Assert.Equal(0, await fx.CountExecutionsAsync(workspaceId));
    }

    [Fact]
    public async Task Approval_And_Policy_Endpoints_Are_Bounded_Too()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(fx.Owner);
        using var client = fx.Client(fx.Owner);
        var big = new string(' ', Limit + 1);

        var approve = await client.PostAsync(
            $"/api/workspaces/{workspaceId}/tool-executions/{Guid.NewGuid()}/approve",
            new StringContent(big, Encoding.UTF8, "application/json"));
        await AssertTooLargeAsync(approve);

        var policy = await client.PutAsync(
            $"/api/workspaces/{workspaceId}/tool-policies/{Echo}",
            new StringContent(
                """{"expectedVersion":0,"enabled":true,"requiresApproval":false""" +
                new string(' ', ToolRequestLimits.MaxPolicyRequestBytes) + "}",
                Encoding.UTF8,
                "application/json"));
        await AssertTooLargeAsync(policy);

        var smallPolicy = await client.PutAsync(
            $"/api/workspaces/{workspaceId}/tool-policies/{Echo}",
            new StringContent("""{"expectedVersion":0,"enabled":true,"requiresApproval":false}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, smallPolicy.StatusCode);
    }

    [Fact]
    public async Task Size_Gate_Runs_After_Authentication()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(fx.Owner);
        using var anonymous = fx.Factory.CreateClient();

        var response = await RequestRawAsync(anonymous, workspaceId, PaddedRequest(Limit + 1));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
