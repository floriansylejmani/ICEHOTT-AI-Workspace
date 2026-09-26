using ICEHOTT.Domain.Workflows;

namespace ICEHOTT.Application.Abstractions;

public sealed record ArtifactWorkspaceUsage(
    int ActiveArtifactCount,
    long ActiveBytes);

public enum ArtifactAddOutcome
{
    Added = 1,
    QuotaExceeded = 2,
    IdempotencyExists = 3,
    WorkspaceMissing = 4
}

public interface IArtifactRepository
{
    Task AddAsync(
        Artifact artifact,
        CancellationToken cancellationToken = default);

    Task<ArtifactAddOutcome> TryAddWithinQuotaAsync(
        Artifact artifact,
        int maxArtifactCount,
        long maxWorkspaceBytes,
        CancellationToken cancellationToken = default);

    Task<Artifact?> FindAsync(
        Guid workspaceId,
        Guid artifactId,
        CancellationToken cancellationToken = default);

    Task<Artifact?> FindByIdempotencyKeyAsync(
        Guid workspaceId,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Artifact>> ListAsync(
        Guid workspaceId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<ArtifactWorkspaceUsage> GetUsageAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Artifact>> ListMaintenanceCandidatesAsync(
        DateTimeOffset pendingOlderThanUtc,
        int limit,
        CancellationToken cancellationToken = default);
}
