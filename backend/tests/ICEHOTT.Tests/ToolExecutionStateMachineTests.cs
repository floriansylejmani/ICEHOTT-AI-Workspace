using ICEHOTT.Domain.Tools;

namespace ICEHOTT.Tests;

/// <summary>
/// Exhaustive transition checks for the frozen Phase 4 state machine:
/// PendingApproval -> Ready | Rejected; Ready -> Running; Running -> Succeeded | Failed.
/// </summary>
public sealed class ToolExecutionStateMachineTests
{
    private static readonly Guid Requester = Guid.NewGuid();
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    public enum Transition { Approve, Reject, Start, Succeed, Fail }

    public static TheoryData<ToolExecutionStatus, Transition> IllegalTransitions()
    {
        var allowed = new HashSet<(ToolExecutionStatus, Transition)>
        {
            (ToolExecutionStatus.PendingApproval, Transition.Approve),
            (ToolExecutionStatus.PendingApproval, Transition.Reject),
            (ToolExecutionStatus.Ready, Transition.Start),
            (ToolExecutionStatus.Running, Transition.Succeed),
            (ToolExecutionStatus.Running, Transition.Fail),
        };

        var data = new TheoryData<ToolExecutionStatus, Transition>();
        foreach (var status in Enum.GetValues<ToolExecutionStatus>())
            foreach (var transition in Enum.GetValues<Transition>())
            {
                if (!allowed.Contains((status, transition)))
                    data.Add(status, transition);
            }

        return data;
    }

    [Theory]
    [MemberData(nameof(IllegalTransitions))]
    public void Illegal_Transitions_Throw_And_Leave_State_Unchanged(
        ToolExecutionStatus from,
        Transition transition)
    {
        var execution = InState(from);
        var before = Snapshot(execution);

        Assert.Throws<InvalidOperationException>(() => Apply(execution, transition));
        Assert.Equal(before, Snapshot(execution));
    }

    [Theory]
    [InlineData(ToolExecutionStatus.Succeeded)]
    [InlineData(ToolExecutionStatus.Failed)]
    [InlineData(ToolExecutionStatus.Rejected)]
    public void Terminal_States_Accept_No_Transition(ToolExecutionStatus terminal)
    {
        var execution = InState(terminal);

        foreach (var transition in Enum.GetValues<Transition>())
            Assert.Throws<InvalidOperationException>(() => Apply(execution, transition));

        Assert.Equal(terminal, execution.Status);
    }

    [Fact]
    public void Requester_Cannot_Approve_Or_Reject_Own_Execution_In_Domain()
    {
        var execution = New(requiresApproval: true);

        Assert.Throws<InvalidOperationException>(() => execution.Approve(Requester, Now));
        Assert.Throws<InvalidOperationException>(() => execution.Reject(Requester, Now));
        Assert.Throws<ArgumentException>(() => execution.Approve(Guid.Empty, Now));
        Assert.Equal(ToolExecutionStatus.PendingApproval, execution.Status);
        Assert.Null(execution.ApprovedByUserId);
    }

    [Fact]
    public void ReadOnly_Execution_Starts_Ready_And_Cannot_Be_Approved()
    {
        var execution = New(requiresApproval: false);

        Assert.Equal(ToolExecutionStatus.Ready, execution.Status);
        Assert.Throws<InvalidOperationException>(() => execution.Approve(Guid.NewGuid(), Now));
    }

    [Fact]
    public void Construction_Rejects_Missing_Identity()
    {
        Assert.Throws<ArgumentException>(() => new ToolExecution(
            Guid.NewGuid(), Guid.Empty, Requester, "t", ToolRiskLevel.ReadOnly,
            "{}", new string('a', 64), "key-12345", false, Now));
        Assert.Throws<ArgumentException>(() => new ToolExecution(
            Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, "t", ToolRiskLevel.ReadOnly,
            "{}", new string('a', 64), "key-12345", false, Now));
    }

    private static ToolExecution New(bool requiresApproval) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Requester,
            "workspace.test",
            requiresApproval ? ToolRiskLevel.SensitiveWrite : ToolRiskLevel.ReadOnly,
            "{}",
            new string('a', 64),
            "test-key-123",
            requiresApproval,
            Now);

    private static ToolExecution InState(ToolExecutionStatus status)
    {
        var approver = Guid.NewGuid();
        switch (status)
        {
            case ToolExecutionStatus.PendingApproval:
                return New(true);
            case ToolExecutionStatus.Ready:
                return New(false);
            case ToolExecutionStatus.Running:
                {
                    var e = New(false);
                    e.Start(Now);
                    return e;
                }
            case ToolExecutionStatus.Succeeded:
                {
                    var e = New(true);
                    e.Approve(approver, Now);
                    e.Start(Now);
                    e.Succeed("""{"ok":true}""", Now);
                    return e;
                }
            case ToolExecutionStatus.Failed:
                {
                    var e = New(false);
                    e.Start(Now);
                    e.Fail("tool_execution_failed", "Tool execution failed.", Now);
                    return e;
                }
            case ToolExecutionStatus.Rejected:
                {
                    var e = New(true);
                    e.Reject(approver, Now);
                    return e;
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(status), status, null);
        }
    }

    private static void Apply(ToolExecution execution, Transition transition)
    {
        switch (transition)
        {
            case Transition.Approve: execution.Approve(Guid.NewGuid(), Now); break;
            case Transition.Reject: execution.Reject(Guid.NewGuid(), Now); break;
            case Transition.Start: execution.Start(Now); break;
            case Transition.Succeed: execution.Succeed("""{"x":1}""", Now); break;
            case Transition.Fail: execution.Fail("code", "message", Now); break;
        }
    }

    private static object Snapshot(ToolExecution e) =>
        (e.Status, e.ApprovedByUserId, e.ApprovedAtUtc, e.StartedAtUtc, e.CompletedAtUtc,
            e.ResultJson, e.ErrorCode, e.ErrorMessage, e.ArgumentsJson, e.ArgumentsHash);
}
