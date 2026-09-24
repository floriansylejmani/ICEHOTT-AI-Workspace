namespace ICEHOTT.Domain.Knowledge;

public sealed class KnowledgeProcessingJob
{
    private KnowledgeProcessingJob() { }

    public KnowledgeProcessingJob(
        Guid id,
        Guid documentId,
        Guid workspaceId,
        DateTimeOffset createdAtUtc,
        int maxAttempts = 5)
    {
        Id = id;
        DocumentId = documentId;
        WorkspaceId = workspaceId;
        Status = KnowledgeProcessingStatus.Queued;
        Attempts = 0;
        MaxAttempts = Math.Clamp(maxAttempts, 1, 10);
        AvailableAtUtc = createdAtUtc;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }
    public Guid DocumentId { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public KnowledgeProcessingStatus Status { get; private set; }
    public int Attempts { get; private set; }
    public int MaxAttempts { get; private set; }
    public DateTimeOffset AvailableAtUtc { get; private set; }
    public string? LockedBy { get; private set; }
    public DateTimeOffset? LockedUntilUtc { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public void Reset(DateTimeOffset now)
    {
        Status = KnowledgeProcessingStatus.Queued;
        Attempts = 0;
        AvailableAtUtc = now;
        LockedBy = null;
        LockedUntilUtc = null;
        LastError = null;
        CompletedAtUtc = null;
        UpdatedAtUtc = now;
    }

    public void Lease(string workerId, DateTimeOffset now, TimeSpan leaseDuration)
    {
        if (string.IsNullOrWhiteSpace(workerId)) throw new ArgumentException("Worker ID is required.", nameof(workerId));
        Status = KnowledgeProcessingStatus.Processing;
        Attempts++;
        LockedBy = workerId;
        LockedUntilUtc = now.Add(leaseDuration);
        LastError = null;
        UpdatedAtUtc = now;
    }

    public void RenewLease(string workerId, DateTimeOffset now, TimeSpan leaseDuration)
    {
        if (Status != KnowledgeProcessingStatus.Processing ||
            !string.Equals(LockedBy, workerId, StringComparison.Ordinal))
            throw new InvalidOperationException("Only the active worker can renew this lease.");

        LockedUntilUtc = now.Add(leaseDuration);
        UpdatedAtUtc = now;
    }

    public void Complete(DateTimeOffset now)
    {
        Status = KnowledgeProcessingStatus.Completed;
        LockedBy = null;
        LockedUntilUtc = null;
        LastError = null;
        CompletedAtUtc = now;
        UpdatedAtUtc = now;
    }

    public void Retry(string error, DateTimeOffset availableAtUtc, DateTimeOffset now)
    {
        Status = KnowledgeProcessingStatus.Queued;
        LockedBy = null;
        LockedUntilUtc = null;
        LastError = NormalizeError(error);
        AvailableAtUtc = availableAtUtc;
        UpdatedAtUtc = now;
    }

    public void Fail(string error, DateTimeOffset now)
    {
        Status = KnowledgeProcessingStatus.Failed;
        LockedBy = null;
        LockedUntilUtc = null;
        LastError = NormalizeError(error);
        CompletedAtUtc = now;
        UpdatedAtUtc = now;
    }

    private static string NormalizeError(string error)
    {
        var value = string.IsNullOrWhiteSpace(error) ? "processing_failed" : error.Trim();
        return value.Length <= 2000 ? value : value[..2000];
    }
}
