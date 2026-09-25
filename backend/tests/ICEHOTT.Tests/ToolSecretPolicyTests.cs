using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ICEHOTT.Application.Security;
using ICEHOTT.Domain.Tools;
using ICEHOTT.Persistence;
using Microsoft.Extensions.DependencyInjection;
using static ICEHOTT.Tests.ToolSecurityFixture;

namespace ICEHOTT.Tests;

/// <summary>
/// Phase 4.5 packet C: credential rejection at the tool API boundary and
/// redaction of results, errors, logs and audit data
/// (docs/PHASE-4.5-C-BUDGETS-SECRETS.md C6-C8).
/// </summary>
public sealed class ToolSecretPolicyTests(ToolBudgetFixture fx) : IClassFixture<ToolBudgetFixture>
{
    private const string Echo = "workspace.echo";

    /// <summary>(kind, credential text, the part that must never leak).</summary>
    public static TheoryData<string, string, string> Credentials() => new()
    {
        { "github", TestSecrets.GitHubToken, TestSecrets.GitHubToken },
        { "github-fine-grained", TestSecrets.GitHubFineGrained, TestSecrets.GitHubFineGrained },
        { "aws-access-key-id", TestSecrets.AwsAccessKeyId, TestSecrets.AwsAccessKeyId },
        { "openai", TestSecrets.OpenAiKey, TestSecrets.OpenAiKey },
        { "anthropic", TestSecrets.AnthropicKey, TestSecrets.AnthropicKey },
        { "slack", TestSecrets.SlackToken, TestSecrets.SlackToken },
        { "stripe", TestSecrets.StripeKey, TestSecrets.StripeKey },
        { "google-api-key", TestSecrets.GoogleApiKey, TestSecrets.GoogleApiKey },
        { "jwt", TestSecrets.Jwt, TestSecrets.Jwt },
        { "pem", TestSecrets.PemBlock, TestSecrets.PemBody },
        { "bearer", "Authorization: Bearer " + TestSecrets.BearerValue, TestSecrets.BearerValue },
        { "password-assignment", "password=" + TestSecrets.Password, TestSecrets.Password },
        { "url-credentials", TestSecrets.UrlWithCredentials, TestSecrets.UrlPassword }
    };

    [Theory]
    [MemberData(nameof(Credentials))]
    public void Classifier_Detects_Representative_Credentials_Alone_And_Embedded(string kind, string credential, string secret)
    {
        Assert.NotNull(SecretClassifier.FindCredential(credential));
        Assert.NotNull(SecretClassifier.FindCredential($"please rotate {credential} before friday ({kind})"));

        var redacted = SecretClassifier.Redact($"prefix {credential} suffix");
        Assert.StartsWith("prefix ", redacted);
        Assert.EndsWith(" suffix", redacted);
        Assert.Contains(SecretClassifier.RedactionMarker, redacted);
        Assert.DoesNotContain(secret, redacted);
    }

