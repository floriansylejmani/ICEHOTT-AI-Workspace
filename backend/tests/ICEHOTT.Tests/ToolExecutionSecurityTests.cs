using System.Net;
using System.Text.Json;
using ICEHOTT.Application.Tools;
using ICEHOTT.Domain.Tools;
using ICEHOTT.Domain.Workspaces;
using Microsoft.Extensions.DependencyInjection;
using static ICEHOTT.Tests.ToolSecurityFixture;

namespace ICEHOTT.Tests;

/// <summary>
/// Phase 4 independent security review: HTTP-level coverage of tenant
/// isolation, role enforcement, approval rules, idempotency and argument
/// validation for the tool execution API.
/// </summary>
public sealed class ToolExecutionSecurityTests(ToolSecurityFixture fx)
    : IClassFixture<ToolSecurityFixture>
{
    private const string Echo = "workspace.echo";
    private const string Note = "workspace.audit-note.create";

    [Fact]
    public void Registry_Resolves_Known_Tools_And_Rejects_Unknown()
    {
        using var scope = fx.Factory.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IToolRegistry>();

        var registeredNames = registry.All
            .Select(x => x.Definition.Name)
            .ToArray();

        Assert.Contains(Note, registeredNames);
        Assert.Contains(Echo, registeredNames);

        Assert.Equal(Echo, registry.Find("workspace.echo")!.Definition.Name);
        Assert.Equal(Echo, registry.Find(" WORKSPACE.ECHO ")!.Definition.Name);
        Assert.Equal(Note, registry.Find(Note)!.Definition.Name);

        Assert.Null(registry.Find("workspace.shell"));
        Assert.Null(registry.Find("workspace.echo2"));
        Assert.Null(registry.Find(""));
        Assert.Null(registry.Find("   "));

        var echo = registry.Find(Echo)!.Definition;
        Assert.Equal(ToolRiskLevel.ReadOnly, echo.RiskLevel);
        Assert.Equal(WorkspaceRole.Member, echo.MinimumRequesterRole);
        Assert.False(echo.RequiresApproval);

        var note = registry.Find(Note)!.Definition;
        Assert.Equal(ToolRiskLevel.SensitiveWrite, note.RiskLevel);
        Assert.Equal(WorkspaceRole.Admin, note.MinimumRequesterRole);
        Assert.True(note.RequiresApproval);
        Assert.Equal(WorkspaceRole.Admin, note.MinimumApproverRole);
    }

    [Fact]
    public async Task Unknown_Tool_Is_Rejected_Without_Persisting()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(fx.Owner);
        using var owner = fx.Client(fx.Owner);

        var response = await RequestToolAsync(
            owner, workspaceId, "workspace.shell", new { command = "whoami" }, NewKey());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("tool_not_found", await ReadErrorCodeAsync(response));
        Assert.Equal(0, await fx.CountExecutionsAsync(workspaceId));
    }

    [Theory]
    [InlineData("""{"toolName":"workspace.echo","idempotencyKey":"KEY"}""")]
    [InlineData("""{"toolName":"workspace.echo","arguments":null,"idempotencyKey":"KEY"}""")]
    [InlineData("""{"toolName":"workspace.echo","arguments":[],"idempotencyKey":"KEY"}""")]
    [InlineData("""{"toolName":"workspace.echo","arguments":"hello","idempotencyKey":"KEY"}""")]
    [InlineData("""{"toolName":"workspace.echo","arguments":{},"idempotencyKey":"KEY"}""")]
    [InlineData("""{"toolName":"workspace.echo","arguments":{"text":null},"idempotencyKey":"KEY"}""")]
    [InlineData("""{"toolName":"workspace.echo","arguments":{"text":"   "},"idempotencyKey":"KEY"}""")]
    [InlineData("""{"toolName":"workspace.echo","arguments":{"text":42},"idempotencyKey":"KEY"}""")]
    [InlineData("""{"toolName":"workspace.echo","arguments":{"text":{"nested":"x"}},"idempotencyKey":"KEY"}""")]
    [InlineData("""{"toolName":"workspace.echo","arguments":{"TEXT":"case matters"},"idempotencyKey":"KEY"}""")]
    [InlineData("""{"toolName":"workspace.echo","arguments":{"text":"a\u0000b"},"idempotencyKey":"KEY"}""")]
    [InlineData("""{"toolName":"workspace.echo","arguments":{"text":"bell\u0007"},"idempotencyKey":"KEY"}""")]
    [InlineData("""{"toolName":"workspace.echo","arguments":{"text":{"huge":[1,2,3]},"text":"ok"},"idempotencyKey":"KEY"}""")]
    [InlineData("""{"toolName":"workspace.echo","arguments":{"text":"ok","workspaceId":"00000000-0000-0000-0000-000000000001"},"idempotencyKey":"KEY"}""")]
    [InlineData("""{"toolName":"workspace.echo","arguments":{"text":"ok","userId":"00000000-0000-0000-0000-000000000001"},"idempotencyKey":"KEY"}""")]
    [InlineData("""{"toolName":"workspace.echo","arguments":{"text":"ok","approved":true},"idempotencyKey":"KEY"}""")]
    [InlineData("""{"toolName":"workspace.audit-note.create","arguments":{"message":"ok","requestedByUserId":"00000000-0000-0000-0000-000000000001"},"idempotencyKey":"KEY"}""")]
    public async Task Malformed_Or_Malicious_Arguments_Are_Rejected(string body)
    {
        var workspaceId = await fx.CreateWorkspaceAsync(fx.Owner);
        using var owner = fx.Client(fx.Owner);

        var response = await RequestRawAsync(
            owner, workspaceId, body.Replace("KEY", NewKey("bad")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_arguments", await ReadErrorCodeAsync(response));
        Assert.Equal(0, await fx.CountExecutionsAsync(workspaceId));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("has space in key")]
    public async Task Invalid_Idempotency_Key_Is_Rejected(string key)
    {
        var workspaceId = await fx.CreateWorkspaceAsync(fx.Owner);
        using var owner = fx.Client(fx.Owner);

        var response = await RequestToolAsync(owner, workspaceId, Echo, new { text = "hi" }, key);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_idempotency_key", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task Oversized_Arguments_Are_Rejected_Including_Whitespace_Padding()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(fx.Owner);
        using var owner = fx.Client(fx.Owner);

        var tooLong = await RequestToolAsync(
            owner, workspaceId, Echo, new { text = new string('x', 501) }, NewKey());
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);

        // Regression: length used to be measured after Trim(), so padding let
        // an arbitrarily large raw value through into ArgumentsJson.
        var padded = await RequestToolAsync(
            owner, workspaceId, Echo, new { text = new string(' ', 20_000) + "x" }, NewKey());
        Assert.Equal(HttpStatusCode.BadRequest, padded.StatusCode);

        var noteTooLong = await RequestToolAsync(
            owner, workspaceId, Note, new { message = new string('m', 501) }, NewKey());
        Assert.Equal(HttpStatusCode.BadRequest, noteTooLong.StatusCode);

        Assert.Equal(0, await fx.CountExecutionsAsync(workspaceId));

        var boundary = await RequestToolAsync(
            owner, workspaceId, Echo, new { text = new string('x', 500) }, NewKey());
        Assert.Equal(HttpStatusCode.OK, boundary.StatusCode);
    }

    [Fact]
    public async Task Member_ReadOnly_Execution_Succeeds_And_Result_Persists()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(
            fx.Owner, (fx.Member, WorkspaceRole.Member));
        using var member = fx.Client(fx.Member);

        var response = await RequestToolAsync(
            member, workspaceId, "Workspace.Echo", new { text = "  persisted  " }, NewKey());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ReadJsonAsync(response);
        var executionId = json.GetProperty("id").GetGuid();
        Assert.Equal("Succeeded", json.GetProperty("status").GetString());
        Assert.Equal("persisted", json.GetProperty("result").GetProperty("text").GetString());
        // The tool sees the server-resolved workspace, never a client value.
        Assert.Equal(workspaceId, json.GetProperty("result").GetProperty("workspaceId").GetGuid());

        var row = await fx.FindExecutionAsync(executionId);
        Assert.NotNull(row);
        Assert.Equal(workspaceId, row.WorkspaceId);
        Assert.Equal(fx.Member.UserId, row.RequestedByUserId);
        Assert.Equal(Echo, row.ToolName);
        Assert.Equal(ToolRiskLevel.ReadOnly, row.RiskLevel);
        Assert.Equal(ToolExecutionStatus.Succeeded, row.Status);
        Assert.Matches("^[0-9a-f]{64}$", row.ArgumentsHash);
        Assert.NotNull(row.StartedAtUtc);
        Assert.NotNull(row.CompletedAtUtc);
        Assert.Null(row.ApprovedByUserId);
        Assert.Null(row.ErrorCode);
        using (var result = JsonDocument.Parse(row.ResultJson!))
            Assert.Equal("persisted", result.RootElement.GetProperty("text").GetString());

        Assert.Equal(
            [ToolExecutionAuditEventType.Requested, ToolExecutionAuditEventType.Started, ToolExecutionAuditEventType.Succeeded],
            await fx.AuditTrailAsync(executionId));
    }

    [Fact]
    public async Task NonMember_Cannot_List_Request_Read_Approve_Or_Reject()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(
            fx.Owner, (fx.Admin, WorkspaceRole.Admin));
        using var admin = fx.Client(fx.Admin);
        var pending = await ReadJsonAsync(await RequestToolAsync(
            admin, workspaceId, Note, new { message = "tenant secret" }, NewKey()));
        var executionId = pending.GetProperty("id").GetGuid();

        using var outsider = fx.Client(fx.Outsider);
        var basePath = $"/api/workspaces/{workspaceId}";

        var responses = new[]
        {
            await outsider.GetAsync($"{basePath}/tools"),
            await outsider.GetAsync($"{basePath}/tool-executions"),
            await outsider.GetAsync($"{basePath}/tool-executions/{executionId}"),
            await RequestToolAsync(outsider, workspaceId, Echo, new { text = "x" }, NewKey()),
            await ApproveAsync(outsider, workspaceId, executionId),
            await RejectAsync(outsider, workspaceId, executionId),
        };

        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("workspace_not_found", await ReadErrorCodeAsync(response));
        }

        // No existence oracle: a real execution ID and a random one look identical.
        var random = await outsider.GetAsync($"{basePath}/tool-executions/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, random.StatusCode);
        Assert.Equal("workspace_not_found", await ReadErrorCodeAsync(random));

        var row = await fx.FindExecutionAsync(executionId);
        Assert.Equal(ToolExecutionStatus.PendingApproval, row!.Status);
        Assert.Equal(0, await fx.CountNotesAsync(executionId));
    }

    [Fact]
    public async Task Member_Cannot_Request_Approve_Or_Reject_Sensitive_Tool()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(
            fx.Owner,
            (fx.Admin, WorkspaceRole.Admin),
            (fx.Member, WorkspaceRole.Member));
        using var member = fx.Client(fx.Member);
        using var admin = fx.Client(fx.Admin);

        var memberRequest = await RequestToolAsync(
            member, workspaceId, Note, new { message = "member" }, NewKey());
        Assert.Equal(HttpStatusCode.Forbidden, memberRequest.StatusCode);
        Assert.Equal(0, await fx.CountExecutionsAsync(workspaceId));

        var pending = await ReadJsonAsync(await RequestToolAsync(
            admin, workspaceId, Note, new { message = "admin" }, NewKey()));
        var executionId = pending.GetProperty("id").GetGuid();

        var approve = await ApproveAsync(member, workspaceId, executionId);
        Assert.Equal(HttpStatusCode.Forbidden, approve.StatusCode);
        Assert.Equal("forbidden", await ReadErrorCodeAsync(approve));

        var reject = await RejectAsync(member, workspaceId, executionId);
        Assert.Equal(HttpStatusCode.Forbidden, reject.StatusCode);

        // Members cannot read Admin-only sensitive execution details.
        var get = await member.GetAsync($"/api/workspaces/{workspaceId}/tool-executions/{executionId}");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal("execution_not_found", await ReadErrorCodeAsync(get));

        Assert.Equal(ToolExecutionStatus.PendingApproval, (await fx.FindExecutionAsync(executionId))!.Status);
        Assert.Equal(0, await fx.CountNotesAsync(executionId));
    }

    [Fact]
    public async Task Sensitive_Execution_Starts_PendingApproval_And_Cannot_Run_Before_Approval()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(
            fx.Owner, (fx.Admin, WorkspaceRole.Admin));
        using var admin = fx.Client(fx.Admin);
        var key = NewKey("pending");

        var first = await ReadJsonAsync(await RequestToolAsync(
            admin, workspaceId, Note, new { message = "wait" }, key));
        var executionId = first.GetProperty("id").GetGuid();
        Assert.Equal("PendingApproval", first.GetProperty("status").GetString());
        Assert.True(
            !first.TryGetProperty("startedAtUtc", out var startedAt) ||
            startedAt.ValueKind == JsonValueKind.Null);

        // Replaying the request never executes a pending sensitive tool.
        var replay = await ReadJsonAsync(await RequestToolAsync(
            admin, workspaceId, Note, new { message = "wait" }, key));
        Assert.Equal(executionId, replay.GetProperty("id").GetGuid());
        Assert.Equal("PendingApproval", replay.GetProperty("status").GetString());

        Assert.Equal(0, await fx.CountNotesAsync(executionId));
        Assert.Equal(
            [ToolExecutionAuditEventType.Requested],
            await fx.AuditTrailAsync(executionId));
    }

    [Fact]
    public async Task Requester_Cannot_Approve_Or_Reject_Own_Sensitive_Execution()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(
            fx.Owner, (fx.Admin, WorkspaceRole.Admin));
        using var admin = fx.Client(fx.Admin);
        using var owner = fx.Client(fx.Owner);

        foreach (var (client, label) in new[] { (admin, "admin"), (owner, "owner") })
        {
            var pending = await ReadJsonAsync(await RequestToolAsync(
                client, workspaceId, Note, new { message = $"self {label}" }, NewKey()));
            var executionId = pending.GetProperty("id").GetGuid();

            var approve = await ApproveAsync(client, workspaceId, executionId);
            Assert.Equal(HttpStatusCode.Forbidden, approve.StatusCode);
            Assert.Equal("self_approval_forbidden", await ReadErrorCodeAsync(approve));

            var reject = await RejectAsync(client, workspaceId, executionId);
            Assert.Equal(HttpStatusCode.Forbidden, reject.StatusCode);
            Assert.Equal("self_approval_forbidden", await ReadErrorCodeAsync(reject));

            var row = await fx.FindExecutionAsync(executionId);
            Assert.Equal(ToolExecutionStatus.PendingApproval, row!.Status);
            Assert.Null(row.ApprovedByUserId);
            Assert.Equal(0, await fx.CountNotesAsync(executionId));
        }
    }

    [Fact]
    public async Task Owner_Approves_Admin_Request_And_Execution_Runs_Exactly_Once()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(
            fx.Owner,
            (fx.Admin, WorkspaceRole.Admin),
            (fx.Admin2, WorkspaceRole.Admin));
        using var admin = fx.Client(fx.Admin);
        using var admin2 = fx.Client(fx.Admin2);
        using var owner = fx.Client(fx.Owner);
        var key = NewKey("once");

        var pending = await ReadJsonAsync(await RequestToolAsync(
            admin, workspaceId, Note, new { message = "run once" }, key));
        var executionId = pending.GetProperty("id").GetGuid();

        var approval = await ApproveAsync(owner, workspaceId, executionId);
        Assert.Equal(HttpStatusCode.OK, approval.StatusCode);
        var approved = await ReadJsonAsync(approval);
        Assert.Equal("Succeeded", approved.GetProperty("status").GetString());
        Assert.Equal(fx.Owner.UserId, approved.GetProperty("approvedByUserId").GetGuid());

        var secondApproval = await ApproveAsync(admin2, workspaceId, executionId);
        Assert.Equal(HttpStatusCode.Conflict, secondApproval.StatusCode);
        Assert.Equal("invalid_state", await ReadErrorCodeAsync(secondApproval));

        var lateReject = await RejectAsync(admin2, workspaceId, executionId);
        Assert.Equal(HttpStatusCode.Conflict, lateReject.StatusCode);

        var replay = await ReadJsonAsync(await RequestToolAsync(
            admin, workspaceId, Note, new { message = "run once" }, key));
        Assert.Equal(executionId, replay.GetProperty("id").GetGuid());
        Assert.Equal("Succeeded", replay.GetProperty("status").GetString());

        Assert.Equal(1, await fx.CountNotesAsync(executionId));
        var row = await fx.FindExecutionAsync(executionId);
        Assert.Equal(ToolExecutionStatus.Succeeded, row!.Status);
        Assert.Equal(fx.Owner.UserId, row.ApprovedByUserId);
        Assert.Equal(
            [
                ToolExecutionAuditEventType.Requested,
                ToolExecutionAuditEventType.Approved,
                ToolExecutionAuditEventType.Started,
                ToolExecutionAuditEventType.Succeeded
            ],
            await fx.AuditTrailAsync(executionId));
    }

    [Fact]
    public async Task Rejected_Execution_Is_Terminal_And_Never_Runs()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(
            fx.Owner, (fx.Admin, WorkspaceRole.Admin));
        using var admin = fx.Client(fx.Admin);
        using var owner = fx.Client(fx.Owner);
        var key = NewKey("rejected");

        var pending = await ReadJsonAsync(await RequestToolAsync(
            admin, workspaceId, Note, new { message = "no" }, key));
        var executionId = pending.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.OK, (await RejectAsync(owner, workspaceId, executionId)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ApproveAsync(owner, workspaceId, executionId)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await RejectAsync(owner, workspaceId, executionId)).StatusCode);

        var replay = await ReadJsonAsync(await RequestToolAsync(
            admin, workspaceId, Note, new { message = "no" }, key));
        Assert.Equal(executionId, replay.GetProperty("id").GetGuid());
        Assert.Equal("Rejected", replay.GetProperty("status").GetString());

        var row = await fx.FindExecutionAsync(executionId);
        Assert.Equal(ToolExecutionStatus.Rejected, row!.Status);
        Assert.Null(row.StartedAtUtc);
        Assert.Equal(0, await fx.CountNotesAsync(executionId));
    }

    [Fact]
    public async Task Duplicate_Idempotency_Key_Never_Duplicates_Side_Effects()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(
            fx.Owner, (fx.Admin, WorkspaceRole.Admin));
        using var admin = fx.Client(fx.Admin);
        using var owner = fx.Client(fx.Owner);
        var key = NewKey("dup");

        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var response = await RequestToolAsync(admin, workspaceId, Note, new { message = "dup" }, key);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            ids.Add((await ReadJsonAsync(response)).GetProperty("id").GetGuid());
        }
        Assert.Single(ids.Distinct());

        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(owner, workspaceId, ids[0])).StatusCode);

        for (var i = 0; i < 2; i++)
        {
            var response = await RequestToolAsync(admin, workspaceId, Note, new { message = "dup" }, key);
            Assert.Equal(ids[0], (await ReadJsonAsync(response)).GetProperty("id").GetGuid());
        }

        // A different requester reusing the key in the same workspace also
        // resolves to the one existing execution instead of a second side effect.
        var fromOwner = await RequestToolAsync(owner, workspaceId, Note, new { message = "dup" }, key);
        Assert.Equal(ids[0], (await ReadJsonAsync(fromOwner)).GetProperty("id").GetGuid());

        Assert.Equal(1, await fx.CountExecutionsAsync(workspaceId));
        Assert.Equal(1, await fx.CountNotesAsync(ids[0]));
    }

    [Fact]
    public async Task Same_Idempotency_Key_In_Different_Workspaces_Does_Not_Collide()
    {
        var workspaceA = await fx.CreateWorkspaceAsync(fx.Owner, (fx.Admin, WorkspaceRole.Admin));
        var workspaceB = await fx.CreateWorkspaceAsync(fx.Owner, (fx.Admin, WorkspaceRole.Admin));
        using var owner = fx.Client(fx.Owner);
        using var admin = fx.Client(fx.Admin);
        var key = NewKey("shared");

        var echoA = await ReadJsonAsync(await RequestToolAsync(owner, workspaceA, Echo, new { text = "same" }, key));
        var echoB = await ReadJsonAsync(await RequestToolAsync(owner, workspaceB, Echo, new { text = "same" }, key));
        Assert.NotEqual(echoA.GetProperty("id").GetGuid(), echoB.GetProperty("id").GetGuid());
        Assert.Equal(workspaceA, echoA.GetProperty("workspaceId").GetGuid());
        Assert.Equal(workspaceB, echoB.GetProperty("workspaceId").GetGuid());
        Assert.Equal("Succeeded", echoB.GetProperty("status").GetString());

        // Same key, different tool in the same workspace is a separate execution too.
        var noteA = await ReadJsonAsync(await RequestToolAsync(admin, workspaceA, Note, new { message = "same" }, key));
        var noteB = await ReadJsonAsync(await RequestToolAsync(admin, workspaceB, Note, new { message = "same" }, key));
        var noteAId = noteA.GetProperty("id").GetGuid();
        var noteBId = noteB.GetProperty("id").GetGuid();
        Assert.NotEqual(noteAId, noteBId);
        Assert.NotEqual(echoA.GetProperty("id").GetGuid(), noteAId);

        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(owner, workspaceA, noteAId)).StatusCode);
        Assert.Equal(1, await fx.CountNotesAsync(noteAId));
        Assert.Equal(0, await fx.CountNotesAsync(noteBId));
        Assert.Equal(ToolExecutionStatus.PendingApproval, (await fx.FindExecutionAsync(noteBId))!.Status);
    }

    [Fact]
    public async Task Approving_Execution_A_Does_Not_Approve_Execution_B()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(fx.Owner, (fx.Admin, WorkspaceRole.Admin));
        using var admin = fx.Client(fx.Admin);
        using var owner = fx.Client(fx.Owner);

        var a = (await ReadJsonAsync(await RequestToolAsync(admin, workspaceId, Note, new { message = "A" }, NewKey("a")))).GetProperty("id").GetGuid();
        var b = (await ReadJsonAsync(await RequestToolAsync(admin, workspaceId, Note, new { message = "B" }, NewKey("b")))).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(owner, workspaceId, a)).StatusCode);

        var rowB = await fx.FindExecutionAsync(b);
        Assert.Equal(ToolExecutionStatus.PendingApproval, rowB!.Status);
        Assert.Null(rowB.ApprovedByUserId);
        Assert.Equal(1, await fx.CountNotesAsync(a));
        Assert.Equal(0, await fx.CountNotesAsync(b));
    }

    [Fact]
    public async Task Arguments_Are_Immutable_After_Creation_And_Approval()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(fx.Owner, (fx.Admin, WorkspaceRole.Admin));
        using var admin = fx.Client(fx.Admin);
        using var owner = fx.Client(fx.Owner);
        var key = NewKey("immutable");

        var pending = await ReadJsonAsync(await RequestToolAsync(admin, workspaceId, Note, new { message = "original" }, key));
        var executionId = pending.GetProperty("id").GetGuid();
        var before = await fx.FindExecutionAsync(executionId);

        var tamperBefore = await RequestToolAsync(admin, workspaceId, Note, new { message = "tampered" }, key);
        Assert.Equal(HttpStatusCode.Conflict, tamperBefore.StatusCode);
        Assert.Equal("idempotency_conflict", await ReadErrorCodeAsync(tamperBefore));

        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(owner, workspaceId, executionId)).StatusCode);

        var tamperAfter = await RequestToolAsync(admin, workspaceId, Note, new { message = "tampered" }, key);
        Assert.Equal(HttpStatusCode.Conflict, tamperAfter.StatusCode);

        var after = await fx.FindExecutionAsync(executionId);
        Assert.Equal(before!.ArgumentsJson, after!.ArgumentsJson);
        Assert.Equal(before.ArgumentsHash, after.ArgumentsHash);

        using var scope = fx.Factory.Services.CreateScope();
        var notes = scope.ServiceProvider.GetRequiredService<ICEHOTT.Application.Abstractions.IWorkspaceAuditNoteRepository>();
        var note = await notes.FindByExecutionAsync(workspaceId, executionId);
        Assert.Equal("original", note!.Message);
    }

    [Fact]
    public async Task Cross_Workspace_Get_Approve_And_Reject_Are_Impossible()
    {
        var workspaceA = await fx.CreateWorkspaceAsync(fx.Owner, (fx.Admin, WorkspaceRole.Admin));
        // Admin2 owns a different workspace and is not a member of A.
        var workspaceB = await fx.CreateWorkspaceAsync(fx.Admin2);
        using var admin = fx.Client(fx.Admin);
        using var attacker = fx.Client(fx.Admin2);

        var executionId = (await ReadJsonAsync(await RequestToolAsync(
            admin, workspaceA, Note, new { message = "A only" }, NewKey()))).GetProperty("id").GetGuid();

        // Using the attacker's own workspace in the route with A's execution ID.
        var viaOwnWorkspace = new[]
        {
            await attacker.GetAsync($"/api/workspaces/{workspaceB}/tool-executions/{executionId}"),
            await ApproveAsync(attacker, workspaceB, executionId),
            await RejectAsync(attacker, workspaceB, executionId),
        };
        foreach (var response in viaOwnWorkspace)
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("execution_not_found", await ReadErrorCodeAsync(response));
        }

        // Using A's route directly.
        var viaVictimWorkspace = new[]
        {
            await attacker.GetAsync($"/api/workspaces/{workspaceA}/tool-executions/{executionId}"),
            await ApproveAsync(attacker, workspaceA, executionId),
            await RejectAsync(attacker, workspaceA, executionId),
        };
        foreach (var response in viaVictimWorkspace)
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("workspace_not_found", await ReadErrorCodeAsync(response));
        }

        var list = await ReadJsonAsync(await attacker.GetAsync($"/api/workspaces/{workspaceB}/tool-executions"));
        Assert.DoesNotContain(list.EnumerateArray(), x => x.GetProperty("id").GetGuid() == executionId);

        var row = await fx.FindExecutionAsync(executionId);
        Assert.Equal(ToolExecutionStatus.PendingApproval, row!.Status);
        Assert.Equal(workspaceA, row.WorkspaceId);
        Assert.Equal(0, await fx.CountNotesAsync(executionId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Sensitive_Tool_Is_Not_Executed_For_Demoted_Or_Removed_Requester(bool removeEntirely)
    {
        var workspaceId = await fx.CreateWorkspaceAsync(fx.Owner, (fx.Admin, WorkspaceRole.Admin));
        using var admin = fx.Client(fx.Admin);
        using var owner = fx.Client(fx.Owner);

        var executionId = (await ReadJsonAsync(await RequestToolAsync(
            admin, workspaceId, Note, new { message = "stale authority" }, NewKey()))).GetProperty("id").GetGuid();

        await fx.SetMembershipAsync(
            workspaceId,
            fx.Admin.UserId,
            removeEntirely ? null : WorkspaceRole.Member);

        var approve = await ApproveAsync(owner, workspaceId, executionId);
        Assert.Equal(HttpStatusCode.Conflict, approve.StatusCode);
        Assert.Equal("requester_no_longer_authorized", await ReadErrorCodeAsync(approve));

        var row = await fx.FindExecutionAsync(executionId);
        Assert.Equal(ToolExecutionStatus.PendingApproval, row!.Status);
        Assert.Equal(0, await fx.CountNotesAsync(executionId));

        // The stale request can still be cleaned up by rejecting it.
        Assert.Equal(HttpStatusCode.OK, (await RejectAsync(owner, workspaceId, executionId)).StatusCode);
        Assert.Equal(ToolExecutionStatus.Rejected, (await fx.FindExecutionAsync(executionId))!.Status);
    }
}
