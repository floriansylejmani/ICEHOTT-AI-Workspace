using ICEHOTT.Application.Abstractions;
using ICEHOTT.Domain.Knowledge;
using Microsoft.EntityFrameworkCore;

namespace ICEHOTT.Persistence.Repositories;

public sealed class EmbeddingProfileRepository(ICEHOTTDbContext db) : IEmbeddingProfileRepository
{
    public Task<EmbeddingProfile?> GetActiveAsync(CancellationToken cancellationToken = default) =>
        db.EmbeddingProfiles
            .FirstOrDefaultAsync(p => p.Status == EmbeddingProfileStatus.Active, cancellationToken);

    public Task<EmbeddingProfile?> GetBuildingAsync(CancellationToken cancellationToken = default) =>
        db.EmbeddingProfiles
            .FirstOrDefaultAsync(p => p.Status == EmbeddingProfileStatus.Building, cancellationToken);

    public async Task<EmbeddingProfile?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        await db.EmbeddingProfiles.FindAsync([id], cancellationToken);

    public async Task AddAsync(EmbeddingProfile profile, CancellationToken cancellationToken = default) =>
        await db.EmbeddingProfiles.AddAsync(profile, cancellationToken);
}
