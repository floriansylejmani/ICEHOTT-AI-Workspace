namespace ICEHOTT.Application.Abstractions;

public sealed record ArtifactStageResult(
    string StagingKey,
    string StorageKey,
    long SizeBytes,
    string Sha256);

public sealed record ArtifactStoredObjectInfo(
    long SizeBytes,
    string Sha256);

public interface IArtifactStore
{
    Task<ArtifactStageResult> StageAsync(
        Guid workspaceId,
        Guid artifactId,
        Stream source,
        long maxBytes,
        CancellationToken cancellationToken = default);

    Task CommitAsync(
        string stagingKey,
        string storageKey,
        long expectedSizeBytes,
        string expectedSha256,
        CancellationToken cancellationToken = default);

    Task<ArtifactStoredObjectInfo?> GetInfoAsync(
        string storageKey,
        CancellationToken cancellationToken = default);

    Task<Stream?> OpenReadAsync(
        string storageKey,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        string storageKey,
        CancellationToken cancellationToken = default);

    Task DeleteStagingAsync(
        string stagingKey,
        CancellationToken cancellationToken = default);

    Task<int> CleanupStagingAsync(
        DateTimeOffset olderThanUtc,
        int maxItems,
        CancellationToken cancellationToken = default);
}

public sealed class ArtifactTooLargeException : Exception
{
    public ArtifactTooLargeException() : base("Artifact exceeds the configured size limit.")
    {
    }
}

public sealed class ArtifactStoreIntegrityException(string message)
    : Exception(message);
