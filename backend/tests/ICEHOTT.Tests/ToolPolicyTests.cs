using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ICEHOTT.Domain.Tools;
using ICEHOTT.Domain.Workspaces;
using static ICEHOTT.Tests.ToolSecurityFixture;

namespace ICEHOTT.Tests;

/// <summary>
/// Phase 4.5 Work Packet B: per-workspace tool policy overlay (HTTP level).
/// Contract: docs/PHASE-4.5-B-TOOL-POLICY.md (decisions D1-D10).
/// </summary>
public sealed class ToolPolicyTests(ToolSecurityFixture fx)
    : IClassFixture<ToolSecurityFixture>
{
    private const string Echo = "workspace.echo";
    private const string Note = "workspace.audit-note.create";

    // ---------- helpers ----------

    internal static object Policy(
        int expectedVersion,
        bool enabled = true,
        string? requester = null,
        string? approver = null,
        bool requiresApproval = false,
        int? maxArgumentLength = null) =>
        new
        {
            expectedVersion,
            enabled,
            minimumRequesterRole = requester,
            minimumApproverRole = approver,
            requiresApproval,
            maxArgumentLength
        };

    internal static Task<HttpResponseMessage> PutPolicyAsync(
        HttpClient client, Guid workspaceId, string toolName, object body) =>
        client.PutAsJsonAsync(
            $"/api/workspaces/{workspaceId}/tool-policies/{toolName}", body);

    internal static Task<HttpResponseMessage> PutRawPolicyAsync(
        HttpClient client, Guid workspaceId, string toolName, string json) =>
        client.PutAsync(
            $"/api/workspaces/{workspaceId}/tool-policies/{toolName}",
            new StringContent(json, Encoding.UTF8, "application/json"));

    private async Task<int> PolicyVersionAsync(Guid workspaceId, string toolName)
    {
        using var owner = fx.Client(fx.Owner);
        var response = await owner.GetAsync($"/api/workspaces/{workspaceId}/tool-policies/{toolName}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReadJsonAsync(response)).GetProperty("version").GetInt32();
    }

    private async Task<Guid> PendingNoteAsync(Guid workspaceId, ToolTestIdentity requester, string message = "pending")
    {
        using var client = fx.Client(requester);
        var response = await RequestToolAsync(client, workspaceId, Note, new { message }, NewKey("note"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ReadJsonAsync(response);
        Assert.Equal("PendingApproval", json.GetProperty("status").GetString());
        return json.GetProperty("id").GetGuid();
    }

    // ---------- policy API authorisation / tenant isolation ----------

    [Fact]
    public async Task Policy_Api_Resolves_Membership_And_Only_Owner_Can_Write()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(
            fx.Owner, (fx.Admin, WorkspaceRole.Admin), (fx.Member, WorkspaceRole.Member));
        var path = $"/api/workspaces/{workspaceId}/tool-policies";

        using var outsider = fx.Client(fx.Outsider);
        foreach (var response in new[]
                 {
                     await outsider.GetAsync(path),
                     await outsider.GetAsync($"{path}/{Echo}"),
                     await PutPolicyAsync(outsider, workspaceId, Echo, Policy(0, enabled: false)),
                     await outsider.GetAsync($"{path}/{Echo}/audit"),
                 })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("workspace_not_found", await ReadErrorCodeAsync(response));
        }

        using var member = fx.Client(fx.Member);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await PutPolicyAsync(member, workspaceId, Echo, Policy(0, enabled: false))).StatusCode);

        using var admin = fx.Client(fx.Admin);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(path)).StatusCode);
        var adminPut = await PutPolicyAsync(admin, workspaceId, Echo, Policy(0, enabled: false));
        Assert.Equal(HttpStatusCode.Forbidden, adminPut.StatusCode);
        Assert.Equal("forbidden", await ReadErrorCodeAsync(adminPut));
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync($"{path}/{Echo}/audit")).StatusCode);

        Assert.Equal(0, await PolicyVersionAsync(workspaceId, Echo));

        using var owner = fx.Client(fx.Owner);
        var ownerPut = await PutPolicyAsync(owner, workspaceId, Echo, Policy(0, enabled: false));
        Assert.Equal(HttpStatusCode.OK, ownerPut.StatusCode);
        var body = await ReadJsonAsync(ownerPut);
        Assert.Equal(1, body.GetProperty("version").GetInt32());
        Assert.False(body.GetProperty("enabled").GetBoolean());
        Assert.False(body.GetProperty("effective").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Unknown_Tool_Policy_Is_Rejected()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(fx.Owner);
        using var owner = fx.Client(fx.Owner);

        var put = await PutPolicyAsync(owner, workspaceId, "workspace.shell", Policy(0));
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        Assert.Equal("tool_not_found", await ReadErrorCodeAsync(put));

        var get = await owner.GetAsync($"/api/workspaces/{workspaceId}/tool-policies/workspace.shell");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact]
    public async Task Policy_Is_Isolated_Per_Workspace()
    {
        var workspaceA = await fx.CreateWorkspaceAsync(fx.Owner, (fx.Member, WorkspaceRole.Member));
        var workspaceB = await fx.CreateWorkspaceAsync(fx.Owner, (fx.Member, WorkspaceRole.Member));
        var workspaceC = await fx.CreateWorkspaceAsync(fx.Admin2); // Admin2 is Owner of C only

        using var owner = fx.Client(fx.Owner);
        Assert.Equal(HttpStatusCode.OK,
            (await PutPolicyAsync(owner, workspaceA, Echo, Policy(0, enabled: false))).StatusCode);

        using var member = fx.Client(fx.Member);
        var inA = await RequestToolAsync(member, workspaceA, Echo, new { text = "a" }, NewKey());
        Assert.Equal(HttpStatusCode.Conflict, inA.StatusCode);
        Assert.Equal("tool_disabled", await ReadErrorCodeAsync(inA));

        var inB = await RequestToolAsync(member, workspaceB, Echo, new { text = "b" }, NewKey());
        Assert.Equal(HttpStatusCode.OK, inB.StatusCode);
        Assert.Equal(0, await PolicyVersionAsync(workspaceB, Echo));

        // The Owner of another workspace cannot read or change A's policy.
        using var otherOwner = fx.Client(fx.Admin2);
        var foreignPut = await PutPolicyAsync(otherOwner, workspaceA, Echo, Policy(1, enabled: true));
        Assert.Equal(HttpStatusCode.NotFound, foreignPut.StatusCode);
        Assert.Equal("workspace_not_found", await ReadErrorCodeAsync(foreignPut));
        Assert.Equal(HttpStatusCode.NotFound,
            (await otherOwner.GetAsync($"/api/workspaces/{workspaceA}/tool-policies")).StatusCode);

        // Owning C gives no authority over A even through C's route.
        var viaOwnRoute = await otherOwner.GetAsync($"/api/workspaces/{workspaceC}/tool-policies/{Echo}");
        Assert.Equal(0, (await ReadJsonAsync(viaOwnRoute)).GetProperty("version").GetInt32());
        Assert.Equal(1, await PolicyVersionAsync(workspaceA, Echo));
    }

    // ---------- only-tighten / downgrade rejection ----------

    [Theory]
    [InlineData(Note, """{"expectedVersion":0,"enabled":true,"requiresApproval":false}""", "policy_downgrade_rejected")]
    [InlineData(Note, """{"expectedVersion":0,"enabled":true,"requiresApproval":true,"minimumRequesterRole":"Member"}""", "policy_downgrade_rejected")]
    [InlineData(Note, """{"expectedVersion":0,"enabled":true,"requiresApproval":true,"minimumApproverRole":"Member"}""", "policy_downgrade_rejected")]
    [InlineData(Note, """{"expectedVersion":0,"enabled":true,"requiresApproval":true,"riskLevel":"ReadOnly"}""", "policy_downgrade_rejected")]
    [InlineData(Echo, """{"expectedVersion":0,"enabled":true,"requiresApproval":true,"minimumApproverRole":"Member"}""", "policy_downgrade_rejected")]
    [InlineData(Echo, """{"expectedVersion":0,"enabled":true,"requiresApproval":false,"maxArgumentLength":501}""", "invalid_policy")]
    [InlineData(Echo, """{"expectedVersion":0,"enabled":true,"requiresApproval":false,"maxArgumentLength":0}""", "invalid_policy")]
    [InlineData(Echo, """{"expectedVersion":0,"enabled":true,"requiresApproval":false,"minimumRequesterRole":1}""", "invalid_policy")]
    [InlineData(Echo, """{"expectedVersion":0,"enabled":true,"requiresApproval":false,"minimumRequesterRole":"Superuser"}""", "invalid_policy")]
    [InlineData(Echo, """{"expectedVersion":0,"enabled":true,"requiresApproval":false,"approved":true}""", "invalid_policy")]
    [InlineData(Echo, """{"expectedVersion":0,"enabled":true,"enabled":false,"requiresApproval":false}""", "invalid_policy")]
    [InlineData(Echo, """{"expectedVersion":0,"requiresApproval":false}""", "invalid_policy")]
    [InlineData(Echo, """{"expectedVersion":-1,"enabled":true,"requiresApproval":false}""", "invalid_policy")]
    [InlineData(Echo, """[]""", "invalid_policy")]
    public async Task Policy_Downgrade_And_Malformed_Policies_Are_Rejected(string tool, string body, string expectedCode)
    {
        var workspaceId = await fx.CreateWorkspaceAsync(fx.Owner);
        using var owner = fx.Client(fx.Owner);

        var response = await PutRawPolicyAsync(owner, workspaceId, tool, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expectedCode, await ReadErrorCodeAsync(response));
        Assert.Equal(0, await PolicyVersionAsync(workspaceId, tool));

        var audit = await ReadJsonAsync(await owner.GetAsync(
            $"/api/workspaces/{workspaceId}/tool-policies/{tool}/audit"));
        Assert.Equal(0, audit.GetArrayLength());
    }

    [Fact]
    public async Task SensitiveWrite_Stays_Sensitive_And_Approval_Required_Under_Any_Policy()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(fx.Owner, (fx.Admin, WorkspaceRole.Admin));
        using var owner = fx.Client(fx.Owner);

        var put = await PutPolicyAsync(owner, workspaceId, Note,
            Policy(0, requiresApproval: true, maxArgumentLength: 100));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var effective = (await ReadJsonAsync(put)).GetProperty("effective");
        Assert.True(effective.GetProperty("requiresApproval").GetBoolean());
        Assert.Equal("Admin", effective.GetProperty("minimumApproverRole").GetString());

        var executionId = await PendingNoteAsync(workspaceId, fx.Admin);
        var row = await fx.FindExecutionAsync(executionId);
        Assert.Equal(ToolRiskLevel.SensitiveWrite, row!.RiskLevel);
        Assert.Equal(ToolExecutionStatus.PendingApproval, row.Status);
    }

    // ---------- versioning, optimistic concurrency, audit ----------

    [Fact]
    public async Task Stale_Version_Is_Rejected_And_Every_Change_Is_Audited()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(fx.Owner);
        using var owner = fx.Client(fx.Owner);

        Assert.Equal(HttpStatusCode.OK,
            (await PutPolicyAsync(owner, workspaceId, Echo, Policy(0, requester: "Admin"))).StatusCode);

        var stale = await PutPolicyAsync(owner, workspaceId, Echo, Policy(0, enabled: false));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("policy_version_conflict", await ReadErrorCodeAsync(stale));

        var ahead = await PutPolicyAsync(owner, workspaceId, Echo, Policy(5, enabled: false));
        Assert.Equal(HttpStatusCode.Conflict, ahead.StatusCode);

        Assert.Equal(HttpStatusCode.OK,
            (await PutPolicyAsync(owner, workspaceId, Echo, Policy(1, enabled: false))).StatusCode);
        Assert.Equal(2, await PolicyVersionAsync(workspaceId, Echo));

        var audit = await ReadJsonAsync(await owner.GetAsync(
            $"/api/workspaces/{workspaceId}/tool-policies/{Echo}/audit"));
        Assert.Equal(2, audit.GetArrayLength());

        var first = audit[0];
        Assert.Equal(0, first.GetProperty("previousVersion").GetInt32());
        Assert.Equal(1, first.GetProperty("newVersion").GetInt32());
        Assert.Equal(fx.Owner.UserId, first.GetProperty("actorUserId").GetGuid());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("previousPolicy").ValueKind);
        Assert.Equal("Admin", first.GetProperty("newPolicy").GetProperty("minimumRequesterRole").GetString());

        var second = audit[1];
        Assert.Equal(1, second.GetProperty("previousVersion").GetInt32());
        Assert.Equal(2, second.GetProperty("newVersion").GetInt32());
        Assert.True(second.GetProperty("previousPolicy").GetProperty("enabled").GetBoolean());
        Assert.False(second.GetProperty("newPolicy").GetProperty("enabled").GetBoolean());
    }

    // ---------- request-time enforcement ----------

    [Fact]
    public async Task Disabled_Tool_Cannot_Be_Requested_And_Is_Hidden_From_Tool_List()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(fx.Owner, (fx.Member, WorkspaceRole.Member));
        using var owner = fx.Client(fx.Owner);
        using var member = fx.Client(fx.Member);

        Assert.Equal(HttpStatusCode.OK,
            (await PutPolicyAsync(owner, workspaceId, Echo, Policy(0, enabled: false))).StatusCode);

        var request = await RequestToolAsync(member, workspaceId, Echo, new { text = "x" }, NewKey());
        Assert.Equal(HttpStatusCode.Conflict, request.StatusCode);
        Assert.Equal("tool_disabled", await ReadErrorCodeAsync(request));
        Assert.Equal(0, await fx.CountExecutionsAsync(workspaceId));

        var tools = await ReadJsonAsync(await owner.GetAsync($"/api/workspaces/{workspaceId}/tools"));
        Assert.DoesNotContain(tools.EnumerateArray(), x => x.GetProperty("name").GetString() == Echo);

        Assert.Equal(HttpStatusCode.OK,
            (await PutPolicyAsync(owner, workspaceId, Echo, Policy(1, enabled: true))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await RequestToolAsync(member, workspaceId, Echo, new { text = "x" }, NewKey())).StatusCode);
    }

    [Fact]
    public async Task Raised_Requester_Role_Is_Enforced_And_Listed()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(
            fx.Owner, (fx.Admin, WorkspaceRole.Admin), (fx.Member, WorkspaceRole.Member));
        using var owner = fx.Client(fx.Owner);
        Assert.Equal(HttpStatusCode.OK,
            (await PutPolicyAsync(owner, workspaceId, Echo, Policy(0, requester: "Admin"))).StatusCode);

        using var member = fx.Client(fx.Member);
        var denied = await RequestToolAsync(member, workspaceId, Echo, new { text = "x" }, NewKey());
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var memberTools = await ReadJsonAsync(await member.GetAsync($"/api/workspaces/{workspaceId}/tools"));
        Assert.DoesNotContain(memberTools.EnumerateArray(), x => x.GetProperty("name").GetString() == Echo);

        using var admin = fx.Client(fx.Admin);
        var allowed = await RequestToolAsync(admin, workspaceId, Echo, new { text = "x" }, NewKey());
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

        var adminTools = await ReadJsonAsync(await admin.GetAsync($"/api/workspaces/{workspaceId}/tools"));
        var echo = adminTools.EnumerateArray().Single(x => x.GetProperty("name").GetString() == Echo);
        Assert.Equal("Admin", echo.GetProperty("minimumRequesterRole").GetString());
        Assert.Equal(1, echo.GetProperty("policyVersion").GetInt32());
    }

    [Fact]
    public async Task Policy_Required_Approval_Turns_ReadOnly_Tool_Into_PendingApproval()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(
            fx.Owner, (fx.Admin, WorkspaceRole.Admin), (fx.Member, WorkspaceRole.Member));
        using var owner = fx.Client(fx.Owner);
        Assert.Equal(HttpStatusCode.OK,
            (await PutPolicyAsync(owner, workspaceId, Echo, Policy(0, requiresApproval: true))).StatusCode);

        using var member = fx.Client(fx.Member);
        var pending = await ReadJsonAsync(await RequestToolAsync(member, workspaceId, Echo, new { text = "gated" }, NewKey()));
        Assert.Equal("PendingApproval", pending.GetProperty("status").GetString());
        var executionId = pending.GetProperty("id").GetGuid();

        var row = await fx.FindExecutionAsync(executionId);
        Assert.Equal(ToolRiskLevel.ReadOnly, row!.RiskLevel);
        Assert.Equal(1, row.PolicyVersion);
        Assert.True(row.PolicyRequiresApproval);
        Assert.Equal(WorkspaceRole.Admin, row.PolicyMinimumApproverRole);

        Assert.Equal(HttpStatusCode.Forbidden, (await ApproveAsync(member, workspaceId, executionId)).StatusCode);

        using var admin = fx.Client(fx.Admin);
        var approved = await ReadJsonAsync(await ApproveAsync(admin, workspaceId, executionId));
        Assert.Equal("Succeeded", approved.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Lower_Argument_Limit_Is_Enforced_At_Request()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(fx.Owner);
        using var owner = fx.Client(fx.Owner);
        Assert.Equal(HttpStatusCode.OK,
            (await PutPolicyAsync(owner, workspaceId, Echo, Policy(0, maxArgumentLength: 10))).StatusCode);

        var tooLong = await RequestToolAsync(owner, workspaceId, Echo, new { text = new string('x', 11) }, NewKey());
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal("invalid_arguments", await ReadErrorCodeAsync(tooLong));

        var ok = await RequestToolAsync(owner, workspaceId, Echo, new { text = new string('x', 10) }, NewKey());
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var tools = await ReadJsonAsync(await owner.GetAsync($"/api/workspaces/{workspaceId}/tools"));
        var echo = tools.EnumerateArray().Single(x => x.GetProperty("name").GetString() == Echo);
        Assert.Equal(10, echo.GetProperty("arguments")[0].GetProperty("maxLength").GetInt32());
    }

    [Fact]
    public async Task Execution_Snapshots_Effective_Policy_Version()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(fx.Owner, (fx.Admin, WorkspaceRole.Admin));
        using var owner = fx.Client(fx.Owner);

        var before = await ReadJsonAsync(await RequestToolAsync(owner, workspaceId, Echo, new { text = "v0" }, NewKey()));
        var rowBefore = await fx.FindExecutionAsync(before.GetProperty("id").GetGuid());
        Assert.Equal(0, rowBefore!.PolicyVersion);
        Assert.Equal(WorkspaceRole.Member, rowBefore.PolicyMinimumRequesterRole);
        Assert.False(rowBefore.PolicyRequiresApproval);
        Assert.Null(rowBefore.PolicyMinimumApproverRole);

        Assert.Equal(HttpStatusCode.OK, (await PutPolicyAsync(owner, workspaceId, Note,
            Policy(0, requester: "Admin", approver: "Owner", requiresApproval: true, maxArgumentLength: 300))).StatusCode);

        var executionId = await PendingNoteAsync(workspaceId, fx.Admin);
        var row = await fx.FindExecutionAsync(executionId);
        Assert.Equal(1, row!.PolicyVersion);
        Assert.Equal(WorkspaceRole.Admin, row.PolicyMinimumRequesterRole);
        Assert.Equal(WorkspaceRole.Owner, row.PolicyMinimumApproverRole);
        Assert.True(row.PolicyRequiresApproval);
        Assert.Equal(300, row.PolicyMaxArgumentLength);

        var view = await ReadJsonAsync(await owner.GetAsync($"/api/workspaces/{workspaceId}/tool-executions/{executionId}"));
        Assert.Equal(1, view.GetProperty("policyVersion").GetInt32());
    }

    // ---------- policy change while PendingApproval ----------

    [Fact]
    public async Task Disabling_Tool_While_Pending_Blocks_Approval_But_Allows_Reject()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(fx.Owner, (fx.Admin, WorkspaceRole.Admin));
        var executionId = await PendingNoteAsync(workspaceId, fx.Admin);
        using var owner = fx.Client(fx.Owner);

        Assert.Equal(HttpStatusCode.OK, (await PutPolicyAsync(owner, workspaceId, Note,
            Policy(0, enabled: false, requiresApproval: true))).StatusCode);

        var approve = await ApproveAsync(owner, workspaceId, executionId);
        Assert.Equal(HttpStatusCode.Conflict, approve.StatusCode);
        Assert.Equal("tool_disabled", await ReadErrorCodeAsync(approve));
        Assert.Equal(ToolExecutionStatus.PendingApproval, (await fx.FindExecutionAsync(executionId))!.Status);
        Assert.Equal(0, await fx.CountNotesAsync(executionId));

        Assert.Equal(HttpStatusCode.OK, (await RejectAsync(owner, workspaceId, executionId)).StatusCode);
        Assert.Equal(ToolExecutionStatus.Rejected, (await fx.FindExecutionAsync(executionId))!.Status);
    }

    [Fact]
    public async Task Raising_Approver_Role_While_Pending_Is_Enforced()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(
            fx.Owner, (fx.Admin, WorkspaceRole.Admin), (fx.Admin2, WorkspaceRole.Admin));
        var executionId = await PendingNoteAsync(workspaceId, fx.Admin);
        using var owner = fx.Client(fx.Owner);
        using var admin2 = fx.Client(fx.Admin2);

        Assert.Equal(HttpStatusCode.OK, (await PutPolicyAsync(owner, workspaceId, Note,
            Policy(0, approver: "Owner", requiresApproval: true))).StatusCode);

        var byAdmin = await ApproveAsync(admin2, workspaceId, executionId);
        Assert.Equal(HttpStatusCode.Forbidden, byAdmin.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await RejectAsync(admin2, workspaceId, executionId)).StatusCode);
        Assert.Equal(0, await fx.CountNotesAsync(executionId));

        var byOwner = await ReadJsonAsync(await ApproveAsync(owner, workspaceId, executionId));
        Assert.Equal("Succeeded", byOwner.GetProperty("status").GetString());
        Assert.Equal(1, await fx.CountNotesAsync(executionId));
    }

    [Fact]
    public async Task Raising_Requester_Role_While_Pending_Blocks_Execution()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(fx.Owner, (fx.Admin, WorkspaceRole.Admin));
        var executionId = await PendingNoteAsync(workspaceId, fx.Admin);
        using var owner = fx.Client(fx.Owner);

        Assert.Equal(HttpStatusCode.OK, (await PutPolicyAsync(owner, workspaceId, Note,
            Policy(0, requester: "Owner", requiresApproval: true))).StatusCode);

        var approve = await ApproveAsync(owner, workspaceId, executionId);
        Assert.Equal(HttpStatusCode.Conflict, approve.StatusCode);
        Assert.Equal("requester_no_longer_authorized", await ReadErrorCodeAsync(approve));
        Assert.Equal(0, await fx.CountNotesAsync(executionId));
    }

    [Fact]
    public async Task Loosening_Policy_While_Pending_Does_Not_Loosen_The_Pending_Execution()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(
            fx.Owner, (fx.Admin, WorkspaceRole.Admin), (fx.Admin2, WorkspaceRole.Admin));
        using var owner = fx.Client(fx.Owner);
        using var admin2 = fx.Client(fx.Admin2);

        Assert.Equal(HttpStatusCode.OK, (await PutPolicyAsync(owner, workspaceId, Note,
            Policy(0, approver: "Owner", requiresApproval: true))).StatusCode);
        var executionId = await PendingNoteAsync(workspaceId, fx.Admin);

        // Back to built-in defaults (approver Admin).
        Assert.Equal(HttpStatusCode.OK, (await PutPolicyAsync(owner, workspaceId, Note,
            Policy(1, requiresApproval: true))).StatusCode);

        var byAdmin = await ApproveAsync(admin2, workspaceId, executionId);
        Assert.Equal(HttpStatusCode.Forbidden, byAdmin.StatusCode);
        Assert.Equal(0, await fx.CountNotesAsync(executionId));

        // A new request under the loosened policy may be approved by an Admin.
        var fresh = await PendingNoteAsync(workspaceId, fx.Admin, "fresh");
        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(admin2, workspaceId, fresh)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(owner, workspaceId, executionId)).StatusCode);
    }

    [Fact]
    public async Task Lowering_Argument_Limit_While_Pending_Blocks_Approval()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(fx.Owner, (fx.Admin, WorkspaceRole.Admin));
        var executionId = await PendingNoteAsync(workspaceId, fx.Admin, new string('m', 100));
        using var owner = fx.Client(fx.Owner);

        Assert.Equal(HttpStatusCode.OK, (await PutPolicyAsync(owner, workspaceId, Note,
            Policy(0, requiresApproval: true, maxArgumentLength: 50))).StatusCode);

        var approve = await ApproveAsync(owner, workspaceId, executionId);
        Assert.Equal(HttpStatusCode.Conflict, approve.StatusCode);
        Assert.Equal("policy_limit_exceeded", await ReadErrorCodeAsync(approve));
        Assert.Equal(ToolExecutionStatus.PendingApproval, (await fx.FindExecutionAsync(executionId))!.Status);
    }

    [Fact]
    public async Task Owner_Only_Requester_Policy_Requires_A_Different_Owner_To_Approve()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(
            fx.Owner, (fx.Admin, WorkspaceRole.Admin), (fx.Admin2, WorkspaceRole.Admin));
        using var owner = fx.Client(fx.Owner);
        Assert.Equal(HttpStatusCode.OK, (await PutPolicyAsync(owner, workspaceId, Note,
            Policy(0, requester: "Owner", requiresApproval: true))).StatusCode);

        var executionId = await PendingNoteAsync(workspaceId, fx.Owner);

        using var admin = fx.Client(fx.Admin);
        Assert.Equal(HttpStatusCode.Forbidden, (await ApproveAsync(admin, workspaceId, executionId)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ApproveAsync(owner, workspaceId, executionId)).StatusCode);

        await fx.SetMembershipAsync(workspaceId, fx.Admin2.UserId, WorkspaceRole.Owner);
        using var secondOwner = fx.Client(fx.Admin2);
        var approved = await ReadJsonAsync(await ApproveAsync(secondOwner, workspaceId, executionId));
        Assert.Equal("Succeeded", approved.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Execution_Visibility_Follows_The_Stricter_Requester_Role()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(
            fx.Owner, (fx.Admin, WorkspaceRole.Admin), (fx.Member, WorkspaceRole.Member));
        using var admin = fx.Client(fx.Admin);
        using var member = fx.Client(fx.Member);
        using var owner = fx.Client(fx.Owner);

        var executionId = (await ReadJsonAsync(await RequestToolAsync(
            admin, workspaceId, Echo, new { text = "visible" }, NewKey()))).GetProperty("id").GetGuid();
        var path = $"/api/workspaces/{workspaceId}/tool-executions/{executionId}";
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync(path)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await PutPolicyAsync(owner, workspaceId, Echo,
            Policy(0, requester: "Admin"))).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(path)).StatusCode);
    }
}
