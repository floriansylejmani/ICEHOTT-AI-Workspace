namespace ICEHOTT.Application.Abstractions;

public sealed record KnowledgeJobLease(
    Guid Id,
    Guid DocumentId,
    Guid WorkspaceId,
    int Attempts,
    int MaxAttempts,
    string WorkerId);

public interface IKnowledgeJobQueue
{
    Task EnqueueAsync(
        Guid documentId,
        Guid workspaceId,
        CancellationToken cancellationToken = default);

    Task<KnowledgeJobLease?> LeaseNextAsync(
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    Task<bool> RenewLeaseAsync(
        Guid jobId,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    Task<bool> CompleteAsync(
        Guid jobId,
        string workerId,
        CancellationToken cancellationToken = default);

    Task<bool> RetryAsync(
        Guid jobId,
        string workerId,
        string error,
        DateTimeOffset availableAtUtc,
        CancellationToken cancellationToken = default);

    Task<bool> FailAsync(
        Guid jobId,
        string workerId,
        string error,
        CancellationToken cancellationToken = default);
}
