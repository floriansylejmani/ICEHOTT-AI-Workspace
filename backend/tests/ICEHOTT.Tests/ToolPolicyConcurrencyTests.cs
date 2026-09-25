using System.Text.Json;
using ICEHOTT.Application.Abstractions;
using ICEHOTT.Application.Tools;
using ICEHOTT.Domain.Tools;
using ICEHOTT.Domain.Workspaces;
using ICEHOTT.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static ICEHOTT.Tests.ToolSecurityFixture;

namespace ICEHOTT.Tests;

/// <summary>
/// Phase 4.5 Work Packet B: races between policy updates, approvals and
/// immediate executions (decisions D3 and D6). Interleavings are forced
/// deterministically with separate DbContext scopes / a lookup-hiding decorator.
/// </summary>
public sealed class ToolPolicyConcurrencyTests(ToolSecurityFixture fx)
    : IClassFixture<ToolSecurityFixture>
{
    private const string Echo = "workspace.echo";
    private const string Note = "workspace.audit-note.create";

    private static JsonElement Body(object value) => JsonSerializer.SerializeToElement(value);

    private static JsonElement PolicyBody(int expectedVersion, bool enabled, bool requiresApproval, string? approver = null) =>
        Body(new
        {
            expectedVersion,
            enabled,
            minimumRequesterRole = (string?)null,
            minimumApproverRole = approver,
            requiresApproval,
            maxArgumentLength = (int?)null
        });

    private async Task<int> UpdatePolicyAsync(Guid workspaceId, string tool, JsonElement body)
    {
        using var scope = fx.Factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<ToolPolicyService>()
            .UpdateAsync(fx.Owner.UserId, workspaceId, tool, body);
        Assert.Null(result.ErrorCode);
        return result.Value!.Version;
    }

    private async Task<Guid> PendingNoteAsync(Guid workspaceId)
    {
        using var scope = fx.Factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<ToolExecutionService>()
            .RequestAsync(fx.Admin.UserId, workspaceId, Note, Body(new { message = "race" }), NewKey("race"));
        Assert.Null(result.ErrorCode);
        Assert.Equal(ToolExecutionStatus.PendingApproval, result.Value!.Status);
        return result.Value.Id;
    }

    [Fact]
    public async Task Concurrent_Policy_Updates_With_Same_Expected_Version_Cannot_Both_Commit()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(fx.Owner);
        Assert.Equal(1, await UpdatePolicyAsync(workspaceId, Echo, PolicyBody(0, true, false)));

        // Racer B reads version 1...
        using var scopeB = fx.Factory.Services.CreateScope();
        var dbB = scopeB.ServiceProvider.GetRequiredService<ICEHOTTDbContext>();
        var staleB = await dbB.ToolPolicies.SingleAsync(x => x.WorkspaceId == workspaceId && x.ToolName == Echo);
        Assert.Equal(1, staleB.Version);

        // ...racer A commits version 2...
        Assert.Equal(2, await UpdatePolicyAsync(workspaceId, Echo, PolicyBody(1, false, false)));

        // ...B's update, still believing version 1 is current, must not commit.
        var loser = await scopeB.ServiceProvider.GetRequiredService<ToolPolicyService>()
            .UpdateAsync(fx.Owner.UserId, workspaceId, Echo, PolicyBody(1, true, true));
        Assert.Equal("policy_version_conflict", loser.ErrorCode);

        using var verify = fx.Factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<ICEHOTTDbContext>();
        var row = await db.ToolPolicies.AsNoTracking().SingleAsync(x => x.WorkspaceId == workspaceId && x.ToolName == Echo);
        Assert.Equal(2, row.Version);
        Assert.False(row.Enabled);
        Assert.False(row.RequiresApproval);
        Assert.Equal(2, await db.ToolPolicyAuditEvents.CountAsync(x => x.WorkspaceId == workspaceId && x.ToolName == Echo));
    }

    [Fact]
    public async Task Concurrent_First_Policy_Creations_Cannot_Both_Commit()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(fx.Owner);

        using var scopeB = fx.Factory.Services.CreateScope();
        var serviceB = new ToolPolicyService(
            scopeB.ServiceProvider.GetRequiredService<IWorkspaceRepository>(),
            new HideFirstPolicyLookup(scopeB.ServiceProvider.GetRequiredService<IToolPolicyRepository>()),
            scopeB.ServiceProvider.GetRequiredService<IToolRegistry>(),
            TimeProvider.System);

        Assert.Equal(1, await UpdatePolicyAsync(workspaceId, Echo, PolicyBody(0, false, false)));

        var loser = await serviceB.UpdateAsync(fx.Owner.UserId, workspaceId, Echo, PolicyBody(0, true, true));
        Assert.Equal("policy_version_conflict", loser.ErrorCode);

        using var verify = fx.Factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<ICEHOTTDbContext>();
        var row = await db.ToolPolicies.AsNoTracking().SingleAsync(x => x.WorkspaceId == workspaceId && x.ToolName == Echo);
        Assert.Equal(1, row.Version);
        Assert.False(row.Enabled);
    }

    [Fact]
    public async Task Approval_Racing_A_Policy_Change_Fails_Closed_Without_Side_Effects()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(fx.Owner, (fx.Admin, WorkspaceRole.Admin));
        Assert.Equal(1, await UpdatePolicyAsync(workspaceId, Note, PolicyBody(0, true, true)));
        var executionId = await PendingNoteAsync(workspaceId);

        // Approver B evaluates the policy at version 1 (enabled)...
        using var scopeB = fx.Factory.Services.CreateScope();
        var dbB = scopeB.ServiceProvider.GetRequiredService<ICEHOTTDbContext>();
        _ = await dbB.ToolPolicies.SingleAsync(x => x.WorkspaceId == workspaceId && x.ToolName == Note);

        // ...the Owner disables the tool (version 2) before B commits...
        Assert.Equal(2, await UpdatePolicyAsync(workspaceId, Note, PolicyBody(1, false, true)));

        // ...B's approval must not commit under the obsolete version.
        var result = await scopeB.ServiceProvider.GetRequiredService<ToolExecutionService>()
            .ApproveAsync(fx.Owner.UserId, workspaceId, executionId);
        Assert.Equal("policy_changed", result.ErrorCode);

        var row = await fx.FindExecutionAsync(executionId);
        Assert.Equal(ToolExecutionStatus.PendingApproval, row!.Status);
        Assert.Null(row.ApprovedByUserId);
        Assert.Equal(0, await fx.CountNotesAsync(executionId));
        Assert.DoesNotContain(ToolExecutionAuditEventType.Approved, await fx.AuditTrailAsync(executionId));

        // A fresh attempt re-evaluates and sees the disabled tool.
        using var retry = fx.Factory.Services.CreateScope();
        var again = await retry.ServiceProvider.GetRequiredService<ToolExecutionService>()
            .ApproveAsync(fx.Owner.UserId, workspaceId, executionId);
        Assert.Equal("tool_disabled", again.ErrorCode);
    }

    [Fact]
    public async Task Approval_Racing_The_First_Policy_Creation_Fails_Closed()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(fx.Owner, (fx.Admin, WorkspaceRole.Admin));
        var executionId = await PendingNoteAsync(workspaceId);

        // B evaluates "no overlay" (built-in defaults)...
        using var scopeB = fx.Factory.Services.CreateScope();
        var serviceB = new ToolExecutionService(
            scopeB.ServiceProvider.GetRequiredService<IWorkspaceRepository>(),
            scopeB.ServiceProvider.GetRequiredService<IToolExecutionRepository>(),
            new HideFirstPolicyLookup(scopeB.ServiceProvider.GetRequiredService<IToolPolicyRepository>()),
            scopeB.ServiceProvider.GetRequiredService<IToolRegistry>(),
            TimeProvider.System);

        // ...while the Owner creates the first policy, disabling the tool.
        Assert.Equal(1, await UpdatePolicyAsync(workspaceId, Note, PolicyBody(0, false, true)));

        var result = await serviceB.ApproveAsync(fx.Owner.UserId, workspaceId, executionId);
        Assert.Equal("policy_changed", result.ErrorCode);
        Assert.Equal(ToolExecutionStatus.PendingApproval, (await fx.FindExecutionAsync(executionId))!.Status);
        Assert.Equal(0, await fx.CountNotesAsync(executionId));
    }

    [Fact]
    public async Task Immediate_Execution_Racing_A_Disable_Fails_Closed_Without_Running_Handler()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(fx.Owner);
        Assert.Equal(1, await UpdatePolicyAsync(workspaceId, Echo, PolicyBody(0, true, false)));

        using var scopeB = fx.Factory.Services.CreateScope();
        var dbB = scopeB.ServiceProvider.GetRequiredService<ICEHOTTDbContext>();
        _ = await dbB.ToolPolicies.SingleAsync(x => x.WorkspaceId == workspaceId && x.ToolName == Echo);

        Assert.Equal(2, await UpdatePolicyAsync(workspaceId, Echo, PolicyBody(1, false, false)));

        var result = await scopeB.ServiceProvider.GetRequiredService<ToolExecutionService>()
            .RequestAsync(fx.Owner.UserId, workspaceId, Echo, Body(new { text = "late" }), NewKey("late"));
        Assert.Equal("policy_changed", result.ErrorCode);
        Assert.Equal(0, await fx.CountExecutionsAsync(workspaceId));
    }

    [Fact]
    public async Task Approvals_Of_Different_Executions_Do_Not_Conflict_With_Each_Other()
    {
        var workspaceId = await fx.CreateWorkspaceAsync(fx.Owner, (fx.Admin, WorkspaceRole.Admin));
        Assert.Equal(1, await UpdatePolicyAsync(workspaceId, Note, PolicyBody(0, true, true)));
        var first = await PendingNoteAsync(workspaceId);
        var second = await PendingNoteAsync(workspaceId);

        using var scopeA = fx.Factory.Services.CreateScope();
        using var scopeB = fx.Factory.Services.CreateScope();
        _ = await scopeB.ServiceProvider.GetRequiredService<ICEHOTTDbContext>().ToolPolicies
            .SingleAsync(x => x.WorkspaceId == workspaceId && x.ToolName == Note);

        var a = await scopeA.ServiceProvider.GetRequiredService<ToolExecutionService>()
            .ApproveAsync(fx.Owner.UserId, workspaceId, first);
        var b = await scopeB.ServiceProvider.GetRequiredService<ToolExecutionService>()
            .ApproveAsync(fx.Owner.UserId, workspaceId, second);

        Assert.Null(a.ErrorCode);
        Assert.Null(b.ErrorCode);
        Assert.Equal(1, await fx.CountNotesAsync(first));
        Assert.Equal(1, await fx.CountNotesAsync(second));
    }

    /// <summary>Returns "no overlay" for the first lookup only.</summary>
    private sealed class HideFirstPolicyLookup(IToolPolicyRepository inner) : IToolPolicyRepository
    {
        private bool _hidden;

        public Task<ToolPolicy?> FindAsync(Guid workspaceId, string toolName, CancellationToken cancellationToken = default)
        {
            if (!_hidden)
            {
                _hidden = true;
                return Task.FromResult<ToolPolicy?>(null);
            }

            return inner.FindAsync(workspaceId, toolName, cancellationToken);
        }

        public Task<IReadOnlyList<ToolPolicy>> ListAsync(Guid workspaceId, CancellationToken cancellationToken = default) =>
            inner.ListAsync(workspaceId, cancellationToken);

        public Task<IReadOnlyList<ToolPolicyAuditEvent>> ListAuditEventsAsync(Guid workspaceId, string toolName, CancellationToken cancellationToken = default) =>
            inner.ListAuditEventsAsync(workspaceId, toolName, cancellationToken);

        public Task AddAsync(ToolPolicy policy, CancellationToken cancellationToken = default) =>
            inner.AddAsync(policy, cancellationToken);

        public Task AddAuditEventAsync(ToolPolicyAuditEvent auditEvent, CancellationToken cancellationToken = default) =>
            inner.AddAuditEventAsync(auditEvent, cancellationToken);

        public Task GuardUnchangedAsync(ToolPolicy? observed, Guid workspaceId, string toolName, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            inner.GuardUnchangedAsync(observed, workspaceId, toolName, now, cancellationToken);

        public Task<ToolPersistenceOutcome> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            inner.SaveChangesAsync(cancellationToken);
    }
}
