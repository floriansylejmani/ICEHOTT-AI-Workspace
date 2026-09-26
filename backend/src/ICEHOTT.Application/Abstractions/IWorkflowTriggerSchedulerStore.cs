namespace ICEHOTT.Application.Abstractions;

public enum WorkflowTriggerSchedulerDisposition
{
    NoWork = 0,
    Claimed = 1,
    RunCreated = 2,
    Skipped = 3,
    Failed = 4
}

public sealed record WorkflowTriggerSchedulerResult(
    WorkflowTriggerSchedulerDisposition Disposition,
    Guid? TriggerId = null,
    Guid? FireId = null,
    Guid? WorkflowRunId = null,
    string? Reason = null)
{
    public bool DidWork =>
        Disposition != WorkflowTriggerSchedulerDisposition.NoWork;
}

public interface IWorkflowTriggerSchedulerStore
{
    Task<WorkflowTriggerSchedulerResult> ClaimNextDueAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<WorkflowTriggerSchedulerResult> ProcessNextClaimedAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}
