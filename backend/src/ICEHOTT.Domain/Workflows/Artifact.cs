namespace ICEHOTT.Domain.Workflows;

public sealed class Artifact
{
    private Artifact() { }

    public Artifact(
        Guid id,
        Guid workspaceId,
        Guid createdByUserId,
        string fileName,
        string contentType,
        long sizeBytes,
        string sha256,
        string storageKey,
        DateTimeOffset createdAtUtc,
        Guid? workflowRunId = null,
        Guid? stepRunId = null,
        string? stagingKey = null,
        string? idempotencyKey = null)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Artifact ID is required.", nameof(id));
        if (workspaceId == Guid.Empty)
            throw new ArgumentException("Workspace ID is required.", nameof(workspaceId));
        if (createdByUserId == Guid.Empty)
            throw new ArgumentException("Creator ID is required.", nameof(createdByUserId));
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("File name is required.", nameof(fileName));
        if (fileName.Trim().Length > 260)
            throw new ArgumentOutOfRangeException(nameof(fileName));
        if (string.IsNullOrWhiteSpace(contentType))
            throw new ArgumentException("Content type is required.", nameof(contentType));
        if (contentType.Trim().Length > 160)
            throw new ArgumentOutOfRangeException(nameof(contentType));
        if (sizeBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(sizeBytes));
        if (string.IsNullOrWhiteSpace(sha256))
            throw new ArgumentException("SHA-256 is required.", nameof(sha256));
        var normalizedSha = sha256.Trim().ToLowerInvariant();
        if (normalizedSha.Length != 64 ||
            normalizedSha.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("SHA-256 must be 64 hexadecimal characters.", nameof(sha256));
        if (string.IsNullOrWhiteSpace(storageKey))
            throw new ArgumentException("Storage key is required.", nameof(storageKey));
        if (storageKey.Trim().Length > 500)
            throw new ArgumentOutOfRangeException(nameof(storageKey));
        if (stagingKey?.Trim().Length > 500)
            throw new ArgumentOutOfRangeException(nameof(stagingKey));
        if (idempotencyKey?.Trim().Length > 160)
            throw new ArgumentOutOfRangeException(nameof(idempotencyKey));
        if (stepRunId.HasValue && !workflowRunId.HasValue)
            throw new ArgumentException(
                "A step-bound artifact must also reference its workflow run.",
                nameof(stepRunId));

        Id = id;
        WorkspaceId = workspaceId;
        CreatedByUserId = createdByUserId;
        WorkflowRunId = workflowRunId;
        StepRunId = stepRunId;
        FileName = fileName.Trim();
        ContentType = contentType.Trim();
        SizeBytes = sizeBytes;
        Sha256 = normalizedSha;
        StorageKey = storageKey.Trim();
        StagingKey = NormalizeOptional(stagingKey);
        IdempotencyKey = NormalizeOptional(idempotencyKey);
        CreatedAtUtc = createdAtUtc;
        Status = ArtifactStatus.Pending;
    }

    public Guid Id { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public Guid CreatedByUserId { get; private set; }
    public Guid? WorkflowRunId { get; private set; }
    public Guid? StepRunId { get; private set; }
    public string FileName { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = string.Empty;
    public long SizeBytes { get; private set; }
    public string Sha256 { get; private set; } = string.Empty;
    public string StorageKey { get; private set; } = string.Empty;
    public string? StagingKey { get; private set; }
    public string? IdempotencyKey { get; private set; }
    public ArtifactStatus Status { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? FailedAtUtc { get; private set; }
    public DateTimeOffset? DeletedAtUtc { get; private set; }
    public DateTimeOffset? StorageDeletedAtUtc { get; private set; }

    public void MarkReady()
    {
        if (Status != ArtifactStatus.Pending)
            throw new InvalidOperationException(
                "Only pending artifacts can become ready.");

        Status = ArtifactStatus.Ready;
        StagingKey = null;
        FailedAtUtc = null;
    }

    public void MarkFailed(DateTimeOffset failedAtUtc)
    {
        if (Status != ArtifactStatus.Pending)
            throw new InvalidOperationException(
                "Only pending artifacts can fail.");

        Status = ArtifactStatus.Failed;
        FailedAtUtc = failedAtUtc;
    }

    public void MarkStagingCleaned()
    {
        StagingKey = null;
    }

    public void MarkDeleted(DateTimeOffset deletedAtUtc)
    {
        if (Status == ArtifactStatus.Deleted)
            return;

        Status = ArtifactStatus.Deleted;
        DeletedAtUtc = deletedAtUtc;
    }

    public void MarkStorageDeleted(DateTimeOffset deletedAtUtc)
    {
        if (Status != ArtifactStatus.Deleted)
            throw new InvalidOperationException(
                "Only logically deleted artifacts can mark storage deleted.");

        StorageDeletedAtUtc = deletedAtUtc;
        StagingKey = null;
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
