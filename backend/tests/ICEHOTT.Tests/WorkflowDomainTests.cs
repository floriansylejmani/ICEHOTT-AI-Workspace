using ICEHOTT.Domain.Workflows;
using ICEHOTT.Domain.Workspaces;

namespace ICEHOTT.Tests;

public sealed class WorkflowDomainTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Definition_Is_Draft_And_Only_Draft_Can_Be_Edited()
    {
        var definition = NewDefinition();

        Assert.Equal(WorkflowDefinitionStatus.Draft, definition.Status);
        definition.UpdateDraft("Renamed", "desc", WorkspaceRole.Admin, T0.AddMinutes(1));
        Assert.Equal("Renamed", definition.Name);
        Assert.Equal(WorkspaceRole.Admin, definition.MinimumRunRole);

        definition.MarkActive(T0.AddMinutes(2));
        Assert.Equal(WorkflowDefinitionStatus.Active, definition.Status);
        Assert.Throws<InvalidOperationException>(() =>
            definition.UpdateDraft("No", null, WorkspaceRole.Member, T0.AddMinutes(3)));
    }

    [Fact]
    public void Version_Transitions_Draft_To_Active_To_Retired_Once()
    {
        var version = NewVersion();

        Assert.Equal(WorkflowVersionStatus.Draft, version.Status);
        version.Activate(T0.AddMinutes(1));
        Assert.Equal(WorkflowVersionStatus.Active, version.Status);
        Assert.NotNull(version.ActivatedAtUtc);

        version.Retire(T0.AddMinutes(2));
        Assert.Equal(WorkflowVersionStatus.Retired, version.Status);
        Assert.NotNull(version.RetiredAtUtc);

        Assert.Throws<InvalidOperationException>(() =>
            version.Activate(T0.AddMinutes(3)));
    }

    [Fact]
    public void Run_Waiting_Requires_Resume_Time_Except_For_Checkpoint()
    {
        var run = NewRun();
        run.Start("first", T0.AddMinutes(1));

        Assert.Throws<ArgumentException>(() =>
            run.Wait(WorkflowWaitReason.Delay));

        run.Wait(WorkflowWaitReason.Checkpoint);
        Assert.Equal(WorkflowRunStatus.Waiting, run.Status);
        Assert.Equal(WorkflowWaitReason.Checkpoint, run.WaitReason);
        Assert.Null(run.ResumeAtUtc);

        run.Resume();
        var resumeAt = T0.AddMinutes(10);
        run.Wait(WorkflowWaitReason.RetryBackoff, resumeAt);
        Assert.Equal(resumeAt, run.ResumeAtUtc);
    }

    [Fact]
    public void Run_Lease_Generation_Is_Monotonic_And_Terminal_Run_Cannot_Be_Leased()
    {
        var run = NewRun();

        var g1 = run.ClaimLease(Guid.NewGuid(), T0.AddMinutes(1));
        var g2 = run.ClaimLease(Guid.NewGuid(), T0.AddMinutes(2));

        Assert.Equal(1, g1);
        Assert.Equal(2, g2);

        run.Start(null, T0.AddSeconds(1));
        run.Succeed(T0.AddMinutes(3));

        Assert.Throws<InvalidOperationException>(() =>
            run.ClaimLease(Guid.NewGuid(), T0.AddMinutes(4)));
    }

    [Fact]
    public void OutcomeUnknown_Is_Terminal_And_Cannot_Be_Replayed_By_State_Machine()
    {
        var run = NewRun();
        run.Start("sensitive", T0.AddMinutes(1));
        run.MarkOutcomeUnknown(null, T0.AddMinutes(2));

        Assert.Equal(WorkflowRunStatus.OutcomeUnknown, run.Status);
        Assert.Equal("workflow_outcome_unknown", run.ErrorCode);
        Assert.Throws<InvalidOperationException>(() => run.Cancel(T0.AddMinutes(3)));
        Assert.Throws<InvalidOperationException>(() =>
            run.Fail("again", null, T0.AddMinutes(3)));
    }

    [Fact]
    public void Step_Attempts_Are_Positive_And_Terminal_Failure_Is_Not_Reapplied()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WorkflowStepRun(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                "step", 0, WorkflowStepType.Tool, "{}"));

        var step = NewStep();
        step.MarkReady();
        step.Start(T0.AddMinutes(1));
        step.Fail("failed", "safe", T0.AddMinutes(2));

        Assert.Equal(WorkflowStepRunStatus.Failed, step.Status);
        Assert.Throws<InvalidOperationException>(() =>
            step.Fail("again", null, T0.AddMinutes(3)));
    }

    [Fact]
    public void Checkpoint_Enforces_Separation_Of_Duty_And_First_Decision_Wins()
    {
        var requester = Guid.NewGuid();
        var checkpoint = new WorkflowCheckpoint(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            requester, WorkspaceRole.Admin, true, T0);

        Assert.Throws<InvalidOperationException>(() =>
            checkpoint.Approve(requester, null, T0.AddMinutes(1)));

        var approver = Guid.NewGuid();
        checkpoint.Approve(approver, "ok", T0.AddMinutes(2));

        Assert.Equal(WorkflowCheckpointStatus.Approved, checkpoint.Status);
        Assert.Equal(approver, checkpoint.DecidedByUserId);
        Assert.Throws<InvalidOperationException>(() =>
            checkpoint.Reject(Guid.NewGuid(), null, T0.AddMinutes(3)));
    }

    [Fact]
    public void Artifact_Step_Reference_Requires_Workflow_Run()
    {
        Assert.Throws<ArgumentException>(() =>
            new Artifact(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                "a.txt", "text/plain", 10, new string('a', 64),
                "staging/key", T0, workflowRunId: null, stepRunId: Guid.NewGuid()));

        var artifact = new Artifact(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "a.txt", "text/plain", 10, new string('a', 64),
            "staging/key", T0);

        Assert.Equal(ArtifactStatus.Pending, artifact.Status);
        artifact.MarkReady();
        Assert.Equal(ArtifactStatus.Ready, artifact.Status);
    }

    [Fact]
    public void Trigger_Is_Disabled_By_Default_And_Cannot_Fire_While_Disabled()
    {
        var trigger = NewTrigger();

        Assert.False(trigger.Enabled);
        Assert.Throws<InvalidOperationException>(() =>
            trigger.RecordFire(T0.AddHours(1), T0.AddHours(2)));

        trigger.Enable(T0.AddHours(1));
        trigger.RecordFire(T0.AddHours(1), T0.AddHours(2));

        Assert.True(trigger.Enabled);
        Assert.Equal(T0.AddHours(1), trigger.LastRunAtUtc);
        Assert.Equal(T0.AddHours(2), trigger.NextRunAtUtc);
    }

    [Fact]
    public void TriggerFire_Can_Create_Only_One_Run()
    {
        var fire = new WorkflowTriggerFire(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            T0.AddHours(1), "fire-key", T0);

        var runId = Guid.NewGuid();
        fire.MarkRunCreated(runId, T0.AddHours(1));

        Assert.Equal(WorkflowTriggerFireStatus.RunCreated, fire.Status);
        Assert.Equal(runId, fire.WorkflowRunId);
        Assert.Throws<InvalidOperationException>(() =>
            fire.MarkRunCreated(Guid.NewGuid(), T0.AddHours(2)));
    }

    private static WorkflowDefinition NewDefinition() =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Workflow",
            null,
            WorkspaceRole.Member,
            Guid.NewGuid(),
            T0);

    private static WorkflowVersion NewVersion() =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            "{\"steps\":[]}",
            new string('b', 64),
            Guid.NewGuid(),
            T0);

    private static WorkflowRun NewRun() =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "idem-key",
            T0);

    private static WorkflowStepRun NewStep() =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "step-1",
            1,
            WorkflowStepType.Tool,
            "{}");

    private static WorkflowTrigger NewTrigger() =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "0 * * * *",
            "UTC",
            Guid.NewGuid(),
            Guid.NewGuid(),
            T0.AddHours(1),
            T0);
}
