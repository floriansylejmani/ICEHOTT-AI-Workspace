using ICEHOTT.Application.Abstractions;
using ICEHOTT.Application.Tools;
using ICEHOTT.Domain.Tools;
using ICEHOTT.Domain.Workspaces;

namespace ICEHOTT.Tests;

public sealed class ToolExecutionDomainTests
{
    [Fact]
    public void Sensitive_Execution_Requires_Different_Approver()
    {
        var requester = Guid.NewGuid();
        var execution = NewExecution(
            requester,
            requiresApproval: true);

        Assert.Equal(
            ToolExecutionStatus.PendingApproval,
            execution.Status);

        Assert.Throws<InvalidOperationException>(
            () => execution.Approve(
                requester,
                DateTimeOffset.UtcNow));

        var approver = Guid.NewGuid();
        execution.Approve(
            approver,
            DateTimeOffset.UtcNow);

        Assert.Equal(
            ToolExecutionStatus.Ready,
            execution.Status);
        Assert.Equal(
            approver,
            execution.ApprovedByUserId);
    }

    [Fact]
    public void Execution_State_Machine_Is_Terminal_After_Success()
    {
        var execution = NewExecution(
            Guid.NewGuid(),
            requiresApproval: false);

        execution.Start(DateTimeOffset.UtcNow);
        execution.Succeed(
            """{"ok":true}""",
            DateTimeOffset.UtcNow);

        Assert.Equal(
            ToolExecutionStatus.Succeeded,
            execution.Status);

        Assert.Throws<InvalidOperationException>(
            () => execution.Start(
                DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Rejected_Execution_Cannot_Be_Approved()
    {
        var execution = NewExecution(
            Guid.NewGuid(),
            requiresApproval: true);

        execution.Reject(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow);

        Assert.Equal(
            ToolExecutionStatus.Rejected,
            execution.Status);

        Assert.Throws<InvalidOperationException>(
            () => execution.Approve(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cancellation_Before_Start_Is_Terminal(bool requiresApproval)
    {
        var execution = NewExecution(Guid.NewGuid(), requiresApproval);
        var now = DateTimeOffset.UtcNow;

        execution.Cancel(now);

        Assert.Equal(ToolExecutionStatus.Cancelled, execution.Status);
        Assert.Equal(now, execution.CompletedAtUtc);
        Assert.Null(execution.StartedAtUtc);
        Assert.Throws<InvalidOperationException>(() => execution.Start(now));
        Assert.Throws<InvalidOperationException>(() => execution.Cancel(now));
    }

    [Fact]
    public void Running_Timeout_Has_Safe_Code_And_Cannot_Succeed()
    {
        var execution = NewExecution(Guid.NewGuid(), false);
        var now = DateTimeOffset.UtcNow;
        execution.Start(now);
        execution.TimeOut(now.AddSeconds(1));

        Assert.Equal(ToolExecutionStatus.TimedOut, execution.Status);
        Assert.Equal("tool_timeout", execution.ErrorCode);
        Assert.Equal(now.AddSeconds(1), execution.CompletedAtUtc);
        Assert.Throws<InvalidOperationException>(() => execution.Succeed("{}", now));
    }

    [Fact]
    public void Expired_Running_Execution_Has_Unknown_Outcome_Without_Replay()
    {
        var execution = NewExecution(Guid.NewGuid(), true);
        var now = DateTimeOffset.UtcNow;
        execution.Approve(Guid.NewGuid(), now);
        execution.Start(now);
        execution.MarkOutcomeUnknown(now.AddMinutes(1));

        Assert.Equal(ToolExecutionStatus.OutcomeUnknown, execution.Status);
        Assert.Equal("tool_outcome_unknown", execution.ErrorCode);
        Assert.Equal(now.AddMinutes(1), execution.CompletedAtUtc);
        Assert.Throws<InvalidOperationException>(() => execution.Start(now));
        Assert.Throws<InvalidOperationException>(() => execution.MarkOutcomeUnknown(now));
    }

    [Fact]
    public void Running_Cannot_Be_Cancelled_As_If_No_Side_Effect_Occurred()
    {
        var execution = NewExecution(Guid.NewGuid(), false);
        execution.Start(DateTimeOffset.UtcNow);

        Assert.Throws<InvalidOperationException>(
            () => execution.Cancel(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Running_Claim_Records_Owner_Deadline_And_Lease()
    {
        var execution = NewExecution(Guid.NewGuid(), false);
        var now = DateTimeOffset.UtcNow;
        var owner = Guid.NewGuid();

        execution.Start(now, owner, now.AddSeconds(30), now.AddSeconds(45));

        Assert.Equal(owner, execution.LeaseOwnerId);
        Assert.Equal(now.AddSeconds(30), execution.DeadlineAtUtc);
        Assert.Equal(now.AddSeconds(45), execution.LeaseExpiresAtUtc);
    }

    [Fact]
    public void Running_Claim_Rejects_Invalid_Lease_Without_State_Change()
    {
        var execution = NewExecution(Guid.NewGuid(), false);
        var now = DateTimeOffset.UtcNow;

        Assert.Throws<ArgumentException>(() =>
            execution.Start(now, Guid.Empty, now.AddSeconds(30), now.AddSeconds(45)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            execution.Start(now, Guid.NewGuid(), now, now.AddSeconds(45)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            execution.Start(now, Guid.NewGuid(), now.AddSeconds(30), now.AddSeconds(20)));

        Assert.Equal(ToolExecutionStatus.Ready, execution.Status);
        Assert.Null(execution.LeaseOwnerId);
    }

    [Fact]
    public void Registry_Rejects_Duplicate_Tool_Names()
    {
        var first = new FakeTool("workspace.same");
        var second = new FakeTool("WORKSPACE.SAME");

        Assert.Throws<InvalidOperationException>(
            () => new ToolRegistry([first, second]));
    }

    private static ToolExecution NewExecution(
        Guid requester,
        bool requiresApproval) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            requester,
            "workspace.test",
            requiresApproval
                ? ToolRiskLevel.SensitiveWrite
                : ToolRiskLevel.ReadOnly,
            "{}",
            new string('a', 64),
            "test-key-123",
            requiresApproval,
            DateTimeOffset.UtcNow);

    private sealed class FakeTool(string name)
        : IWorkspaceTool
    {
        public ToolDefinition Definition { get; } = new(
            name,
            "test",
            ToolRiskLevel.ReadOnly,
            WorkspaceRole.Member,
            false,
            null,
            []);

        public ToolArgumentValidationResult ValidateArguments(
            System.Text.Json.JsonElement arguments) =>
            ToolArgumentValidationResult.Valid;

        public Task<ToolExecutionOutput> ExecuteAsync(
            ToolExecutionContext context,
            System.Text.Json.JsonElement arguments,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                new ToolExecutionOutput("{}"));
    }
}
