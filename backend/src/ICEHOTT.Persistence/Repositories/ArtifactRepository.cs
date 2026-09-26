using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Workflows;
using Microsoft.EntityFrameworkCore;

namespace ICEHOTT.Persistence.Repositories;

public sealed class ArtifactRepository(ICEHOTTDbContext db) : IArtifactRepository
{
    public Task AddAsync(
        Artifact artifact,
        CancellationToken cancellationToken = default) =>
        db.Artifacts.AddAsync(artifact, cancellationToken).AsTask();

    public Task<Artifact?> FindAsync(
        Guid workspaceId,
        Guid artifactId,
        CancellationToken cancellationToken = default) =>
        db.Artifacts.SingleOrDefaultAsync(
            x => x.WorkspaceId == workspaceId && x.Id == artifactId,
            cancellationToken);
    public async Task<IReadOnlyList<Artifact>> ListAsync(
        Guid workspaceId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var capped = Math.Clamp(limit, 1, 200);
        var query = db.Artifacts.AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId);

        if (db.Database.ProviderName?.Contains(
                "Sqlite",
                StringComparison.OrdinalIgnoreCase) == true)
        {
            var items = await query.ToListAsync(cancellationToken);
            return items.OrderByDescending(x => x.CreatedAtUtc)
                .Take(capped)
                .ToArray();
        }

        return await query.OrderByDescending(x => x.CreatedAtUtc)
            .Take(capped)
            .ToListAsync(cancellationToken);
    }
}
