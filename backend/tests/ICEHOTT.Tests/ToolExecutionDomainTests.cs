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
