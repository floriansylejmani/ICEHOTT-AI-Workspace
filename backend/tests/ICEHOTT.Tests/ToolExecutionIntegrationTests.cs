using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ICEHOTT.Domain.Tools;
using ICEHOTT.Domain.Workspaces;
using ICEHOTT.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ICEHOTT.Tests;

public sealed class ToolExecutionIntegrationTests
    : IClassFixture<IcehottApiFactory>
{
    private readonly IcehottApiFactory _factory;

    public ToolExecutionIntegrationTests(
        IcehottApiFactory factory) =>
        _factory = factory;

    [Fact]
    public async Task ReadOnly_Tool_Is_Idempotent_And_Audited()
    {
        using var owner = _factory.CreateClient();
        var identity = await RegisterAsync(
            owner,
            $"tool-owner-{Guid.NewGuid():N}@icehott.dev",
            "Tool Owner");
        Authenticate(owner, identity.Token);

        var workspaceId = await CreateWorkspaceAsync(
            owner,
            "Tool Workspace");

        var key = $"echo-{Guid.NewGuid():N}";
        var first = await owner.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/tool-executions",
            new
            {
                toolName = "workspace.echo",
                arguments = new { text = "hello" },
                idempotencyKey = key
            });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var firstJson = JsonDocument.Parse(
            await first.Content.ReadAsStringAsync());

        var executionId =
            firstJson.RootElement.GetProperty("id").GetGuid();
        Assert.Equal(
            "Succeeded",
            firstJson.RootElement
                .GetProperty("status")
                .GetString());
        Assert.Equal(
            "hello",
            firstJson.RootElement
                .GetProperty("result")
                .GetProperty("text")
                .GetString());

        var replay = await owner.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/tool-executions",
            new
            {
                toolName = "workspace.echo",
                arguments = new { text = "hello" },
                idempotencyKey = key
            });

        replay.EnsureSuccessStatusCode();
        using var replayJson = JsonDocument.Parse(
            await replay.Content.ReadAsStringAsync());
        Assert.Equal(
            executionId,
            replayJson.RootElement
                .GetProperty("id")
                .GetGuid());

        var list = await owner.GetAsync(
            $"/api/workspaces/{workspaceId}/tool-executions");
        list.EnsureSuccessStatusCode();
        using var listJson = JsonDocument.Parse(
            await list.Content.ReadAsStringAsync());
        Assert.Contains(
            listJson.RootElement.EnumerateArray(),
            item => item.GetProperty("id").GetGuid() == executionId);

        var conflict = await owner.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/tool-executions",
            new
            {
                toolName = "workspace.echo",
                arguments = new { text = "different" },
                idempotencyKey = key
            });

        Assert.Equal(
            HttpStatusCode.Conflict,
            conflict.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<ICEHOTTDbContext>();

        var events = (await db.ToolExecutionAuditEvents
                .AsNoTracking()
                .Where(x => x.ExecutionId == executionId)
                .ToListAsync())
            .OrderBy(x => x.OccurredAtUtc)
            .Select(x => x.EventType)
            .ToArray();

        Assert.Equal(
            [
                ToolExecutionAuditEventType.Requested,
                ToolExecutionAuditEventType.Started,
                ToolExecutionAuditEventType.Succeeded
            ],
            events);
    }

    [Fact]
    public async Task Sensitive_Write_Requires_Independent_Admin_Approval()
    {
        using var owner = _factory.CreateClient();
        var ownerIdentity = await RegisterAsync(
            owner,
            $"approval-owner-{Guid.NewGuid():N}@icehott.dev",
            "Approval Owner");
        Authenticate(owner, ownerIdentity.Token);

        var workspaceId = await CreateWorkspaceAsync(
            owner,
            "Approval Workspace");

        using var approver = _factory.CreateClient();
        var approverIdentity = await RegisterAsync(
            approver,
            $"approval-admin-{Guid.NewGuid():N}@icehott.dev",
            "Approval Admin");
        Authenticate(approver, approverIdentity.Token);

        await AddMembershipAsync(
            workspaceId,
            approverIdentity.UserId,
            WorkspaceRole.Admin);

        var request = await owner.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/tool-executions",
            new
            {
                toolName = "workspace.audit-note.create",
                arguments = new { message = "approved note" },
                idempotencyKey = $"note-{Guid.NewGuid():N}"
            });

        request.EnsureSuccessStatusCode();
        using var requestJson = JsonDocument.Parse(
            await request.Content.ReadAsStringAsync());

        var executionId =
            requestJson.RootElement.GetProperty("id").GetGuid();

        Assert.Equal(
            "PendingApproval",
            requestJson.RootElement
                .GetProperty("status")
                .GetString());

        var selfApproval = await owner.PostAsync(
            $"/api/workspaces/{workspaceId}/tool-executions/{executionId}/approve",
            null);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            selfApproval.StatusCode);

        using var memberApprover = _factory.CreateClient();
        var memberIdentity = await RegisterAsync(
            memberApprover,
            $"approval-member-{Guid.NewGuid():N}@icehott.dev",
            "Approval Member");
        Authenticate(memberApprover, memberIdentity.Token);
        await AddMembershipAsync(
            workspaceId,
            memberIdentity.UserId,
            WorkspaceRole.Member);

        var memberApproval = await memberApprover.PostAsync(
            $"/api/workspaces/{workspaceId}/tool-executions/{executionId}/approve",
            null);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            memberApproval.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider
                .GetRequiredService<ICEHOTTDbContext>();
            Assert.False(
                await db.WorkspaceAuditNotes.AnyAsync(
                    x => x.ToolExecutionId == executionId));
        }

        var approval = await approver.PostAsync(
            $"/api/workspaces/{workspaceId}/tool-executions/{executionId}/approve",
            null);

        approval.EnsureSuccessStatusCode();
        using var approvalJson = JsonDocument.Parse(
            await approval.Content.ReadAsStringAsync());

        Assert.Equal(
            "Succeeded",
            approvalJson.RootElement
                .GetProperty("status")
                .GetString());

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider
                .GetRequiredService<ICEHOTTDbContext>();

            var note = await db.WorkspaceAuditNotes
                .AsNoTracking()
                .SingleAsync(
                    x => x.ToolExecutionId == executionId);

            Assert.Equal(workspaceId, note.WorkspaceId);
            Assert.Equal(
                ownerIdentity.UserId,
                note.CreatedByUserId);
            Assert.Equal(
                "approved note",
                note.Message);
        }
    }

    [Fact]
    public async Task Member_Cannot_Request_Admin_Write_And_Outsider_Cannot_Read_Execution()
    {
        using var owner = _factory.CreateClient();
        var ownerIdentity = await RegisterAsync(
            owner,
            $"boundary-owner-{Guid.NewGuid():N}@icehott.dev",
            "Boundary Owner");
        Authenticate(owner, ownerIdentity.Token);

        var workspaceId = await CreateWorkspaceAsync(
            owner,
            "Boundary Workspace");

        var echo = await owner.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/tool-executions",
            new
            {
                toolName = "workspace.echo",
                arguments = new { text = "private" },
                idempotencyKey = $"boundary-{Guid.NewGuid():N}"
            });
        echo.EnsureSuccessStatusCode();

        using var echoJson = JsonDocument.Parse(
            await echo.Content.ReadAsStringAsync());
        var executionId =
            echoJson.RootElement.GetProperty("id").GetGuid();

        using var member = _factory.CreateClient();
        var memberIdentity = await RegisterAsync(
            member,
            $"boundary-member-{Guid.NewGuid():N}@icehott.dev",
            "Boundary Member");
        Authenticate(member, memberIdentity.Token);
        await AddMembershipAsync(
            workspaceId,
            memberIdentity.UserId,
            WorkspaceRole.Member);

        var tools = await member.GetAsync(
            $"/api/workspaces/{workspaceId}/tools");
        tools.EnsureSuccessStatusCode();
        using (var toolsJson = JsonDocument.Parse(
                   await tools.Content.ReadAsStringAsync()))
        {
            var names = toolsJson.RootElement
                .EnumerateArray()
                .Select(x => x.GetProperty("name").GetString())
                .ToArray();
            Assert.Contains("workspace.echo", names);
            Assert.DoesNotContain(
                "workspace.audit-note.create",
                names);
        }

        var write = await member.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/tool-executions",
            new
            {
                toolName = "workspace.audit-note.create",
                arguments = new { message = "not allowed" },
                idempotencyKey = $"member-{Guid.NewGuid():N}"
            });

        Assert.Equal(
            HttpStatusCode.Forbidden,
            write.StatusCode);

        using var outsider = _factory.CreateClient();
        var outsiderIdentity = await RegisterAsync(
            outsider,
            $"boundary-outsider-{Guid.NewGuid():N}@icehott.dev",
            "Boundary Outsider");
        Authenticate(outsider, outsiderIdentity.Token);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await outsider.GetAsync(
                $"/api/workspaces/{workspaceId}/tool-executions/{executionId}"))
            .StatusCode);
    }

    [Fact]
    public async Task Rejected_Sensitive_Write_Is_Terminal_And_Has_No_Side_Effect()
    {
        using var owner = _factory.CreateClient();
        var ownerIdentity = await RegisterAsync(
            owner,
            $"reject-owner-{Guid.NewGuid():N}@icehott.dev",
            "Reject Owner");
        Authenticate(owner, ownerIdentity.Token);

        var workspaceId = await CreateWorkspaceAsync(
            owner,
            "Reject Workspace");

        using var approver = _factory.CreateClient();
        var approverIdentity = await RegisterAsync(
            approver,
            $"reject-admin-{Guid.NewGuid():N}@icehott.dev",
            "Reject Admin");
        Authenticate(approver, approverIdentity.Token);
        await AddMembershipAsync(
            workspaceId,
            approverIdentity.UserId,
            WorkspaceRole.Admin);

        var request = await owner.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/tool-executions",
            new
            {
                toolName = "workspace.audit-note.create",
                arguments = new { message = "do not create" },
                idempotencyKey = $"reject-{Guid.NewGuid():N}"
            });
        request.EnsureSuccessStatusCode();

        using var requestJson = JsonDocument.Parse(
            await request.Content.ReadAsStringAsync());
        var executionId = requestJson.RootElement
            .GetProperty("id")
            .GetGuid();

        var reject = await approver.PostAsync(
            $"/api/workspaces/{workspaceId}/tool-executions/{executionId}/reject",
            null);
        reject.EnsureSuccessStatusCode();

        using var rejectJson = JsonDocument.Parse(
            await reject.Content.ReadAsStringAsync());
        Assert.Equal(
            "Rejected",
            rejectJson.RootElement.GetProperty("status").GetString());

        var approveAfterReject = await approver.PostAsync(
            $"/api/workspaces/{workspaceId}/tool-executions/{executionId}/approve",
            null);
        Assert.Equal(
            HttpStatusCode.Conflict,
            approveAfterReject.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<ICEHOTTDbContext>();
        Assert.False(
            await db.WorkspaceAuditNotes.AnyAsync(
                x => x.ToolExecutionId == executionId));
    }

    [Fact]
    public async Task Tool_Arguments_Are_Strictly_Validated()
    {
        using var owner = _factory.CreateClient();
        var identity = await RegisterAsync(
            owner,
            $"validation-owner-{Guid.NewGuid():N}@icehott.dev",
            "Validation Owner");
        Authenticate(owner, identity.Token);
        var workspaceId = await CreateWorkspaceAsync(
            owner,
            "Validation Workspace");

        var invalid = await owner.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/tool-executions",
            new
            {
                toolName = "workspace.echo",
                arguments = new
                {
                    text = "hello",
                    unexpected = "reject me"
                },
                idempotencyKey = $"invalid-{Guid.NewGuid():N}"
            });

        Assert.Equal(
            HttpStatusCode.BadRequest,
            invalid.StatusCode);

        using var json = JsonDocument.Parse(
            await invalid.Content.ReadAsStringAsync());
        Assert.Equal(
            "invalid_arguments",
            json.RootElement.GetProperty("code").GetString());

        var wrongType = await owner.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/tool-executions",
            new
            {
                toolName = "workspace.echo",
                arguments = new { text = 42 },
                idempotencyKey = $"wrong-type-{Guid.NewGuid():N}"
            });

        Assert.Equal(
            HttpStatusCode.BadRequest,
            wrongType.StatusCode);
    }

    private async Task AddMembershipAsync(
        Guid workspaceId,
        Guid userId,
        WorkspaceRole role)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<ICEHOTTDbContext>();
        db.WorkspaceMemberships.Add(
            new WorkspaceMembership(
                workspaceId,
                userId,
                role,
                DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
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
        return json.RootElement
            .GetProperty("id")
            .GetGuid();
    }

    private static async Task<RegisteredIdentity> RegisterAsync(
        HttpClient client,
        string email,
        string displayName)
    {
        var response = await client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                email,
                displayName,
                password = "StrongPassword123!"
            });

        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        return new RegisteredIdentity(
            json.RootElement
                .GetProperty("user")
                .GetProperty("id")
                .GetGuid(),
            json.RootElement
                .GetProperty("accessToken")
                .GetString()!);
    }

    private static void Authenticate(
        HttpClient client,
        string token) =>
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                token);

    private sealed record RegisteredIdentity(
        Guid UserId,
        string Token);
}
