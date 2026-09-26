using ICEHOTT.Domain.Workflows;

namespace ICEHOTT.Application.Abstractions;

public interface IArtifactRepository
{
    Task AddAsync(
        Artifact artifact,
        CancellationToken cancellationToken = default);

    Task<Artifact?> FindAsync(
        Guid workspaceId,
        Guid artifactId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Artifact>> ListAsync(
        Guid workspaceId,
        int limit,
        CancellationToken cancellationToken = default);
}
