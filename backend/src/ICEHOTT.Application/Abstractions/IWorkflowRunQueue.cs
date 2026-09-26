namespace ICEHOTT.Application.Abstractions;

public enum WorkflowRunPersistenceOutcome
{
    Saved = 1,
    LeaseLost = 2,
    ConcurrencyConflict = 3
}

public sealed record WorkflowRunLease(
    Guid RunId,
    Guid WorkerId,
    Guid WorkspaceId,
    Guid WorkflowDefinitionId,
    Guid WorkflowVersionId,
    Guid RunAsUserId,
    string? CurrentStepKey,
    int LeaseGeneration,
    DateTimeOffset LeaseExpiresAtUtc);

public interface IWorkflowRunQueue
{
    Task<WorkflowRunLease?> LeaseNextAsync(
        Guid workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    Task<bool> RenewLeaseAsync(
        Guid runId,
        Guid workerId,
        int leaseGeneration,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    Task<bool> RequestCancellationAsync(
        Guid workspaceId,
        Guid runId,
        Guid actorUserId,
        DateTimeOffset requestedAtUtc,
        CancellationToken cancellationToken = default);

    Task<bool> IsCancellationRequestedAsync(
        Guid runId,
        Guid workerId,
        int leaseGeneration,
        CancellationToken cancellationToken = default);

    Task<WorkflowRunPersistenceOutcome> SaveFencedAsync(
        Guid runId,
        Guid workerId,
        int leaseGeneration,
        CancellationToken cancellationToken = default);
}