    [Theory]
    [InlineData("Quarterly review completed; nothing to report.")]
    [InlineData("The password policy changed last week.")]
    [InlineData("token count is 5, secret santa on friday")]
    [InlineData("scikit uses sk-learn naming")]
    [InlineData("https://example.com/path?query=1&b=two")]
    [InlineData("contact ops@example.com")]
    [InlineData("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("3f36a640-0d25-4a76-9d0d-640000000001")]
    // This API's own idempotency-key shape (NewKey: "key-" + 32 hex), which is
    // also the legacy Mailgun key format; split so the source holds no such literal.
    [InlineData("key-" + "7c9e6679742540de944be07fc1f90ae7")]
    public void Classifier_Leaves_Ordinary_Text_Alone(string text)
    {
        Assert.Null(SecretClassifier.FindCredential(text));
        Assert.Equal(text, SecretClassifier.Redact(text));
    }

    [Theory]
    [InlineData("password", true)]
    [InlineData("Password", true)]
    [InlineData("api_key", true)]
    [InlineData("apiKey", true)]
    [InlineData("X-Api-Key", true)]
    [InlineData("client_secret", true)]
    [InlineData("accessToken", true)]
    [InlineData("refresh_token", true)]
    [InlineData("Authorization", true)]
    [InlineData("privateKey", true)]
    [InlineData("connectionString", true)]
    [InlineData("aws_secret_access_key", true)]
    [InlineData("cookie", true)]
    [InlineData("sessionToken", true)]
    [InlineData("passphrase", true)]
    [InlineData("credentials", true)]
    [InlineData("auth", true)]
    [InlineData("text", false)]
    [InlineData("message", false)]
    [InlineData("author", false)]
    [InlineData("keyboard", false)]
    [InlineData("passage", false)]
    public void Classifier_Recognises_Credential_Field_Names(string name, bool expected) =>
        Assert.Equal(expected, SecretClassifier.IsCredentialName(name));

    [Fact]
    public void Json_Redaction_Masks_Credential_Fields_And_Embedded_Tokens_Only()
    {
        var json = JsonSerializer.Serialize(new
        {
            status = "ok",
            count = 3,
            apiKey = "plain",
            password = 12345,
            nested = new { clientSecret = new { value = "x" }, note = $"use {TestSecrets.GitHubToken}" },
            items = new object[] { $"Bearer {TestSecrets.BearerValue}", "harmless", 7 }
        });

        using var document = JsonDocument.Parse(SecretClassifier.RedactJson(json));
        var root = document.RootElement;
        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.Equal(3, root.GetProperty("count").GetInt32());
        Assert.Equal(SecretClassifier.RedactionMarker, root.GetProperty("apiKey").GetString());
        Assert.Equal(SecretClassifier.RedactionMarker, root.GetProperty("password").GetString());
        Assert.Equal(SecretClassifier.RedactionMarker, root.GetProperty("nested").GetProperty("clientSecret").GetString());
        Assert.Equal($"use {SecretClassifier.RedactionMarker}", root.GetProperty("nested").GetProperty("note").GetString());
        Assert.DoesNotContain(TestSecrets.BearerValue, root.GetProperty("items")[0].GetString());
        Assert.Equal("harmless", root.GetProperty("items")[1].GetString());
        Assert.Equal(7, root.GetProperty("items")[2].GetInt32());
    }

    [Theory]
    [MemberData(nameof(Credentials))]
    public async Task Credential_Values_In_Arguments_Are_Rejected_And_Never_Stored_Or_Echoed(
        string kind, string credential, string secret)
    {
        var workspaceId = await fx.CreateWorkspaceAsync(fx.Owner);
        using var client = fx.Client(fx.Owner);

        var response = await RequestToolAsync(client, workspaceId, Echo, new { text = $"{kind}: {credential}" }, NewKey("cred"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("credential_rejected", JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain(secret, body);
        Assert.Contains("$.text", body);

        Assert.Equal(0, await fx.CountExecutionsAsync(workspaceId));
        Assert.Equal(0, await fx.QuotaUsedAsync(workspaceId, Echo));
        Assert.DoesNotContain(secret, fx.Logs.All);
    }

    [Theory]
    [InlineData("""{"text":"hi","password":"hunter2-but-longer"}""")]
    [InlineData("""{"text":"hi","options":{"apiKey":"v"}}""")]
    [InlineData("""{"text":"hi","list":[{"client_secret":"v"}]}""")]
    public async Task Credential_Named_Fields_Are_Rejected_At_Any_Depth_Before_Tool_Validation(string arguments)
    {
        var workspaceId = await fx.CreateWorkspaceAsync(fx.Owner);
        using var client = fx.Client(fx.Owner);

        var response = await RequestRawAsync(client, workspaceId,
            $$$"""{"toolName":"{{{Echo}}}","idempotencyKey":"{{{NewKey()}}}","arguments":{{{arguments}}}}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("credential_rejected", await ReadErrorCodeAsync(response));
        Assert.Equal(0, await fx.CountExecutionsAsync(workspaceId));
    }

    [Fact]
    public async Task Credential_Shaped_Property_Names_And_Idempotency_Keys_Are_Rejected_Without_Echo()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(fx.Owner);
        using var client = fx.Client(fx.Owner);

        var byName = await RequestRawAsync(client, workspaceId,
            $$$"""{"toolName":"{{{Echo}}}","idempotencyKey":"{{{NewKey()}}}","arguments":{"text":"hi","{{{TestSecrets.GitHubToken}}}":"x"}}""");
        Assert.Equal(HttpStatusCode.BadRequest, byName.StatusCode);
        Assert.Equal("credential_rejected", await ReadErrorCodeAsync(byName));
        Assert.DoesNotContain(TestSecrets.GitHubToken, await byName.Content.ReadAsStringAsync());

        var byKey = await RequestToolAsync(client, workspaceId, Echo, new { text = "hi" }, TestSecrets.AwsAccessKeyId);
        Assert.Equal(HttpStatusCode.BadRequest, byKey.StatusCode);
        Assert.Equal("credential_rejected", await ReadErrorCodeAsync(byKey));
        Assert.DoesNotContain(TestSecrets.AwsAccessKeyId, await byKey.Content.ReadAsStringAsync());

        Assert.Equal(0, await fx.CountExecutionsAsync(workspaceId));
    }

    [Fact]
    public async Task Malformed_Json_Carrying_A_Secret_Is_Rejected_Without_Echo()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(fx.Owner);
        using var client = fx.Client(fx.Owner);

        foreach (var body in new[]
                 {
                     $$"""{"toolName":"{{Echo}}","arguments":{"text":"{{TestSecrets.OpenAiKey}}""",
                     $$$"""{"toolName":"{{{Echo}}}","arguments":{"text":{{{TestSecrets.OpenAiKey}}}}}""",
                     $$"""{"toolName":{"x":"{{TestSecrets.OpenAiKey}}"},"arguments":{},"idempotencyKey":"k"}"""
                 })
        {
            var response = await RequestRawAsync(client, workspaceId, body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.DoesNotContain(TestSecrets.OpenAiKey, await response.Content.ReadAsStringAsync());
        }

        Assert.Equal(0, await fx.CountExecutionsAsync(workspaceId));
        Assert.DoesNotContain(TestSecrets.OpenAiKey, fx.Logs.All);
    }

    [Fact]
    public async Task Handler_Exception_Carrying_Secrets_Is_Redacted_Everywhere()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(fx.Owner);
        using var client = fx.Client(fx.Owner);
        var inputMarker = $"input-marker-{Guid.NewGuid():N}";

        var response = await RequestToolAsync(client, workspaceId, SecretFailureTool.Name, new { text = inputMarker }, NewKey("fail"));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal("tool_execution_failed", json.GetProperty("code").GetString());
        var execution = json.GetProperty("execution");
        var executionId = execution.GetProperty("id").GetGuid();
        Assert.Equal("Tool execution failed.", execution.GetProperty("errorMessage").GetString());

        var persisted = await fx.PersistedToolDataAsync(workspaceId);
        var logs = fx.Logs.All;
        foreach (var secret in new[]
                 {
                     TestSecrets.OpenAiKey, TestSecrets.Password, TestSecrets.BearerValue, TestSecrets.GitHubToken
                 })
        {
            Assert.DoesNotContain(secret, body);
            Assert.DoesNotContain(secret, persisted);
            Assert.DoesNotContain(secret, logs);
        }

        // Operators still get a usable, redacted diagnostic, without raw tool input.
        var failure = Assert.Single(fx.Logs.Lines, x => x.Contains(executionId.ToString()) && x.Contains("System.InvalidOperationException"));
        Assert.Contains(SecretClassifier.RedactionMarker, failure);
        Assert.Contains("System.Net.Http.HttpRequestException", failure);
        Assert.Contains(SecretFailureTool.Name, failure);
        Assert.DoesNotContain(inputMarker, logs);

        Assert.Equal(
            [ToolExecutionAuditEventType.Requested, ToolExecutionAuditEventType.Started, ToolExecutionAuditEventType.Failed],
            await fx.AuditTrailAsync(executionId));
    }

    [Fact]
    public async Task Handler_Results_Carrying_Secrets_Are_Redacted_Before_Storage_And_Response()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(fx.Owner);
        using var client = fx.Client(fx.Owner);

        var response = await RequestToolAsync(client, workspaceId, SecretResultTool.Name, new { text = "go" }, NewKey("result"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var result = JsonDocument.Parse(body).RootElement.GetProperty("result");
        Assert.Equal("ok", result.GetProperty("status").GetString());
        Assert.Equal(SecretClassifier.RedactionMarker, result.GetProperty("apiKey").GetString());
        Assert.Equal(SecretClassifier.RedactionMarker, result.GetProperty("nested").GetProperty("clientSecret").GetString());
        Assert.Equal("harmless", result.GetProperty("items")[1].GetString());

        var persisted = await fx.PersistedToolDataAsync(workspaceId);
        foreach (var secret in new[] { "plain-value-without-pattern", "another-plain-value", TestSecrets.GitHubToken, TestSecrets.BearerValue })
        {
            Assert.DoesNotContain(secret, body);
            Assert.DoesNotContain(secret, persisted);
        }
    }

    [Fact]
    public async Task Stored_Legacy_Arguments_With_Secrets_Are_Redacted_On_Read()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(fx.Owner);
        var executionId = Guid.NewGuid();
        using (var scope = fx.Factory.Services.CreateScope())
        {
            // A Phase 4 row admitted before credential screening existed.
            var db = scope.ServiceProvider.GetRequiredService<ICEHOTTDbContext>();
            db.ToolExecutions.Add(new ToolExecution(
                executionId, workspaceId, fx.Owner.UserId, Echo, ToolRiskLevel.ReadOnly,
                JsonSerializer.Serialize(new { text = $"legacy {TestSecrets.GitHubToken}" }),
                "legacy-hash", NewKey("legacy"), requiresApproval: false, fx.Clock.GetUtcNow()));
            await db.SaveChangesAsync();
        }

        using var client = fx.Client(fx.Owner);
        var single = await client.GetStringAsync($"/api/workspaces/{workspaceId}/tool-executions/{executionId}");
        var list = await client.GetStringAsync($"/api/workspaces/{workspaceId}/tool-executions");

        Assert.DoesNotContain(TestSecrets.GitHubToken, single);
        Assert.DoesNotContain(TestSecrets.GitHubToken, list);
        Assert.Contains($"legacy {SecretClassifier.RedactionMarker}", JsonDocument.Parse(single)
            .RootElement.GetProperty("arguments").GetProperty("text").GetString());
    }

    [Fact]
    public async Task Ordinary_Arguments_Are_Accepted_Unchanged()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(fx.Owner);
        using var client = fx.Client(fx.Owner);
        const string text = "Rotate the staging password on Friday; token count is 5.";

        var response = await RequestToolAsync(client, workspaceId, Echo, new { text }, NewKey("plain"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ReadJsonAsync(response);
        Assert.Equal(text, json.GetProperty("arguments").GetProperty("text").GetString());
        Assert.Equal(text, json.GetProperty("result").GetProperty("text").GetString());
    }
}
