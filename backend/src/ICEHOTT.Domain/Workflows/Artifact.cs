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
        Guid? stepRunId = null)
    {
        if (id == Guid.Empty) throw new ArgumentException("Artifact ID is required.", nameof(id));
        if (workspaceId == Guid.Empty) throw new ArgumentException("Workspace ID is required.", nameof(workspaceId));
        if (createdByUserId == Guid.Empty) throw new ArgumentException("Creator ID is required.", nameof(createdByUserId));
        if (string.IsNullOrWhiteSpace(fileName)) throw new ArgumentException("File name is required.", nameof(fileName));
        if (string.IsNullOrWhiteSpace(contentType)) throw new ArgumentException("Content type is required.", nameof(contentType));
        if (sizeBytes < 0) throw new ArgumentOutOfRangeException(nameof(sizeBytes));
        if (string.IsNullOrWhiteSpace(sha256)) throw new ArgumentException("SHA-256 is required.", nameof(sha256));
        if (string.IsNullOrWhiteSpace(storageKey)) throw new ArgumentException("Storage key is required.", nameof(storageKey));
        if (stepRunId.HasValue && !workflowRunId.HasValue)
            throw new ArgumentException("A step-bound artifact must also reference its workflow run.", nameof(stepRunId));

        Id = id;
        WorkspaceId = workspaceId;
        CreatedByUserId = createdByUserId;
        WorkflowRunId = workflowRunId;
        StepRunId = stepRunId;
        FileName = fileName.Trim();
        ContentType = contentType.Trim();
        SizeBytes = sizeBytes;
        Sha256 = sha256.Trim();
        StorageKey = storageKey.Trim();
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
    public ArtifactStatus Status { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? DeletedAtUtc { get; private set; }

    public void MarkReady()
    {
        if (Status != ArtifactStatus.Pending)
            throw new InvalidOperationException("Only pending artifacts can become ready.");
        Status = ArtifactStatus.Ready;
    }

    public void MarkFailed()
    {
        if (Status != ArtifactStatus.Pending)
            throw new InvalidOperationException("Only pending artifacts can fail.");
        Status = ArtifactStatus.Failed;
    }

    public void MarkDeleted(DateTimeOffset deletedAtUtc)
    {
        if (Status == ArtifactStatus.Deleted)
            return;

        Status = ArtifactStatus.Deleted;
        DeletedAtUtc = deletedAtUtc;
    }
}
